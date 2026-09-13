using Xunit.Sdk;

namespace IntegrationLab.Tests.Support;

/// <summary>
/// Condition polling with a timeout instead of random sleeps. When the timeout expires,
/// the failure message carries the requestId-scoped SQL state so a red test explains
/// WHERE the flow stopped - not just that it did.
/// </summary>
public static class Eventually
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public static async Task UntilAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        Func<Task<string>>? diagnostics = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(45));
        while (true)
        {
            if (await condition())
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                var state = diagnostics is not null ? await diagnostics() : "no diagnostics provided";
                throw new XunitException($"Condition was not met within {timeout ?? TimeSpan.FromSeconds(45)}. {state}");
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }
}
