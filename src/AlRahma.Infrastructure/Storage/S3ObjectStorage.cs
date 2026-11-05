using Amazon.S3;
using Amazon.S3.Model;
using AlRahma.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace AlRahma.Infrastructure.Storage;

public class StorageOptions
{
    public const string Section = "Storage";
    /// <summary>عنوان MinIO الذي يصله السيرفر (داخل الشبكة).</summary>
    public string ServiceUrl { get; set; } = "http://localhost:9000";
    /// <summary>عنوان MinIO الذي يصله المتصفح. الروابط الموقعة تُبنى عليه. الافتراضي = ServiceUrl.</summary>
    public string? PublicUrl { get; set; }
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Bucket { get; set; } = "medical";
    public string Region { get; set; } = "us-east-1";
}

/// <summary>MinIO عبر واجهة S3. توقيع الروابط يتم محلياً ولا يحتاج اتصالاً.</summary>
public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly AmazonS3Client _internal;
    private readonly AmazonS3Client _public;

    public S3ObjectStorage(IOptions<StorageOptions> options)
    {
        var o = options.Value;
        Bucket = o.Bucket;
        _internal = Create(o, o.ServiceUrl);
        _public = Create(o, string.IsNullOrWhiteSpace(o.PublicUrl) ? o.ServiceUrl : o.PublicUrl);
    }

    public string Bucket { get; }

    private static AmazonS3Client Create(StorageOptions o, string url) =>
        new(o.AccessKey, o.SecretKey, new AmazonS3Config
        {
            ServiceURL = url,
            ForcePathStyle = true,
            AuthenticationRegion = o.Region,
            UseHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
        });

    public string PresignUpload(string objectKey, string contentType, TimeSpan expiresIn) =>
        _public.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = DateTime.UtcNow.Add(expiresIn),
        });

    public string PresignDownload(string objectKey, TimeSpan expiresIn) =>
        _public.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiresIn),
        });

    public async Task<long?> GetSizeAsync(string objectKey, CancellationToken ct = default)
    {
        try
        {
            var meta = await _internal.GetObjectMetadataAsync(Bucket, objectKey, ct);
            return meta.ContentLength;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task DeleteAsync(string objectKey, CancellationToken ct = default) =>
        _internal.DeleteObjectAsync(Bucket, objectKey, ct);

    public void Dispose()
    {
        _internal.Dispose();
        _public.Dispose();
    }
}
