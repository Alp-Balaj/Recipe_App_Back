using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Images.Abstractions;
using RecipeApp.Infrastructure.Images;

namespace RecipeApp.UnitTests;

// R2ImageStorage in isolation (publish cp1): no network — the fake below captures the
// PutObjectRequest the storage would send. Hand-rolled fake per the repo convention
// (FakeChatAssistantService): IAmazonS3 is ~200 members, but AmazonS3Client's operation
// methods are virtual, so a subclass overriding just PutObjectAsync is enough.
public class R2ImageStorageTests
{
    private sealed class FakeS3Client() : AmazonS3Client(
        new BasicAWSCredentials("test-access-key", "test-secret"),
        new AmazonS3Config { ServiceURL = "https://fake.example.com", ForcePathStyle = true })
    {
        public PutObjectRequest? CapturedRequest { get; private set; }

        public override Task<PutObjectResponse> PutObjectAsync(
            PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            CapturedRequest = request;
            return Task.FromResult(new PutObjectResponse());
        }
    }

    private static R2Settings Settings(string publicBaseUrl = "https://pub-abc.r2.dev") => new()
    {
        AccountId = "acct",
        AccessKeyId = "key",
        SecretAccessKey = "secret",
        Bucket = "recipe-images",
        PublicBaseUrl = publicBaseUrl,
    };

    [Theory]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".png", "image/png")]
    [InlineData(".webp", "image/webp")]
    public async Task SaveAsync_PutsGuidNamedObjectWithMappedContentType(string extension, string expectedContentType)
    {
        var fake = new FakeS3Client();
        var storage = new R2ImageStorage(fake, Settings());
        using var content = new MemoryStream([1, 2, 3]);

        var url = await storage.SaveAsync(content, extension);

        var request = Assert.IsType<PutObjectRequest>(fake.CapturedRequest);
        Assert.Equal("recipe-images", request.BucketName);
        Assert.Matches($"^[0-9a-f]{{32}}{Regex.Escape(extension)}$", request.Key);
        Assert.Equal(expectedContentType, request.ContentType);
        Assert.True(request.DisablePayloadSigning);
        Assert.Equal($"https://pub-abc.r2.dev/{request.Key}", url);
    }

    [Fact]
    public async Task SaveAsync_TrailingSlashBaseUrl_ProducesSingleSlashUrl()
    {
        var fake = new FakeS3Client();
        var storage = new R2ImageStorage(fake, Settings("https://pub-abc.r2.dev/"));
        using var content = new MemoryStream([1]);

        var url = await storage.SaveAsync(content, ".png");

        Assert.Equal($"https://pub-abc.r2.dev/{fake.CapturedRequest!.Key}", url);
    }

    // --- AddR2ImageStorage registration/config validation --------------------------------

    private static Dictionary<string, string?> FullConfig() => new()
    {
        ["ImageStorage:R2:AccountId"] = "acct",
        ["ImageStorage:R2:AccessKeyId"] = "key",
        ["ImageStorage:R2:SecretAccessKey"] = "secret",
        ["ImageStorage:R2:Bucket"] = "recipe-images",
        ["ImageStorage:R2:PublicBaseUrl"] = "https://pub-abc.r2.dev",
    };

    [Fact]
    public void AddR2ImageStorage_CompleteConfig_ResolvesR2Storage()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(FullConfig()).Build();
        var services = new ServiceCollection().AddR2ImageStorage(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<R2ImageStorage>(provider.GetRequiredService<IImageStorage>());
    }

    [Theory]
    [InlineData("AccountId")]
    [InlineData("AccessKeyId")]
    [InlineData("SecretAccessKey")]
    [InlineData("Bucket")]
    [InlineData("PublicBaseUrl")]
    public void AddR2ImageStorage_MissingKey_ThrowsNamingTheKey(string missingKey)
    {
        var config = FullConfig();
        config.Remove($"ImageStorage:R2:{missingKey}");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddR2ImageStorage(configuration));

        Assert.Contains(missingKey, exception.Message);
    }
}
