using Microsoft.Extensions.Diagnostics.HealthChecks;
using Rag.Core.Providers.Storage;

namespace Rag.Api.Health;

/// <summary>
/// Vendor-neutral readiness probe: one authenticated S3 call instead of a store-specific URL
/// (MinIO's /minio/health/live, SeaweedFS's /healthz, ... differ). No-op for the local provider.
/// </summary>
public sealed class ObjectStorageHealthCheck(IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var s3 = services.GetService<S3BlobStorage>();
        if (s3 is null)
            return HealthCheckResult.Healthy("No remote object storage configured.");

        try
        {
            await s3.PingAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Object storage unreachable.", ex);
        }
    }
}
