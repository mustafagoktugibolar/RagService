using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Core.Abstractions;
using Rag.Core.Contracts;
using Rag.Core.Options;
using RabbitMQ.Client;

namespace Rag.WikipediaSeeder;

public sealed class WikipediaSeeder(
    IBlobStorage blobStorage,
    IOptions<HuggingFaceOptions> hfOptions,
    IOptions<SeederOptions> seederOptions,
    IOptions<RabbitMqOptions> rabbitOptions,
    IHttpClientFactory httpClientFactory,
    ILogger<WikipediaSeeder> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(CancellationToken ct)
    {
        var hf = hfOptions.Value;
        var seeder = seederOptions.Value;
        var rabbit = rabbitOptions.Value;

        using var connection = await CreateRabbitConnectionAsync(rabbit, ct);
        using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await channel.QueueDeclareAsync(
            queue: seeder.IndexQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: ct);

        using var httpClient = httpClientFactory.CreateClient("huggingface");

        var totalUploaded = 0;
        var totalSkipped = 0;
        var offset = 0;
        int? totalRows = null;

        logger.LogInformation(
            "Starting Wikipedia seed from HuggingFace dataset {Dataset}/{Config}/{Split}",
            hf.Dataset, hf.Config, hf.Split);

        while (!ct.IsCancellationRequested)
        {
            var url = $"https://datasets-server.huggingface.co/rows" +
                      $"?dataset={Uri.EscapeDataString(hf.Dataset)}" +
                      $"&config={Uri.EscapeDataString(hf.Config)}" +
                      $"&split={Uri.EscapeDataString(hf.Split)}" +
                      $"&offset={offset}&length={hf.BatchSize}";

            var response = await httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var responseBytes = await response.Content.ReadAsByteArrayAsync(ct);
            using var doc = JsonDocument.Parse(responseBytes);
            var root = doc.RootElement;

            totalRows ??= root.GetProperty("num_rows_total").GetInt32();

            var rows = root.GetProperty("rows");
            if (rows.GetArrayLength() == 0)
                break;

            foreach (var row in rows.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();

                var rowData = row.GetProperty("row");
                var id = GetStringValue(rowData.GetProperty("id"))
                         ?? row.GetProperty("row_idx").GetInt64().ToString();
                var passage = GetStringValue(rowData.GetProperty("passage")) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(passage))
                    continue;

                var objectKey = $"passages/{id}.txt";

                var existingKeys = blobStorage.ListObjectKeysAsync(objectKey, ct);
                var alreadyExists = false;
                await foreach (var _ in existingKeys)
                {
                    alreadyExists = true;
                    break;
                }

                if (alreadyExists)
                {
                    totalSkipped++;
                    continue;
                }

                var bytes = Encoding.UTF8.GetBytes(passage);
                using var stream = new MemoryStream(bytes);
                await blobStorage.UploadAsync(objectKey, stream, "text/plain", ct);

                await PublishIndexRequestAsync(channel, seeder, id, objectKey, ct);

                totalUploaded++;

                if (seeder.MaxDocuments > 0 && totalUploaded >= seeder.MaxDocuments)
                {
                    logger.LogInformation("Reached MaxDocuments limit of {Max}", seeder.MaxDocuments);
                    goto done;
                }
            }

            offset += hf.BatchSize;
            logger.LogInformation(
                "Progress: {Uploaded} uploaded, {Skipped} skipped, {Offset}/{Total}",
                totalUploaded, totalSkipped, offset, totalRows);

            if (offset >= totalRows)
                break;
        }

        done:
        logger.LogInformation(
            "Seeding complete. Uploaded: {Uploaded}, Skipped (already existed): {Skipped}",
            totalUploaded, totalSkipped);
    }

    private static async Task PublishIndexRequestAsync(
        IChannel channel,
        SeederOptions seeder,
        string documentId,
        string objectKey,
        CancellationToken ct)
    {
        var request = new IndexDocumentRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            Collection = seeder.Collection,
            DocumentId = documentId,
            FilePath = objectKey
        };

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions));

        var props = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent
        };

        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: seeder.IndexQueueName,
            mandatory: false,
            basicProperties: props,
            body: body,
            cancellationToken: ct);
    }

    private async Task<IConnection> CreateRabbitConnectionAsync(RabbitMqOptions opts, CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = opts.HostName,
            Port = opts.Port,
            UserName = opts.UserName,
            Password = opts.Password,
            VirtualHost = opts.VirtualHost
        };

        var delay = TimeSpan.FromSeconds(Math.Max(1, opts.ConnectionRetryDelaySeconds));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                return await factory.CreateConnectionAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "RabbitMQ not ready, retrying in {Delay}s...", delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }

        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Could not connect to RabbitMQ.");
    }

    private static string? GetStringValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => null,
        _ => element.GetRawText()
    };
}
