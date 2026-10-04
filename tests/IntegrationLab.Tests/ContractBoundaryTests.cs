using System.Text;
using System.Text.Json;
using Integration.Shared.Contracts;
using Integration.Shared.Messaging;
using Integration.Shared.Persistence;
using Integration.Shared.Persistence.Entities;
using Integration.Shared.Runtime;
using Integration.Worker.Consuming;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Xunit;

namespace IntegrationLab.Tests;

/// <summary>
/// The boundaries that have to hold BEFORE anything durable is written: a value the database
/// cannot store must become a reasoned rejection at the edge, never a SQL error inside a
/// transaction that nobody can ACK their way out of.
///
/// Pure validation, no SQL and no broker, so these stay outside the "lab" collection. The
/// end-to-end consequences of the same rules (queue drains, one rejection, zero jobs) are
/// proved against real dependencies in <see cref="InboxTests"/> and <see cref="TracingTests"/>.
/// </summary>
public sealed class ContractBoundaryTests
{
    private static readonly MessageValidator Validator = new(Options.Create(new LabOptions()));

    private static ReadOnlyMemory<byte> ValidEnvelope(Guid requestId, string amountLiteral = "160.00")
    {
        var json =
            "{\"eventId\":\"" + requestId.ToString("D") + "\"," +
            "\"type\":\"OrderExportRequested\",\"schemaVersion\":1," +
            "\"occurredAtUtc\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"," +
            "\"data\":{\"requestId\":\"" + requestId.ToString("D") + "\"," +
            "\"externalReference\":\"PO-100\",\"amount\":" + amountLiteral + ",\"currency\":\"TRY\"}}";
        return Encoding.UTF8.GetBytes(json);
    }

    // ------------------------------------------------------- transport identity limits

    [Fact]
    public void AnIdentityThatExactlyFillsTheColumnIsAccepted()
    {
        var id = new string('a', ColumnLengths.TransportMessageId);

        Assert.True(MessageValidator.IsStorableTransportMessageId(id));
        Assert.True(Validator.Validate(id, ValidEnvelope(Guid.NewGuid())).Valid);
    }

    [Theory]
    [InlineData(129)]
    [InlineData(255)]
    [InlineData(4096)]
    public void AnIdentityLongerThanTheColumnIsQuarantinedRatherThanTruncated(int length)
    {
        var id = new string('a', length);
        var result = Validator.Validate(id, ValidEnvelope(Guid.NewGuid()));

        // Truncating would be the dangerous alternative: two different deliveries would then
        // collide on one InboxReceipts primary key and the second one would look like a replay.
        Assert.False(result.Valid);
        Assert.Equal(RejectionReason.UnstorableMessageId, result.ReasonCode);
    }

    [Theory]
    [InlineData("mesaj-kimliği-ç")]
    [InlineData("メッセージ")]
    [InlineData("id\u0000with-nul")]
    [InlineData("id\twith-tab")]
    public void AnIdentityTheNonUnicodeColumnCannotCarryIsQuarantined(string id)
    {
        // The column is varchar: SQL Server substitutes unmappable characters silently, so the
        // stored identity would no longer be the identity that arrived.
        var result = Validator.Validate(id, ValidEnvelope(Guid.NewGuid()));

        Assert.False(result.Valid);
        Assert.Equal(RejectionReason.UnstorableMessageId, result.ReasonCode);
    }

    [Fact]
    public void AMissingIdentityStillReportsItsOwnReason()
    {
        // The new rule must not swallow the existing one: "absent" and "unstorable" are
        // different operator-facing facts.
        Assert.Equal(
            RejectionReason.MissingMessageId,
            Validator.Validate(null, ValidEnvelope(Guid.NewGuid())).ReasonCode);
        Assert.Equal(
            RejectionReason.MissingMessageId,
            Validator.Validate("   ", ValidEnvelope(Guid.NewGuid())).ReasonCode);
    }

    // ------------------------------------------------------------------ amount limits

    [Fact]
    public void TheLargestStorableAmountIsAccepted()
    {
        Assert.True(ExportLimits.IsStorableAmount(ExportLimits.MaxAmount));
        Assert.True(Validator.Validate(
            Guid.NewGuid().ToString(),
            ValidEnvelope(Guid.NewGuid(), "9999999999999999.99")).Valid);
    }

