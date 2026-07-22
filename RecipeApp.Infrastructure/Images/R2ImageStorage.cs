using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Images.Abstractions;

namespace RecipeApp.Infrastructure.Images;

// Cloudflare R2 IImageStorage (publish cp1) — the production swap decision I1 planned for:
// Railway's disk is ephemeral, so uploads go to an R2 bucket and the returned URL is the
// bucket's public one (absolute; the frontend's resolveImageUrl passes absolute URLs
// through untouched). Same server-generated Guid + validated-extension naming as
// LocalDiskImageStorage, so the two implementations stay interchangeable.
public class R2ImageStorage(IAmazonS3 s3, R2Settings settings) : IImageStorage
{
    public async Task<string> SaveAsync(Stream content, string extension, CancellationToken cancellationToken = default)
    {
        var key = $"{Guid.NewGuid():N}{extension}";

        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = settings.Bucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            // The extension is server-validated (ImageUploadRules pins it to the
            // magic-byte-verified type), so this reverse map is total.
            ContentType = extension switch
            {
                ".jpg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => throw new ArgumentException($"Unexpected image extension '{extension}'.", nameof(extension)),
            },
            // R2 rejects the streaming payload checksums newer AWS SDKs send by default;
            // unsigned payload over https is the documented R2 configuration.
            DisablePayloadSigning = true,
        }, cancellationToken);

        return $"{settings.PublicBaseUrl.TrimEnd('/')}/{key}";
    }
}

/// <summary>Config section ImageStorage:R2 — all five values are required in R2 mode.</summary>
public record R2Settings
{
    public const string SectionName = "ImageStorage:R2";

    public string AccountId { get; init; } = string.Empty;
    public string AccessKeyId { get; init; } = string.Empty;
    public string SecretAccessKey { get; init; } = string.Empty;
    public string Bucket { get; init; } = string.Empty;
    public string PublicBaseUrl { get; init; } = string.Empty;
}

public static class R2ImageStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Cloudflare R2 IImageStorage from the ImageStorage:R2 config section.
    /// Fails fast at startup (same convention as the Jwt:Key check in Program.cs) when any
    /// of the five required values is missing, naming the offending key.
    /// </summary>
    public static IServiceCollection AddR2ImageStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(R2Settings.SectionName).Get<R2Settings>() ?? new R2Settings();

        foreach (var (key, value) in new[]
        {
            (nameof(R2Settings.AccountId), settings.AccountId),
            (nameof(R2Settings.AccessKeyId), settings.AccessKeyId),
            (nameof(R2Settings.SecretAccessKey), settings.SecretAccessKey),
            (nameof(R2Settings.Bucket), settings.Bucket),
            (nameof(R2Settings.PublicBaseUrl), settings.PublicBaseUrl),
        })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"{R2Settings.SectionName}:{key} not configured — set via ImageStorage__R2__{key} env var.");
            }
        }

        // R2 is S3-compatible with two caveats: path-style addressing against the account
        // endpoint, and no support for the SDK's newer default request/response checksums
        // (WHEN_REQUIRED turns them off for plain PutObject).
        var s3Config = new AmazonS3Config
        {
            ServiceURL = $"https://{settings.AccountId}.r2.cloudflarestorage.com",
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        services.AddSingleton<IAmazonS3>(new AmazonS3Client(
            new BasicAWSCredentials(settings.AccessKeyId, settings.SecretAccessKey), s3Config));
        services.AddSingleton<IImageStorage>(sp =>
            new R2ImageStorage(sp.GetRequiredService<IAmazonS3>(), settings));
        return services;
    }
}
