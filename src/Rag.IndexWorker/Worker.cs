using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Rag.Core.Contracts;
using Rag.Core.Options;
using Rag.Core.Services;

namespace Rag.IndexWorker;

public sealed class Worker(
    ILogger<Worker> logger,
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options) : BackgroundService
{
    private const string DlxExchange = "rag.dlx";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private IConnection? _connection;
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rabbitOptions = options.Value;
        var queueName = string.IsNullOrWhiteSpace(rabbitOptions.QueueName)
            ? "rag.index"
            : rabbitOptions.QueueName;

        var factory = CreateConnectionFactory(rabbitOptions);
        await ConnectWithRetryAsync(factory, rabbitOptions, stoppingToken);

        await SetupQueuesAsync(queueName, stoppingToken);

        var channel = _channel ?? throw new InvalidOperationException("RabbitMQ channel is not initialized.");

        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: rabbitOptions.Prefetch == 0 ? (ushort)1 : rabbitOptions.Prefetch,
            global: false,
            cancellationToken: stoppingToken);

        var concurrency = Math.Max(1, rabbitOptions.Concurrency);
        using var semaphore = new SemaphoreSlim(concurrency, concurrency);
        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.ReceivedAsync += async (_, args) =>
        {
            await semaphore.WaitAsync(stoppingToken);
            var success = false;
            try
            {
                success = await HandleMessageAsync(args, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception processing delivery tag {Tag}", args.DeliveryTag);
            }
            finally
            {
                semaphore.Release();
            }

            try
            {
                if (success)
                    await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken: CancellationToken.None);
                else
                    await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false, cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to ack/nack delivery tag {Tag}", args.DeliveryTag);
            }
        };

        await channel.BasicConsumeAsync(
            queue: queueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        logger.LogInformation(
            "Index worker consuming queue {QueueName} with prefetch {Prefetch} and concurrency {Concurrency}",
            queueName,
            rabbitOptions.Prefetch,
            concurrency);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null)
        {
            await _channel.CloseAsync(cancellationToken);
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.CloseAsync(cancellationToken);
            await _connection.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task SetupQueuesAsync(string queueName, CancellationToken ct)
    {
        var dlqQueue = $"{queueName}.dlq";
        var channel = _channel!;

        await channel.ExchangeDeclareAsync(DlxExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.QueueDeclareAsync(dlqQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(dlqQueue, DlxExchange, queueName, cancellationToken: ct);

        try
        {
            await channel.QueueDeclareAsync(
                queue: queueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = DlxExchange },
                cancellationToken: ct);

            logger.LogInformation("Queue {Queue} configured with dead-letter exchange {Dlx}", queueName, DlxExchange);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not configure DLQ for {Queue} — queue may already exist without DLQ arguments. " +
                "Delete the queue and restart to enable dead-lettering.", queueName);

            _channel = await _connection!.CreateChannelAsync(cancellationToken: ct);
            await _channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        }
    }

    private async Task<bool> HandleMessageAsync(BasicDeliverEventArgs args, CancellationToken ct)
    {
        var replyTo = args.BasicProperties?.ReplyTo;
        var correlationId = args.BasicProperties?.CorrelationId;
        IndexDocumentResponse response;

        try
        {
            var request = JsonSerializer.Deserialize<IndexDocumentRequest>(args.Body.Span, JsonOptions)
                ?? throw new JsonException("Message body was empty.");

            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IndexDocumentService>();
            response = await service.IndexAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BrokenCircuitException ex)
        {
            logger.LogWarning(ex, "Circuit open — downstream unavailable, nacking message {CorrelationId}", correlationId);
            response = new IndexDocumentResponse
            {
                RequestId = string.Empty,
                DocumentId = string.Empty,
                Success = false,
                ErrorMessage = "Service temporarily unavailable."
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process index request with correlation id {CorrelationId}", correlationId);
            response = new IndexDocumentResponse
            {
                RequestId = string.Empty,
                DocumentId = string.Empty,
                Success = false,
                ErrorMessage = ex.Message
            };
        }

        if (!string.IsNullOrWhiteSpace(replyTo))
            await PublishResponseAsync(replyTo, correlationId, response, ct);
        else
            logger.LogWarning("Index message with delivery tag {Tag} did not include reply-to", args.DeliveryTag);

        return response.Success;
    }

    private async Task PublishResponseAsync(string replyTo, string? correlationId, IndexDocumentResponse response, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, JsonOptions));
        var properties = new BasicProperties
        {
            CorrelationId = correlationId,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent
        };

        await _channel!.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: replyTo,
            mandatory: true,
            basicProperties: properties,
            body: body,
            cancellationToken: ct);
    }

    private static ConnectionFactory CreateConnectionFactory(RabbitMqOptions rabbitOptions) =>
        new()
        {
            HostName = rabbitOptions.HostName,
            Port = rabbitOptions.Port,
            UserName = rabbitOptions.UserName,
            Password = rabbitOptions.Password,
            VirtualHost = rabbitOptions.VirtualHost
        };

    private async Task ConnectWithRetryAsync(ConnectionFactory factory, RabbitMqOptions rabbitOptions, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, rabbitOptions.ConnectionRetryDelaySeconds));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _connection = await factory.CreateConnectionAsync(ct);
                _channel = await _connection.CreateChannelAsync(cancellationToken: ct);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "RabbitMQ connection failed for {HostName}:{Port}; retrying in {Delay}s",
                    rabbitOptions.HostName, rabbitOptions.Port, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }
}
