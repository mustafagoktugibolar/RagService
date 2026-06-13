using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Rag.Core.Telemetry;

namespace Rag.Core.Extensions;

public static class OpenTelemetryExtensions
{
    public static IServiceCollection AddRagOpenTelemetry(this IServiceCollection services, string? otlpEndpoint)
    {
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(RagTelemetry.ServiceName, serviceVersion: RagTelemetry.ServiceVersion);

        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(resourceBuilder)
                    .AddSource(RagTelemetry.ServiceName)
                    .AddSource("Grpc.Net.Client")
                    .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(resourceBuilder)
                    .AddMeter(RagTelemetry.ServiceName)
                    .AddRuntimeInstrumentation()
                    .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            });

        return services;
    }
}
