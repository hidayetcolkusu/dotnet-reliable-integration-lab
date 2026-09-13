using Integration.Shared.Diagnostics;
using Integration.Shared.Messaging;
using Integration.Shared.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Integration.Worker.Consuming;

/// <summary>
/// Hosts the queue consumer with manual ACK. The rule the whole lab hinges on:
/// ACK happens only after the inbox (or quarantine) transaction committed, and the ACK
/// goes to the delivery's own consumer channel - never the publisher's channel.
///
/// When SQL is unreachable, nothing is ACKed; the channel is closed in a controlled way
/// and a bounded backoff reconnects, so the broker redelivers the still-unaccepted work.
/// A poison message is never BasicReject(requeue:false)-dropped: it is quarantined
/// durably and then ACKed.
/// </summary>
public sealed class InboxConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitConnection _connection;
    private readonly Topology _topology;
    private readonly IFaultHooks _faultHooks;
    private readonly LabOptions _options;
    private readonly ILogger<InboxConsumer> _logger;
    private readonly TimeProvider _timeProvider;

    public InboxConsumer(
        IServiceScopeFactory scopeFactory,
        RabbitConnection connection,
        Topology topology,
        IFaultHooks faultHooks,
        IOptions<LabOptions> options,
        ILogger<InboxConsumer> logger,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _connection = connection;
        _topology = topology;
        _faultHooks = faultHooks;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = _connection.CreateBackoff();
        _logger.LogInformation("Inbox consumer running on queue {Queue}.", _topology.ExportQueue);

        while (!stoppingToken.IsCancellationRequested)
        {
            IChannel? channel = null;
            var channelFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var connection = await _connection
                    .GetConnectionAsync("integration.worker.inbox", stoppingToken)
                    .ConfigureAwait(false);

                channel = await connection.CreateChannelAsync(
                    new CreateChannelOptions(
                        publisherConfirmationsEnabled: false,
                        publisherConfirmationTrackingEnabled: false,
                        outstandingPublisherConfirmationsRateLimiter: null,
                        consumerDispatchConcurrency: 1),
                    stoppingToken).ConfigureAwait(false);

                await _topology.DeclareAsync(channel, stoppingToken).ConfigureAwait(false);
                await channel.BasicQosAsync(0, _options.PrefetchCount, global: false, stoppingToken)
                    .ConfigureAwait(false);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ShutdownAsync += (_, _) =>
                {
                    channelFailed.TrySetResult();
                    return Task.CompletedTask;
                };
                consumer.ReceivedAsync += (_, eventArgs) =>
                    HandleDeliveryAsync(channel, eventArgs, channelFailed);

                await channel.BasicConsumeAsync(
                    _topology.ExportQueue,
                    autoAck: false,
                    consumerTag: string.Empty,
                    noLocal: false,
                    exclusive: false,
                    arguments: null,
                    consumer,
                    stoppingToken).ConfigureAwait(false);

                _logger.LogInformation("Inbox consumer subscribed; prefetch {Prefetch}.", _options.PrefetchCount);

                using var stopLinked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var stopped = stopLinked.Token;
                _ = stopLinked.Token.Register(() => channelFailed.TrySetResult());

                await Task.WhenAny(
                    Task.Delay(Timeout.InfiniteTimeSpan, _timeProvider, stopped),
                    channelFailed.Task).ConfigureAwait(false);

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogWarning("Inbox channel ended or failed; reconnecting with backoff.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Inbox consume loop failed: {Error}. Reconnecting with backoff.",
                    SafeErrors.Describe(ex));
            }
            finally
            {
                if (channel is not null)
                {
                    await SafeCloseAsync(channel).ConfigureAwait(false);
                    await channel.DisposeAsync().ConfigureAwait(false);
                }
            }

            try
            {
                await Task.Delay(backoff.NextDelay(), _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Inbox consumer stopped.");
    }

    private async Task HandleDeliveryAsync(
        IChannel channel,
        BasicDeliverEventArgs eventArgs,
        TaskCompletionSource channelFailed)
    {
        // Copy the payload before anything async: the broker memory is only valid
        // for the lifetime of this callback.
        var body = eventArgs.Body.ToArray();
        var transportId = eventArgs.BasicProperties.MessageId;

        try
        {
            var outcome = await AcceptInScopeAsync(transportId, body, eventArgs.BasicProperties)
                .ConfigureAwait(false);

            if (outcome is AcceptOutcome.Accepted or AcceptOutcome.Quarantined)
            {
                // Crash window the tests exercise: durably accepted, not yet ACKed.
                await _faultHooks.ReachAsync(FaultPoints.InboxAfterCommitBeforeAck, CancellationToken.None)
                    .ConfigureAwait(false);

                await channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            // AcceptOutcome.Failed: nothing durable was written, so nothing may be ACKed.
            // Close the channel; the broker requeues the unaccepted delivery.
            _logger.LogWarning(
                "Delivery {TransportMessageId} was not durably accepted; closing the channel without ACK.",
                transportId ?? "<missing>");
            channelFailed.TrySetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Delivery {TransportMessageId} processing failed: {Error}; no ACK, channel will reconnect.",
                transportId ?? "<missing>",
                SafeErrors.Describe(ex));
            channelFailed.TrySetResult();
        }
    }

    private async Task<AcceptOutcome> AcceptInScopeAsync(
        string? transportId,
        byte[] body,
        IReadOnlyBasicProperties properties)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var acceptor = scope.ServiceProvider.GetRequiredService<InboxAcceptor>();
        return await acceptor.AcceptAsync(transportId, body, properties, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task SafeCloseAsync(IChannel channel)
    {
        try
        {
            if (channel.IsOpen)
            {
                await channel.CloseAsync(320, "inbox consumer reset", abort: true).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Ignoring error while closing inbox channel: {Error}", ex.GetType().Name);
        }
    }
}
