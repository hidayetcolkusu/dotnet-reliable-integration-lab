using Integration.Shared.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Integration.Shared.Messaging;

/// <summary>
/// One shared AMQP connection with bounded reconnect.
///
/// Automatic recovery only helps once a connection existed; it does not solve a broker that was
/// never reachable at startup. So the caller always loops with <see cref="BoundedBackoff"/> and this
/// class simply returns a live connection or throws.
///
/// Publisher, consumer and any management work take their own channels: channels are not shared
/// across concurrent users in the RabbitMQ .NET client.
/// </summary>
public sealed class RabbitConnection : IAsyncDisposable
{
    private readonly RabbitOptions _options;
    private readonly LabOptions _labOptions;
    private readonly ILogger<RabbitConnection> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private bool _disposed;

    public RabbitConnection(
        IOptions<RabbitOptions> options,
        IOptions<LabOptions> labOptions,
        ILogger<RabbitConnection> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(labOptions);
        _options = options.Value;
        _labOptions = labOptions.Value;
        _logger = logger;
    }

    public BoundedBackoff CreateBackoff() => new(
        TimeSpan.FromMilliseconds(_labOptions.ReconnectBaseDelayMilliseconds),
        TimeSpan.FromMilliseconds(_labOptions.ReconnectMaxDelayMilliseconds));

    public async Task<IConnection> GetConnectionAsync(string clientName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var existing = _connection;
        if (existing is { IsOpen: true })
        {
            return existing;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            if (_connection is not null)
            {
                await SafeCloseAsync(_connection).ConfigureAwait(false);
                _connection = null;
            }

            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                ClientProvidedName = clientName,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = false,
                ConsumerDispatchConcurrency = 1,
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Connected to broker as {ClientName} on vhost {VirtualHost}", clientName, _options.VirtualHost);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the cached connection so the next call builds a fresh one.</summary>
    public async Task InvalidateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                await SafeCloseAsync(_connection).ConfigureAwait(false);
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_connection is not null)
        {
            await SafeCloseAsync(_connection).ConfigureAwait(false);
            _connection = null;
        }

        _gate.Dispose();
    }

    private async Task SafeCloseAsync(IConnection connection)
    {
        try
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Ignoring error while closing a broker connection: {Error}", ex.GetType().Name);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Capped exponential backoff. Bounded, so a retry loop never turns into a busy loop.</summary>
public sealed class BoundedBackoff
{
    private readonly TimeSpan _base;
    private readonly TimeSpan _max;
    private int _failures;

    public BoundedBackoff(TimeSpan baseDelay, TimeSpan maxDelay)
    {
        _base = baseDelay;
        _max = maxDelay;
    }

    public void Reset() => _failures = 0;

    public TimeSpan NextDelay()
    {
        _failures = Math.Min(_failures + 1, 16);
        var scaled = _base * Math.Pow(2, _failures - 1);
        return scaled > _max ? _max : scaled;
    }
}
