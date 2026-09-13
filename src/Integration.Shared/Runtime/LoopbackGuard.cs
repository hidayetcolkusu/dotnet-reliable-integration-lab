using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace Integration.Shared.Runtime;

/// <summary>
/// The lab's web applications are unauthenticated and expose control endpoints. A loopback
/// DEFAULT is not the guarantee the plan asks for: <c>--urls</c>, <c>ASPNETCORE_URLS</c>,
/// <c>ASPNETCORE_HTTP_PORTS</c> and a <c>Kestrel:Endpoints</c> section all override that
/// default silently, and any of them can put the process on a real network interface.
///
/// This guard reads the EFFECTIVE listen configuration before the server binds and refuses to
/// start unless every endpoint is loopback. It adds no authentication and no new product
/// surface: it only makes the documented boundary enforced instead of assumed.
/// </summary>
public static class LoopbackGuard
{
    /// <summary>The configuration key the host reads listen URLs from (also fed by ASPNETCORE_URLS).</summary>
    public const string UrlsKey = "urls";

    /// <summary>Wildcard port keys: they always bind every interface, so they are never allowed.</summary>
    public static readonly string[] WildcardPortKeys = ["http_ports", "https_ports"];

    public const string KestrelEndpointsSection = "Kestrel:Endpoints";

    /// <summary>One configured listen endpoint and the configuration path it came from.</summary>
    public sealed record ConfiguredEndpoint(string Source, string Url);

    /// <summary>
    /// Throws <see cref="LabEnvironmentException"/> when any configured listen endpoint is not
    /// loopback. Called from the composition root, before the host builds its server.
    /// </summary>
    public static void EnsureLoopbackOnly(IConfiguration configuration, string applicationName)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var key in WildcardPortKeys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                throw Refuse(
                    applicationName,
                    key,
                    value,
                    "port-only configuration binds every network interface");
            }
        }

        foreach (var endpoint in EnumerateConfiguredEndpoints(configuration))
        {
            if (!IsLoopbackUrl(endpoint.Url, out var reason))
            {
                throw Refuse(applicationName, endpoint.Source, endpoint.Url, reason);
            }
        }
    }

    /// <summary>
    /// Every listen URL the host would use: the <c>urls</c> key (command line or
    /// <c>ASPNETCORE_URLS</c>) plus each <c>Kestrel:Endpoints:&lt;name&gt;:Url</c>. A single
    /// value may carry several semicolon-separated URLs; each one is returned on its own.
    /// </summary>
    public static IEnumerable<ConfiguredEndpoint> EnumerateConfiguredEndpoints(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var url in Split(configuration[UrlsKey]))
        {
            yield return new ConfiguredEndpoint(UrlsKey, url);
        }

        foreach (var endpoint in configuration.GetSection(KestrelEndpointsSection).GetChildren())
        {
            foreach (var url in Split(endpoint["Url"]))
            {
                yield return new ConfiguredEndpoint($"{KestrelEndpointsSection}:{endpoint.Key}:Url", url);
            }
        }
    }

    /// <summary>
    /// True only for <c>localhost</c> and an IP literal the runtime itself calls loopback
    /// (127.0.0.0/8 and <c>::1</c>). Everything else - <c>0.0.0.0</c>, <c>*</c>, <c>+</c>,
    /// <c>[::]</c>, a routable address, a DNS name - is refused, because none of them can be
    /// proven to stay on this machine at configuration time.
    /// </summary>
    public static bool IsLoopbackUrl(string? url, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            reason = "the listen URL is empty";
            return false;
        }

        if (!TryReadHost(url.Trim(), out var host))
        {
            reason = "the listen URL is not a parsable '<scheme>://<host>[:<port>]' value";
            return false;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address))
        {
            return true;
        }

        reason = host is "*" or "+"
            ? "'*' and '+' bind every network interface"
            : $"host '{host}' is not a loopback address";
        return false;
    }

    /// <summary>
    /// Extracts the host portion without <see cref="Uri"/>, which cannot parse the wildcard
    /// hosts Kestrel accepts (<c>*</c>, <c>+</c>) - and those are exactly the values that must
    /// be recognised and refused rather than silently skipped.
    /// </summary>
    private static bool TryReadHost(string url, out string host)
    {
        host = string.Empty;

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return false;
        }

        var authority = url[(schemeEnd + 3)..];
        var pathStart = authority.IndexOfAny(['/', '?', '#']);
        if (pathStart >= 0)
        {
            authority = authority[..pathStart];
        }

        if (authority.Length == 0)
        {
            return false;
        }

        // Credentials are not valid in a listen URL, but if one appears the host is what
        // follows the '@' - reading the wrong half would compare the wrong value.
        var credentialsEnd = authority.LastIndexOf('@');
        if (credentialsEnd >= 0)
        {
            authority = authority[(credentialsEnd + 1)..];
        }

        if (authority.StartsWith('['))
        {
            var closing = authority.IndexOf(']');
            if (closing <= 1)
            {
                return false;
            }

            host = authority[1..closing];
            return true;
        }

        var portSeparator = authority.IndexOf(':');
        host = portSeparator >= 0 ? authority[..portSeparator] : authority;
        return host.Length > 0;
    }

    private static IEnumerable<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static LabEnvironmentException Refuse(
        string applicationName,
        string source,
        string value,
        string reason) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"{applicationName} refuses to listen on '{value}' (from configuration '{source}'): {reason}. " +
            $"This lab is unauthenticated and may only bind loopback (localhost, 127.0.0.0/8 or [::1])."));
}
