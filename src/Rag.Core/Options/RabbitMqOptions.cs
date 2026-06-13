namespace Rag.Core.Options;

public sealed class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string UserName { get; set; } = "guest";

    public string Password { get; set; } = "guest";

    public string VirtualHost { get; set; } = "/";

    public string QueueName { get; set; } = string.Empty;

    public ushort Prefetch { get; set; } = 8;

    public int Concurrency { get; set; } = 8;

    public int ConnectionRetryDelaySeconds { get; set; } = 5;
}
