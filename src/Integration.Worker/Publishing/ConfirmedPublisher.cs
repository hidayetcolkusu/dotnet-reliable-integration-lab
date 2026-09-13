using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Integration.Shared.Diagnostics;
using Integration.Shared.Messaging;
using Integration.Shared.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Integration.Worker.Publishing;

/// <summary>
/// Outcome of one publish attempt. Only <see cref="Confirmed"/> means the broker both
/// accepted AND routed the message; everything else leaves the outbox row retryable.
/// </summary>
public enum PublishOutcome
{
    Confirmed,
    Returned,
    Nacked,
    TimedOut,
    Failed,
}

/// <summary>
/// The publishing seam the dispatcher depends on. The real implementation talks to the
/// broker; tests use it to prove how the dispatcher classifies nack/timeout outcomes
/// without having to force a broker-internal failure on a healthy node.
/// </summary>
public interface IConfirmedPublisher
{
    Task<PublishOutcome> PublishAsync(OutboxPublishRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A publisher channel owned by exactly one sender (the OutboxDispatcher loop).
///
/// Publisher confirms are enabled at channel creation; every publish is tracked by its
/// sequence number and completed by the channel's ack/nack events. basic.return is matched
/// through the MessageId (= outbox row id), and a returned message is never Published even
/// if a confirm follows it. A confirm that never arrives within the configured timeout is
/// treated as unknown - and therefore retryable, which may produce a duplicate delivery.
/// That duplicate is exactly what the inbox is there to absorb.
/// </summary>
public sealed class ConfirmedPublisher : IConfirmedPublisher, IAsyncDisposable
{
    private readonly RabbitConnection _connection;
    private readonly Topology _topology;
    private readonly LabOptions _options;
    private readonly ILogger<ConfirmedPublisher> _logger;

    private readonly ConcurrentDictionary<ulong, InFlightPublish> _inFlightByTag = new();
    private readonly ConcurrentDictionary<string, InFlightPublish> _inFlightByMessageId = new(StringComparer.Ordinal);
    private IChannel? _channel;
    private bool _disposed;

    public ConfirmedPublisher(
        RabbitConnection connection,
        Topology topology,
        IOptions<LabOptions> options,
        ILogger<ConfirmedPublisher> logger)
    {
        _connection = connection;
        _topology = topology;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PublishOutcome> PublishAsync(OutboxPublishRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var channel = await GetOrCreateChannelAsync(cancellationToken).ConfigureAwait(false);

        var completion = new TaskCompletionSource<PublishOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new InFlightPublish(request.OutboxId.ToString(), completion);
        _inFlightByMessageId[request.OutboxId.ToString()] = entry;

        // The whole broker interaction - writing the frame and waiting for its confirm - shares
        // one budget. A frozen broker accepts bytes into the kernel buffer and answers nothing,
        // so an unbounded wait anywhere here would park the dispatcher forever.
        var confirmTimeout = TimeSpan.FromSeconds(_options.ConfirmTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(confirmTimeout);

        ulong sequence;
        try
        {
            sequence = await channel.GetNextPublishSequenceNumberAsync(timeoutCts.Token).ConfigureAwait(false);
            _inFlightByTag[sequence] = entry;

            var properties = MessageProperties.CreatePublishProperties(
                request.OutboxId,
                request.CorrelationId,
                request.Type,
                request.TraceParent,
                request.TraceState);

            using var activity = LabTelemetry.StartLinkedTo(
                request.SpanName,
                request.TraceParent,
                request.TraceState,
                ActivityKind.Producer);
            activity?.SetTag(LabTelemetry.Tags.OutboxId, request.OutboxId.ToString());
            activity?.SetTag(LabTelemetry.Tags.TransportMessageId, request.OutboxId.ToString());
            if (request.RequestId is { } requestId)
            {
                activity?.SetTag(LabTelemetry.Tags.RequestId, requestId.ToString());
            }

            await channel.BasicPublishAsync(
                request.Exchange,
                request.RoutingKey,
                mandatory: true,
                properties,
                Encoding.UTF8.GetBytes(request.Body).AsMemory(),
                timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Forget(entry, sequenceGuess: null);
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Outbox {OutboxId} could not even be written to the broker within {Seconds}s; treating the publish as unknown.",
                request.OutboxId,
                _options.ConfirmTimeoutSeconds);
            Forget(entry, sequenceGuess: null);
            await InvalidateChannelAsync().ConfigureAwait(false);
            return PublishOutcome.TimedOut;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Publish of outbox {OutboxId} failed before leaving the channel: {Error}",
                request.OutboxId,
                SafeErrors.Describe(ex));
            Forget(entry, sequenceGuess: null);
            await InvalidateChannelAsync().ConfigureAwait(false);
            return PublishOutcome.Failed;
        }

        try
        {
            return await entry.Completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "No broker confirm for outbox {OutboxId} within {Seconds}s; treating the publish as unknown.",
                request.OutboxId,
                _options.ConfirmTimeoutSeconds);
            Forget(entry, sequence);
            return PublishOutcome.TimedOut;
        }
        catch (OperationCanceledException)
        {
            Forget(entry, sequence);
            throw;
        }
    }

    private async Task<IChannel> GetOrCreateChannelAsync(CancellationToken cancellationToken)
    {
        var existing = _channel;
        if (existing is { IsOpen: true })
        {
            return existing;
        }

        await InvalidateChannelAsync().ConfigureAwait(false);

        var connection = await _connection
            .GetConnectionAsync("integration.worker.publisher", cancellationToken)
            .ConfigureAwait(false);

        // Confirms on, but the CLIENT's own confirm tracking deliberately off. With tracking on,
        // BasicPublishAsync itself waits for the confirm - unbounded, so a frozen broker parks
        // the dispatcher - and it surfaces nack and basic.return as one PublishException, which
        // collapses two outcomes this lab must tell apart. Tracking the sequence number and the
        // return (by MessageId) here keeps both the timeout and the classification ours.
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: false,
                outstandingPublisherConfirmationsRateLimiter: null,
                consumerDispatchConcurrency: 1),
            cancellationToken).ConfigureAwait(false);

        // Idempotent re-declaration on every (re)connect.
        await _topology.DeclareAsync(channel, cancellationToken).ConfigureAwait(false);

        channel.BasicAcksAsync += OnBasicAcksAsync;
        channel.BasicNacksAsync += OnBasicNacksAsync;
        channel.BasicReturnAsync += OnBasicReturnAsync;
        channel.ChannelShutdownAsync += OnChannelShutdownAsync;

        _channel = channel;
        _logger.LogDebug("Publisher channel open; topology declared.");
        return channel;
    }

