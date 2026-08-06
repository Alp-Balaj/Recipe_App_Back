using System.Text;
using System.Text.Json;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Moderation.Abstractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Chat;

namespace RecipeApp.Infrastructure.Moderation;

// Stream X. Cast in the ChatAssistantService mould, and for the same reason: this class owns
// the policy prompt, the structured-output schema and the parsing, while the one part that
// talks to the network stays behind IChatMessageCaller — so all of the above is unit-testable
// with canned and malformed responses, and provider-agnostic.
//
// Nothing here touches the database, decides whether to write a Report, or knows what a
// confidence THRESHOLD is. Those are the worker's job. This class answers one question about
// one piece of text.
public class ContentModerationClassifier : IContentModerationClassifier
{
    // Gemini's schema dialect: UPPERCASE type names, no additionalProperties (see
    // GeminiMessageCaller). reason is a plain STRING rather than a schema enum on purpose —
    // the parse below already has to be defensive about a value outside the set, so
    // constraining it twice buys nothing and couples this schema to one provider's enum
    // support.
    private static readonly object ResponseSchema = new
    {
        type = "OBJECT",
        properties = new
        {
            violates = new { type = "BOOLEAN" },
            reason = new { type = "STRING" },
            confidence = new { type = "NUMBER" },
            rationale = new { type = "STRING" },
        },
        required = new[] { "violates", "reason", "confidence", "rationale" },
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Long enough to be evidence in the triage queue, short enough that a model that decides
    // to write an essay cannot blow past Report.Details' 1000-char column.
    private const int MaxRationaleLength = 400;

    private readonly IChatMessageCaller _caller;

    public ContentModerationClassifier(IChatMessageCaller caller)
    {
        _caller = caller;
    }

    public async Task<ContentModerationVerdict> ClassifyAsync(
        ModerationSubject subject, CancellationToken cancellationToken = default)
    {
        var systemPrompt = BuildSystemPrompt(subject.TargetType);
        var userMessage = BuildUserMessage(subject);

        // responseSchema is the FOURTH positional parameter and cancellationToken the fifth.
        // A token passed positionally here binds to responseSchema — it boxes into object? and
        // is serialized as the schema, with no compile error anywhere. So the schema goes in
        // positionally and the token is NAMED, exactly as ChatAssistantService does it.
        var call = await _caller.CreateJsonMessageAsync(
            systemPrompt,
            [],
            userMessage,
            ResponseSchema,
            cancellationToken: cancellationToken);

        var parsed = Parse(call.Json);

        // Structured output guarantees the shape, not semantic validity — the same trust
        // boundary ChatAssistantService draws around hallucinated recipe ids. An unrecognised
        // label degrades to Other rather than throwing: the flag itself is the signal worth
        // keeping, and a human is going to read the rationale anyway.
        var reason = Enum.TryParse<ReportReason>(parsed.Reason, ignoreCase: true, out var matched)
            ? matched
            : ReportReason.Other;

        // A model that reports 4.0 or -1 for a [0,1] probability is not a reason to lose the
        // verdict, but the stored value has to be comparable against a threshold.
        var confidence = double.IsNaN(parsed.Confidence) ? 0d : Math.Clamp(parsed.Confidence, 0d, 1d);

        var rationale = parsed.Rationale.Trim();
        if (rationale.Length > MaxRationaleLength)
        {
            rationale = rationale[..MaxRationaleLength];
        }

        return new ContentModerationVerdict(parsed.Violates, reason, confidence, rationale, call.Usage);
    }

    private static RawVerdict Parse(string json)
    {
        RawVerdict? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RawVerdict>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("moderation classifier returned malformed JSON.", ex);
        }

        if (parsed is null || parsed.Reason is null || parsed.Rationale is null)
        {
            throw new InvalidOperationException("moderation classifier response was missing required fields.");
        }

        return parsed;
    }

    private static string BuildSystemPrompt(ReportTargetType targetType)
    {
        var noun = targetType == ReportTargetType.Comment ? "comment" : "recipe";

        var sb = new StringBuilder();
        sb.AppendLine($"You are a content moderation classifier for a recipe sharing app. Judge whether the {noun} below violates the app's policy.");
        sb.AppendLine();
        sb.AppendLine("Set violates=true only for content that is genuinely one of the following:");
        sb.AppendLine("- Spam: advertising, affiliate or promotional links, SEO keyword stuffing, or text unrelated to cooking posted to promote something.");
        sb.AppendLine("- Inappropriate: sexual content, graphic violence, slurs, or instructions for something dangerous, harmful or inedible presented as food.");
        sb.AppendLine("- Harassment: abuse, threats, or demeaning language aimed at a person or group.");
        sb.AppendLine("- Misinformation: food-safety claims that would hurt someone who followed them, such as unsafe cooking temperatures, unsafe canning or preserving, or presenting a toxic ingredient as edible.");
        sb.AppendLine("- Other: a clear policy violation that none of the four labels above describes.");
        sb.AppendLine();
        // The precision/recall split is the whole design: this flag opens a queue item for a
        // person to judge, it never removes anything, so the cost of a false positive is one
        // human glance and the cost of a false negative is unmoderated content.
        sb.AppendLine("A flag does NOT remove or hide anything — it adds one item to a human moderator's review queue, and a person makes the final decision. So when the content is borderline, flag it and say why; but do not flag content that is merely unusual, unappetising, badly written, culturally unfamiliar, or in a language you do not recognise.");
        sb.AppendLine();
        sb.AppendLine("Set confidence between 0 and 1 to express how sure you are that this is a real violation. Use the full range: below 0.5 means you are guessing.");
        sb.AppendLine("Write rationale as ONE short sentence a human moderator can act on, naming the specific text that is the problem. If violates is false, set reason to \"Other\", confidence to 0, and rationale to a brief note that nothing was found.");
        sb.AppendLine();
        sb.AppendLine("reason must be exactly one of: Spam, Inappropriate, Harassment, Misinformation, Other.");

        return sb.ToString();
    }

    // Labelled sections rather than one blob so the rationale can name which part of a recipe
    // is offending — "Step 4" is actionable in the triage queue in a way that "the recipe" is not.
    private static string BuildUserMessage(ModerationSubject subject)
    {
        var sb = new StringBuilder();
        foreach (var section in subject.Sections)
        {
            var text = string.IsNullOrWhiteSpace(section.Text) ? "(empty)" : section.Text;
            sb.AppendLine($"[{section.Label}] {text}");
        }

        return sb.ToString();
    }

    // Mirrors the response schema. Confidence is a double so a model answering 1 (rather than
    // 1.0) still binds.
    private sealed record RawVerdict(bool Violates, string Reason, double Confidence, string Rationale);
}
