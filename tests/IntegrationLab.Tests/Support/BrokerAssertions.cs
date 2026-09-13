using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Integration.Shared.Messaging;
using RabbitMQ.Client;

namespace IntegrationLab.Tests.Support;

public sealed record QueueStats(int MessagesReady, int MessagesUnacknowledged);

public sealed record BrokerMessage(string? MessageId, string? CorrelationId, string? Type, byte[] Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Test-side broker operations through the management HTTP API (depths, bindings, purge)
/// and a direct AMQP connection (publishing crafted messages, draining the DLQ for
/// inspection). DLQ draining ACKs are test/debug inspection only - they are never part
/// of the application's normal flow.
/// </summary>
public sealed class BrokerAssertions : IAsyncDisposable
{
    private readonly HttpClient _management;
    private readonly IConnection _amqp;

    public string EventsExchange { get; }

    public string ExportQueue { get; }

    public string DeadExchange { get; }

    public string DeadQueue { get; }

    private BrokerAssertions(HttpClient management, IConnection amqp, string eventsExchange, string exportQueue, string deadExchange, string deadQueue)
    {
        _management = management;
        _amqp = amqp;
        EventsExchange = eventsExchange;
        ExportQueue = exportQueue;
        DeadExchange = deadExchange;
        DeadQueue = deadQueue;
    }

    public static async Task<BrokerAssertions> CreateAsync(LabFixture fixture)
    {
        var management = new HttpClient
        {
            BaseAddress = new Uri($"http://{fixture.RabbitHostName}:{fixture.RabbitManagementPort}/api/"),
        };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{fixture.RabbitUserName}:{fixture.RabbitPassword}"));
        management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        var prefix = fixture.RabbitNamePrefix;
        var factory = new ConnectionFactory
        {
            HostName = fixture.RabbitHostName,
            Port = fixture.RabbitAmqpPort,
            UserName = fixture.RabbitUserName,
            Password = fixture.RabbitPassword,
            VirtualHost = "/",
        };
        var amqp = await factory.CreateConnectionAsync("integration.lab.tests");

        return new BrokerAssertions(
            management,
            amqp,
            prefix + Topology.EventsExchangeSuffix,
            prefix + Topology.ExportQueueSuffix,
            prefix + Topology.DeadExchangeSuffix,
            prefix + Topology.DeadQueueSuffix);
    }

    public async Task<QueueStats> GetQueueStatsAsync(string queue)
    {
        using var response = await _management.GetAsync($"queues/%2F/{Uri.EscapeDataString(queue)}");
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;
        return new QueueStats(
            root.TryGetProperty("messages_ready", out var ready) ? ready.GetInt32() : 0,
            root.TryGetProperty("messages_unacknowledged", out var unacknowledged) ? unacknowledged.GetInt32() : 0);
    }

    /// <summary>
    /// Empties a queue, treating "the queue does not exist yet" as already empty: the topology
    /// is declared by the applications, so a test that purges before starting a worker would
    /// otherwise fail on a 404 that means exactly what it wanted.
    /// </summary>
    public async Task PurgeQueueAsync(string queue)
    {
        using var response = await _management.DeleteAsync($"queues/%2F/{Uri.EscapeDataString(queue)}/contents");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Depths of a queue that may not exist yet; an undeclared queue reads as empty.</summary>
    public async Task<QueueStats> GetQueueStatsOrEmptyAsync(string queue)
    {
        using var response = await _management.GetAsync($"queues/%2F/{Uri.EscapeDataString(queue)}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new QueueStats(0, 0);
        }

        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;
        return new QueueStats(
            root.TryGetProperty("messages_ready", out var ready) ? ready.GetInt32() : 0,
            root.TryGetProperty("messages_unacknowledged", out var unacknowledged) ? unacknowledged.GetInt32() : 0);
    }

    public async Task<bool> HasBindingAsync(string queue, string sourceExchange, string routingKey)
    {
        using var response = await _management.GetAsync($"queues/%2F/{Uri.EscapeDataString(queue)}/bindings");
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.EnumerateArray().Any(binding =>
            binding.GetProperty("source").GetString() == sourceExchange
            && binding.GetProperty("routing_key").GetString() == routingKey);
    }

    public async Task RemoveQueueBindingAsync(string queue, string sourceExchange, string routingKey)
    {
        using var listResponse = await _management.GetAsync($"queues/%2F/{Uri.EscapeDataString(queue)}/bindings");
        listResponse.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await listResponse.Content.ReadAsStreamAsync());
        foreach (var binding in document.RootElement.EnumerateArray())
        {
            if (binding.GetProperty("source").GetString() == sourceExchange
                && binding.GetProperty("routing_key").GetString() == routingKey)
            {
                // The {props} value is the LAST path segment itself - there is no literal
                // "props/" segment in the management API's binding delete route.
                var propertiesKey = binding.TryGetProperty("properties_key", out var key)
                    && key.GetString() is { Length: > 0 } value
                        ? value
                        : routingKey;
                using var delete = await _management.DeleteAsync(
                    $"bindings/%2F/e/{Uri.EscapeDataString(sourceExchange)}/q/{Uri.EscapeDataString(queue)}/{Uri.EscapeDataString(propertiesKey)}");
                delete.EnsureSuccessStatusCode();
                return;
            }
        }

        throw new InvalidOperationException($"No binding from '{sourceExchange}' to '{queue}' with key '{routingKey}' was found.");
    }

    /// <summary>(Re-)creates a queue binding through the management API, restoring routability.</summary>
    public async Task BindQueueAsync(string queue, string sourceExchange, string routingKey)
    {
        using var response = await _management.PostAsync(
            $"bindings/%2F/e/{Uri.EscapeDataString(sourceExchange)}/q/{Uri.EscapeDataString(queue)}",
            new StringContent($"{{\"routing_key\":\"{routingKey}\",\"arguments\":{{}}}}", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Publishes a crafted message straight to the exchange - duplicates, poison bodies, custom headers.</summary>
    public async Task PublishAsync(
        string exchange,
        string routingKey,
        ReadOnlyMemory<byte> body,
        string? messageId = null,
        string? type = null,
        string? traceParent = null,
        string? traceState = null)
    {
        var channel = await _amqp.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                outstandingPublisherConfirmationsRateLimiter: null,
                consumerDispatchConcurrency: 1));

        try
        {
            var properties = new BasicProperties
            {
                DeliveryMode = DeliveryModes.Persistent,
                ContentType = "application/json",
                MessageId = messageId,
                Type = type,
                CorrelationId = messageId,
            };
            if (traceParent is not null || traceState is not null)
            {
                properties.Headers = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (traceParent is not null)
                {
                    properties.Headers[MessageProperties.TraceParentHeader] = traceParent;
                }

                if (traceState is not null)
                {
                    properties.Headers[MessageProperties.TraceStateHeader] = traceState;
                }
            }

            await channel.BasicPublishAsync(exchange, routingKey, mandatory: false, properties, body);
        }
        finally
        {
            await channel.CloseAsync(320, "test publisher done", abort: true);
            await channel.DisposeAsync();
        }
    }

    /// <summary>Inspection of the dead-letter queue (auto-ACK basic.get). Test-only by contract.</summary>
    public async Task<List<BrokerMessage>> DrainDeadLetterAsync(int max = 20)
    {
        var channel = await _amqp.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                outstandingPublisherConfirmationsRateLimiter: null,
                consumerDispatchConcurrency: 1));

        try
        {
            var messages = new List<BrokerMessage>();
            while (messages.Count < max)
            {
                var result = await channel.BasicGetAsync(DeadQueue, autoAck: true);
                if (result is null)
                {
                    break;
                }

                var headers = new Dictionary<string, string>(StringComparer.Ordinal);
                if (result.BasicProperties.Headers is { } rawHeaders)
                {
                    foreach (var (name, value) in rawHeaders)
                    {
                        if (value is string text)
                        {
                            headers[name] = text;
                        }
                        else if (value is byte[] bytes)
                        {
                            headers[name] = Encoding.UTF8.GetString(bytes);
                        }
                    }
                }

                messages.Add(new BrokerMessage(
                    result.BasicProperties.MessageId,
                    result.BasicProperties.CorrelationId,
                    result.BasicProperties.Type,
                    result.Body.ToArray(),
                    headers));
            }

            return messages;
        }
        finally
        {
            await channel.CloseAsync(320, "test inspection done", abort: true);
            await channel.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _management.Dispose();
        await _amqp.CloseAsync();
        await _amqp.DisposeAsync();
    }
}