    private Task OnBasicAcksAsync(object sender, BasicAckEventArgs args)
    {
        if (args.Multiple)
        {
            foreach (var tag in _inFlightByTag.Keys.Where(tag => tag <= args.DeliveryTag).ToList())
            {
                CompleteByTag(tag, PublishOutcome.Confirmed);
            }
        }
        else
        {
            CompleteByTag(args.DeliveryTag, PublishOutcome.Confirmed);
        }

        return Task.CompletedTask;
    }

    private Task OnBasicNacksAsync(object sender, BasicNackEventArgs args)
    {
        if (args.Multiple)
        {
            foreach (var tag in _inFlightByTag.Keys.Where(tag => tag <= args.DeliveryTag).ToList())
            {
                CompleteByTag(tag, PublishOutcome.Nacked);
            }
        }
        else
        {
            CompleteByTag(args.DeliveryTag, PublishOutcome.Nacked);
        }

        return Task.CompletedTask;
    }

    private Task OnBasicReturnAsync(object sender, BasicReturnEventArgs args)
    {
        var messageId = args.BasicProperties?.MessageId;
        if (messageId is not null && _inFlightByMessageId.TryGetValue(messageId, out var entry))
        {
            _logger.LogWarning(
                "Broker returned message {MessageId} as unroutable ({ReplyCode} {ReplyText}); it will not be Published.",
                messageId,
                args.ReplyCode,
                args.ReplyText);
            entry.MarkReturned();
        }

        return Task.CompletedTask;
    }

    private async Task OnChannelShutdownAsync(object sender, ShutdownEventArgs args)
    {
        _logger.LogWarning("Publisher channel shut down ({ReplyText}); failing in-flight publishes.", args.ReplyText);
        await InvalidateChannelAsync().ConfigureAwait(false);
    }

    private void CompleteByTag(ulong tag, PublishOutcome confirmedOutcome)
    {
        if (!_inFlightByTag.TryRemove(tag, out var entry))
        {
            return;
        }

        _inFlightByMessageId.TryRemove(entry.MessageId, out _);
        // basic.return for a publish always precedes its basic.ack on the same channel,
        // so by the time the ack arrives the Returned flag is already set when applicable.
        entry.Completion.TrySetResult(entry.ConsumedReturn() ? PublishOutcome.Returned : confirmedOutcome);
    }

    private void Forget(InFlightPublish entry, ulong? sequenceGuess)
    {
        _inFlightByMessageId.TryRemove(entry.MessageId, out _);
        if (sequenceGuess is { } tag)
        {
            _inFlightByTag.TryRemove(tag, out _);
        }
        else
        {
            foreach (var pair in _inFlightByTag.Where(p => ReferenceEquals(p.Value, entry)).ToList())
            {
                _inFlightByTag.TryRemove(pair.Key, out _);
            }
        }

        entry.Completion.TrySetCanceled();
    }

    private async Task InvalidateChannelAsync()
    {
        var channel = Interlocked.Exchange(ref _channel, null);
        if (channel is not null)
        {
            channel.BasicAcksAsync -= OnBasicAcksAsync;
            channel.BasicNacksAsync -= OnBasicNacksAsync;
            channel.BasicReturnAsync -= OnBasicReturnAsync;
            channel.ChannelShutdownAsync -= OnChannelShutdownAsync;

            foreach (var entry in _inFlightByTag.Values)
            {
                entry.Completion.TrySetResult(PublishOutcome.Failed);
            }

            _inFlightByTag.Clear();
            _inFlightByMessageId.Clear();

            try
            {
                if (channel.IsOpen)
                {
                    await channel.CloseAsync(
                        320,
                        "publisher channel reset",
                        abort: true).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Ignoring error while closing publisher channel: {Error}", ex.GetType().Name);
            }
            finally
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await InvalidateChannelAsync().ConfigureAwait(false);
    }

    private sealed class InFlightPublish(string messageId, TaskCompletionSource<PublishOutcome> completion)
    {
        private int _returned;

        public string MessageId { get; } = messageId;

        public TaskCompletionSource<PublishOutcome> Completion { get; } = completion;

        public void MarkReturned() => Interlocked.Exchange(ref _returned, 1);

        public bool ConsumedReturn() => Volatile.Read(ref _returned) == 1;
    }
}

/// <summary>Everything the publisher needs for one outbox row, already claimed.</summary>
public sealed record OutboxPublishRequest(
    Guid OutboxId,
    Guid? RequestId,
    Guid CorrelationId,
    string Type,
    string Exchange,
    string RoutingKey,
    string Body,
    string? TraceParent,
    string? TraceState,
    string SpanName);
