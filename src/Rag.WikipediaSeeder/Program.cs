using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rag.Core.Extensions;
using Rag.Core.Options;
using Rag.Core.Telemetry;
using Rag.WikipediaSeeder;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "Rag.WikipediaSeeder")
    .Enrich.WithProperty("ServiceVersion", RagTelemetry.ServiceVersion)
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var host = Host.CreateDefaultBuilder(args)
        .UseSerilog((ctx, services, config) => config
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", "Rag.WikipediaSeeder")
            .Enrich.WithProperty("ServiceVersion", RagTelemetry.ServiceVersion)
            .WriteTo.Console(new CompactJsonFormatter()))
        .ConfigureServices((ctx, services) =>
        {
            var config = ctx.Configuration;

            services.Configure<RabbitMqOptions>(config.GetSection("RabbitMQ"));
            services.Configure<HuggingFaceOptions>(config.GetSection("HuggingFace"));
            services.Configure<SeederOptions>(config.GetSection("Seeder"));

            services.AddBlobStorage(config);
            services.AddRagOpenTelemetry(config["OpenTelemetry:OtlpEndpoint"]);

            services.AddHttpClient("huggingface", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            });

            services.AddTransient<WikipediaSeeder>();
        })
        .Build();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    var seeder = host.Services.GetRequiredService<WikipediaSeeder>();
    await seeder.RunAsync(cts.Token);
}
catch (Exception ex)
{
    Log.Fatal(ex, "WikipediaSeeder terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
