using System.Security.Cryptography;

namespace FakeErp;

/// <summary>
/// Test/Development-only scenario control, keyed by requestId and reset between tests.
/// The scenarios shape RESPONSES; the durable effect in AppliedExports is what the
/// correctness guarantees rely on, never these counters.
///
/// Types:
///  - success                  (default) apply and answer immediately;
///  - first-n-503              answer 503 for the first n NEW applies (no external effect);
///  - permanent-422            answer 422 for every NEW apply (no external effect);
///  - apply-then-delay-response apply + commit, then delay the FIRST new response beyond
///                              a typical client timeout; replays answer immediately;
///  - unavailable              never answer (models an unresponsive external system).
/// </summary>
public sealed class ScenarioRegistry
{
    public const string Success = "success";
    public const string FirstN503 = "first-n-503";
    public const string Permanent422 = "permanent-422";
    public const string ApplyThenDelayResponse = "apply-then-delay-response";
    public const string Unavailable = "unavailable";

    private static readonly string[] KnownTypes =
    [
        Success, FirstN503, Permanent422, ApplyThenDelayResponse, Unavailable,
    ];

    public sealed record Scenario(string Type, int N, int DelayMs);

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Scenario> _scenarios = new();
    private readonly Dictionary<Guid, int> _newApplyAttempts = new();

    public static bool IsValidType(string type) => KnownTypes.Contains(type, StringComparer.Ordinal);

    public void Set(Guid requestId, Scenario scenario)
    {
        lock (_gate)
        {
            _scenarios[requestId] = scenario;
            _newApplyAttempts.Remove(requestId);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _scenarios.Clear();
            _newApplyAttempts.Clear();
        }
    }

    public IReadOnlyDictionary<Guid, Scenario> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<Guid, Scenario>(_scenarios);
        }
    }

    /// <summary>
    /// Decides what to do with a NEW apply (no AppliedExports row yet): answer 503,
    /// answer 422, hang, or apply. Returns null = apply normally.
    /// </summary>
    public string? InterceptNewApply(Guid requestId)
    {
        Scenario? scenario;
        lock (_gate)
        {
            if (!_scenarios.TryGetValue(requestId, out scenario))
            {
                return null;
            }

            switch (scenario.Type)
            {
                case FirstN503:
                    var attempts = _newApplyAttempts.GetValueOrDefault(requestId) + 1;
                    _newApplyAttempts[requestId] = attempts;
                    return attempts <= scenario.N ? "http_503" : null;

                case Permanent422:
                    return "http_422";

                case Unavailable:
                    return "hang";

                default:
                    return null;
            }
        }
    }

    /// <summary>How long the FIRST new response should be delayed after the effect committed.</summary>
    public int? TakeResponseDelay(Guid requestId)
    {
        lock (_gate)
        {
            if (_scenarios.TryGetValue(requestId, out var scenario)
                && scenario.Type == ApplyThenDelayResponse
                && !_newApplyAttempts.ContainsKey(requestId))
            {
                // Only the very first new response is delayed; every replay is immediate.
                _newApplyAttempts[requestId] = 1;
                return scenario.DelayMs;
            }
        }

        return null;
    }
}

/// <summary>
/// The external system's own copy of the payload contract. It is intentionally NOT shared
/// with the application: two systems agreeing on a wire contract each own their
/// implementation of it.
/// </summary>
internal static class ErpPayload
{
    public static string Hash(Guid requestId, string externalReference, decimal amount, string currency)
    {
        var canonical =
            $"{requestId:D}|{externalReference.Trim()}|{amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}|{currency.Trim().ToUpperInvariant()}";
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }
}
