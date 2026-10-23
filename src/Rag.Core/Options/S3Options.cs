namespace Rag.Core.Options;

/// <summary>
/// Any S3-compatible object store: SeaweedFS, MinIO, Ceph RGW, Cloudflare R2, AWS S3, ...
/// Vendor differences are configuration only (endpoint, path-style, credentials).
/// </summary>
public sealed class S3Options
{
    /// <summary>
    /// Endpoint of the S3 API, e.g. <c>http://seaweedfs:8333</c>. Leave empty to talk to AWS S3 using <see cref="Region"/>.
    /// </summary>
    public string ServiceUrl { get; set; } = "http://localhost:8333";

    /// <summary>Signing region. Self-hosted stores ignore it but the signature still needs one.</summary>
    public string Region { get; set; } = "us-east-1";

    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Bucket { get; set; } = "rag-documents";

    /// <summary>
    /// <c>http://host/bucket/key</c> instead of <c>http://bucket.host/key</c>. Required by most self-hosted stores
    /// (SeaweedFS, MinIO); set to false for AWS S3 / R2 virtual-hosted style.
    /// </summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Create <see cref="Bucket"/> on first upload if it does not exist.</summary>
    public bool CreateBucketIfMissing { get; set; } = true;
}