    [Theory]
    // One step past decimal(18,2) - a perfectly ordinary CLR decimal, and a SQL overflow.
    [InlineData("10000000000000000.00")]
    [InlineData("79228162514264337593543950335")]
    public void AnAmountLargerThanTheColumnIsRejectedByTheValidatorNotBySqlServer(string literal)
    {
        var result = Validator.Validate(Guid.NewGuid().ToString(), ValidEnvelope(Guid.NewGuid(), literal));

        Assert.False(result.Valid);
        Assert.Equal(RejectionReason.InvalidPayload, result.ReasonCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TheExistingPositivityAndPrecisionRulesStillHold(decimal amount)
    {
        Assert.False(ExportLimits.IsStorableAmount(amount));
        Assert.False(ExportLimits.IsStorableAmount(160.005m));
        Assert.True(ExportLimits.IsStorableAmount(160.00m));
    }

    // ------------------------------------------------------------------- trace context

    private static IReadOnlyBasicProperties WithTrace(string? traceParent, string? traceState)
    {
        var properties = new BasicProperties();
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (traceParent is not null)
        {
            headers[MessageProperties.TraceParentHeader] = traceParent;
        }

        if (traceState is not null)
        {
            headers[MessageProperties.TraceStateHeader] = traceState;
        }

        properties.Headers = headers;
        return properties;
    }

    private const string ValidTraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    [Fact]
    public void AValidTraceContextSurvivesUnchanged()
    {
        var (parent, state) = MessageProperties.ReadTraceContext(WithTrace(ValidTraceParent, "vendor=abc"));

        Assert.Equal(ValidTraceParent, parent);
        Assert.Equal("vendor=abc", state);
    }

    [Fact]
    public void AnOversizedTraceStateIsDroppedWithoutCostingTheParentLink()
    {
        // tracestate is vendor diagnostics. Losing it must not cost the trace linkage, and it
        // certainly must not fail the INSERT that carries the actual work.
        var oversized = "vendor=" + new string('x', ColumnLengths.TraceState + 1);
        var (parent, state) = MessageProperties.ReadTraceContext(WithTrace(ValidTraceParent, oversized));

        Assert.Equal(ValidTraceParent, parent);
        Assert.Null(state);
    }

    [Fact]
    public void AMalformedTraceStateIsDroppedWithoutCostingTheParentLink()
    {
        var (parent, state) = MessageProperties.ReadTraceContext(
            WithTrace(ValidTraceParent, "this is not a valid tracestate list"));

        Assert.Equal(ValidTraceParent, parent);
        Assert.Null(state);
    }

    [Fact]
    public void ATraceStateWithoutAParentIsDroppedEntirely()
    {
        // Vendor state with nothing to attach it to is not diagnostics, it is just a value that
        // would be written to a column and never read.
        var (parent, state) = MessageProperties.ReadTraceContext(WithTrace(null, "vendor=abc"));

        Assert.Null(parent);
        Assert.Null(state);
    }

    [Fact]
    public void AnOversizedOrMalformedTraceParentDropsBoth()
    {
        var (longParent, longState) = MessageProperties.ReadTraceContext(
            WithTrace(new string('0', ColumnLengths.TraceParent + 1), "vendor=abc"));
        Assert.Null(longParent);
        Assert.Null(longState);

        var (badParent, badState) = MessageProperties.ReadTraceContext(
            WithTrace("definitely-not-a-traceparent", "vendor=abc"));
        Assert.Null(badParent);
        Assert.Null(badState);
    }

    [Fact]
    public void EveryTraceValueThatSurvivesFitsItsColumn()
    {
        // The property this normalisation exists for, stated directly.
        foreach (var candidate in new[]
        {
            (Parent: ValidTraceParent, State: (string?)null),
            (Parent: ValidTraceParent, State: "vendor=abc"),
            (Parent: ValidTraceParent, State: "vendor=" + new string('x', 1000)),
            (Parent: new string('0', 500), State: "vendor=abc"),
            (Parent: "garbage", State: new string('y', 5000)),
        })
        {
            var (parent, state) = MessageProperties.ReadTraceContext(WithTrace(candidate.Parent, candidate.State));
            Assert.True(parent is null || parent.Length <= ColumnLengths.TraceParent);
            Assert.True(state is null || state.Length <= ColumnLengths.TraceState);
            Assert.True(state is null || parent is not null);
        }
    }

    // ------------------------------------------------------------------- envelope round-trip

    [Fact]
    public void AWellFormedEnvelopeStillValidatesEndToEnd()
    {
        // A guard against over-tightening: the ordinary message the whole lab moves must still
        // pass every rule above.
        var requestId = Guid.NewGuid();
        var envelope = new ExportEnvelope(
            requestId,
            MessageTypes.OrderExportRequested,
            MessageTypes.SchemaVersion,
            DateTimeOffset.UtcNow,
            new ExportRequest(requestId, "PO-100", 160.00m, "TRY"));

        var result = Validator.Validate(
            Guid.NewGuid().ToString(),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, LabJson.Options)));

        Assert.True(result.Valid, result.ReasonCode);
        Assert.Equal(requestId, result.Delivery!.Request.RequestId);
    }
}
