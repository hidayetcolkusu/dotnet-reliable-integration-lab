using Integration.Shared.Runtime;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Integration.Shared.Messaging;

/// <summary>
/// The broker topology this lab owns. Durable direct exchanges and explicitly classic durable
/// queues. No auto-delete, no exclusive, no TTL, no queue expiry, no drop-head max-length and no
/// automatic DLX: terminal messages reach the dead exchange through the application outbox, so
/// republish and ACK reliability stay in one mechanism.
///
/// A single broker node proves nothing about high availability. Quorum queues are deliberately not
/// used here - their delivery-limit would drop a message during a long SQL outage, and the point of
/// this lab is that the inbox table, not the broker, carries the work after ACK.
/// </summary>
public sealed class Topology
{
    public const string EventsExchangeSuffix = "integration.events";
    public const string ExportQueueSuffix = "integration.exports";
    public const string DeadExchangeSuffix = "integration.dead";
    public const string DeadQueueSuffix = "integration.exports.dead";

    public const string ExportRoutingKey = "order.export.requested";
    public const string DeadRoutingKey = "order.export.failed";

    public Topology(IOptions<RabbitOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var prefix = options.Value.NamePrefix ?? string.Empty;
        EventsExchange = prefix + EventsExchangeSuffix;
        ExportQueue = prefix + ExportQueueSuffix;
        DeadExchange = prefix + DeadExchangeSuffix;
        DeadQueue = prefix + DeadQueueSuffix;
    }

    public string EventsExchange { get; }

    public string ExportQueue { get; }

    public string DeadExchange { get; }

    public string DeadQueue { get; }

    private static readonly Dictionary<string, object?> ClassicQueueArguments = new(StringComparer.Ordinal)
    {
        ["x-queue-type"] = "classic",
    };

    /// <summary>
    /// Declares everything. Safe to call again on every reconnect: all declarations are idempotent
    /// as long as the arguments stay identical, which is why they live in exactly one place.
    /// </summary>
    public async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        await channel.ExchangeDeclareAsync(
            EventsExchange, ExchangeType.Direct, durable: true, autoDelete: false,
            arguments: null, cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(
            DeadExchange, ExchangeType.Direct, durable: true, autoDelete: false,
            arguments: null, cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            ExportQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>(ClassicQueueArguments, StringComparer.Ordinal),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            DeadQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>(ClassicQueueArguments, StringComparer.Ordinal),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueBindAsync(
            ExportQueue, EventsExchange, ExportRoutingKey,
            arguments: null, cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueBindAsync(
            DeadQueue, DeadExchange, DeadRoutingKey,
            arguments: null, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
