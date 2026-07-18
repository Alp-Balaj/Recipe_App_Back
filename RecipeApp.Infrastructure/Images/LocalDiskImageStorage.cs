using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Images.Abstractions;

namespace RecipeApp.Infrastructure.Images;

// Local-disk IImageStorage (social-feed cp04, decision I1 → option a). Writes each image
// under a server-generated Guid + validated extension inside a single flat root directory
// and returns the relative URL the static-file middleware (mounted on the same root in
// Program.cs) serves it from. Swapping to S3/MinIO later = replace this class + the DI
// line; the interface, endpoint, and tests don't move.
public class LocalDiskImageStorage(string rootPath, string requestPath) : IImageStorage
{
    public async Task<string> SaveAsync(Stream content, string extension, CancellationToken cancellationToken = default)
    {
        // Guid "N" (32 hex chars) — unguessable, filesystem-safe, no client input involved.
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var filePath = Path.Combine(rootPath, fileName);

        // CreateNew: a Guid collision (or anything already squatting the name) faults loudly
        // instead of silently overwriting another user's image.
        var fileStream = new FileStream(
            filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true);
        try
        {
            await using (fileStream)
            {
                await content.CopyToAsync(fileStream, cancellationToken);
            }
        }
        catch
        {
            // Don't leave a half-written orphan behind on abort/failure.
            TryDelete(filePath);
            throw;
        }

        return $"{requestPath}/{fileName}";
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (IOException)
        {
            // Best-effort cleanup only — the original exception is the one that matters.
        }
    }
}

public static class ImageStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the local-disk IImageStorage over <paramref name="rootPath"/> (created if
    /// missing, so the static-file provider mounted on the same directory can't throw on a
    /// fresh checkout). <paramref name="requestPath"/> is the URL prefix returned URLs use.
    /// </summary>
    public static IServiceCollection AddLocalDiskImageStorage(
        this IServiceCollection services, string rootPath, string requestPath = "/images")
    {
        Directory.CreateDirectory(rootPath);
        services.AddSingleton<IImageStorage>(new LocalDiskImageStorage(rootPath, requestPath));
        return services;
    }
}
