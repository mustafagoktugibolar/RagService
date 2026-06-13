using Rag.Core.Extensions;
using Rag.Core.Telemetry;
using Rag.QueryWorker;
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
    var builder = Host.CreateApplicationBuilder(args);
    if (builder.Environment.IsDevelopment())
    {
        builder.Configuration.AddUserSecrets<Worker>(optional: true);
    }

    builder.Services.AddSerilog((services, config) => config
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", RagTelemetry.ServiceName)
        .Enrich.WithProperty("ServiceVersion", RagTelemetry.ServiceVersion)
        .WriteTo.Console(new CompactJsonFormatter()));

    builder.Services.AddRagCore(builder.Configuration);
    builder.Services.AddRagOpenTelemetry(builder.Configuration["OpenTelemetry:OtlpEndpoint"]);
    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "QueryWorker terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
