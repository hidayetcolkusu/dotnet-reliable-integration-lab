using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Integration.Shared.Diagnostics;

/// <summary>
/// One telemetry setup shared by the API, the worker and FakeErp.
/// The OTLP endpoint is optional: tracing must never be a precondition for work completing.
/// </summary>
public static class LabTelemetryHosting
{
    public const string OtlpEndpointConfigKey = "Lab:OtlpEndpoint";

    public static TBuilder AddLabTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                builder.Environment.ApplicationName,
                serviceVersion: LabTelemetry.Version,
                serviceInstanceId: Environment.MachineName))
            .WithTracing(tracing => tracing
                .AddSource(LabTelemetry.ActivitySourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        var otlpEndpoint = builder.Configuration[OtlpEndpointConfigKey];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint) && Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var endpoint))
        {
            telemetry.WithTracing(tracing => tracing.AddOtlpExporter(options => options.Endpoint = endpoint));
        }

        return builder;
    }
}
