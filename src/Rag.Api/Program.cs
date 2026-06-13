using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Rag.Core.Contracts;
using Rag.Core.Extensions;
using Rag.Core.Options;
using Rag.Core.Services;
using Rag.Core.Telemetry;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", RagTelemetry.ServiceName)
    .Enrich.WithProperty("ServiceVersion", RagTelemetry.ServiceVersion)
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", RagTelemetry.ServiceName)
        .Enrich.WithProperty("ServiceVersion", RagTelemetry.ServiceVersion)
        .WriteTo.Console(new CompactJsonFormatter()));

    var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
    var resourceBuilder = ResourceBuilder.CreateDefault()
        .AddService(RagTelemetry.ServiceName, serviceVersion: RagTelemetry.ServiceVersion);

    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing =>
        {
            tracing
                .SetResourceBuilder(resourceBuilder)
                .AddSource(RagTelemetry.ServiceName)
                .AddSource("Grpc.Net.Client")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation();

            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        })
        .WithMetrics(metrics =>
        {
            metrics
                .SetResourceBuilder(resourceBuilder)
                .AddMeter(RagTelemetry.ServiceName)
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddHttpClientInstrumentation()
                .AddPrometheusExporter();

            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        });

    builder.Services.AddRagCore(builder.Configuration);
    builder.Services.AddOpenApi();

    var qdrantOpts = builder.Configuration.GetSection("Qdrant").Get<QdrantOptions>() ?? new QdrantOptions();
    var minioOpts = builder.Configuration.GetSection("Minio").Get<MinioOptions>() ?? new MinioOptions();
    var minioScheme = minioOpts.WithSsl ? "https" : "http";

    builder.Services.AddHealthChecks()
        .AddUrlGroup(
            new Uri($"http://{qdrantOpts.Host}:{qdrantOpts.HttpPort}/healthz"),
            name: "qdrant",
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready"])
        .AddUrlGroup(
            new Uri($"{minioScheme}://{minioOpts.Endpoint}/minio/health/live"),
            name: "minio",
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready"]);

    var app = builder.Build();

    app.UseSerilogRequestLogging();
    app.MapOpenApi();
    app.MapScalarApiReference();
    app.MapPrometheusScrapingEndpoint();

    app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }))
       .WithName("Liveness")
       .ExcludeFromDescription();

    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
        ResponseWriter = async (ctx, report) =>
        {
            ctx.Response.ContentType = "application/json";
            var result = System.Text.Json.JsonSerializer.Serialize(new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), description = e.Value.Description })
            });
            await ctx.Response.WriteAsync(result);
        }
    });

    app.MapPost("/query", async (QueryRequest request, VectorSearchService service, CancellationToken ct) =>
    {
        var req = request with { RequestId = string.IsNullOrWhiteSpace(request.RequestId) ? Guid.NewGuid().ToString() : request.RequestId };
        var response = await service.SearchAsync(req, ct);
        return response.Success ? Results.Ok(response) : Results.BadRequest(response);
    })
    .WithName("Query")
    .WithSummary("Semantic search over an indexed collection");

    app.MapPost("/index", async (IndexDocumentRequest request, IndexDocumentService service, CancellationToken ct) =>
    {
        var req = request with { RequestId = string.IsNullOrWhiteSpace(request.RequestId) ? Guid.NewGuid().ToString() : request.RequestId };
        var response = await service.IndexAsync(req, ct);
        return response.Success ? Results.Ok(response) : Results.BadRequest(response);
    })
    .WithName("Index")
    .WithSummary("Index a document already present in blob storage by its object key");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Rag.Api terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
public partial class Program { }
