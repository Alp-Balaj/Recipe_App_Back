using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Moderation.Abstractions;

namespace RecipeApp.Infrastructure.Moderation;

// Stream X. Registers the out-of-band moderation pass: the queue, its single worker, and the
// classifier the worker resolves per item.
public static class ModerationServiceCollectionExtensions
{
    public static IServiceCollection AddContentModeration(this IServiceCollection services, IConfiguration configuration)
    {
        // Bound eagerly with code defaults — no secret is involved, same as AiQuotaOptions.
        var options = new ModerationOptions();
        configuration.GetSection(ModerationOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        // The queue is a singleton because the channel IS the shared state: scoped request
        // services write to it and the singleton worker reads from it. Registered concretely
        // and then forwarded to the interface, so the worker can reach the internal
        // ChannelReader while every write path only ever sees TryEnqueue.
        services.AddSingleton<ContentModerationQueue>();
        services.AddSingleton<IContentModerationQueue>(sp => sp.GetRequiredService<ContentModerationQueue>());

        // Scoped, and resolved inside the worker's PER-ITEM scope: it depends on
        // IChatMessageCaller, which is itself scoped and whose factory is where the Gemini key
        // check fires. Registering it as a singleton would capture a scoped dependency and
        // demand the key at startup — which is exactly what the chat seam avoids so the
        // integration-test host can build without one.
        services.AddScoped<IContentModerationClassifier, ContentModerationClassifier>();

        services.AddHostedService<ContentModerationWorker>();

        return services;
    }
}
