using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Moderation.Abstractions;

// Stream X. The classification half, split from the queuing half on purpose: this interface
// is pure "text in, verdict out" with no database and no scheduling, which is what makes it
// unit-testable through a faked IChatMessageCaller — the ChatAssistantService mould.
public interface IContentModerationClassifier
{
    /// <summary>
    /// Asks the model whether this content violates the policy. Throws on a provider failure
    /// or a malformed response; the WORKER owns that failure, never a create path.
    /// </summary>
    Task<ContentModerationVerdict> ClassifyAsync(ModerationSubject subject, CancellationToken cancellationToken = default);
}

/// <summary>
/// What the classifier was asked to judge, as labelled sections rather than one blob, so the
/// prompt can name which part of a recipe is offending and the assembly stays testable.
/// </summary>
public sealed record ModerationSubject(ReportTargetType TargetType, IReadOnlyList<ModerationSection> Sections)
{
    /// <summary>Title, description and the step prose — the three fields stream X classifies.</summary>
    public static ModerationSubject ForRecipe(string title, string description, IEnumerable<string> steps)
    {
        var sections = new List<ModerationSection> { new("Title", title), new("Description", description) };
        var stepNumber = 1;
        foreach (var step in steps)
        {
            sections.Add(new ModerationSection($"Step {stepNumber++}", step));
        }

        return new ModerationSubject(ReportTargetType.Recipe, sections);
    }

    public static ModerationSubject ForComment(string content) =>
        new(ReportTargetType.Comment, [new ModerationSection("Comment", content)]);
}

public record ModerationSection(string Label, string Text);

/// <summary>
/// The model's judgement. <paramref name="Usage"/> rides along untouched for the metering
/// lane, exactly as ChatAssistantResult carries it — what a call cost is the provider's
/// report, and is accounted upstream rather than here.
/// </summary>
/// <param name="ShouldFlag">Whether the content violates the policy at all.</param>
/// <param name="Reason">
/// Which of stream D's five human reasons fits best. The closed list was kept closed: it is a
/// good label space for a classifier, and reusing it means an auto-flag sorts and filters in
/// the triage queue identically to a human report.
/// </param>
/// <param name="Confidence">Self-reported, clamped to [0,1] by the implementation.</param>
/// <param name="Rationale">One sentence, shown to the triaging admin as the report's Details.</param>
public record ContentModerationVerdict(
    bool ShouldFlag,
    ReportReason Reason,
    double Confidence,
    string Rationale,
    ChatTokenUsage? Usage);
