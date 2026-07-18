namespace RecipeApp.Application.Images.Abstractions;

// Storage seam for uploaded images (social-feed cp04, decision I1). Provider-agnostic on
// purpose — the interface knows nothing about disks, buckets, or request paths — so the
// local-disk implementation (Infrastructure) can later be swapped for S3/MinIO with one
// class + one DI line, mirroring the IChatAssistantService seam pattern.
public interface IImageStorage
{
    /// <summary>
    /// Persists the image content under a server-generated, unguessable name and returns
    /// the public URL (relative path) that serves it back. The extension is the
    /// server-validated one from ImageUploadRules — never the client's filename.
    /// </summary>
    Task<string> SaveAsync(Stream content, string extension, CancellationToken cancellationToken = default);
}
