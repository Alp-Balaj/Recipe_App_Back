using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Moderation.Abstractions;
using RecipeApp.Domain.Enums;

namespace RecipeApp.IntegrationTests;

// Stream X. Deterministic stand-in for the Gemini-backed IContentModerationClassifier so the
// integration suite never needs a key or makes a paid call — the same arrangement stream C, E
// and the chat lane already use for their assistants.
//
// This matters more here than for the other fakes: the moderation worker is a HOSTED SERVICE,
// so it starts with the test host and drains the queue for real. Without this registration
// every recipe and comment the whole suite creates would try to reach the provider.
//
// Behaviour is a pure function of the text, so it is safe under the shared TestServer with no
// per-test reset.
public sealed class FakeContentModerationClassifier : IContentModerationClassifier
{
    /// <summary>Content containing this is flagged well above the confidence threshold.</summary>
    public const string FlagSentinel = "__FLAG__";

    /// <summary>
    /// Flagged by the model but BELOW ModerationOptions.MinimumConfidence, so the worker must
    /// decline to write a report. This is the case that proves the threshold is load-bearing
    /// rather than decorative.
    /// </summary>
    public const string LowConfidenceSentinel = "__FLAG_LOW__";

    /// <summary>Simulates a provider failure, so the "a create is never affected" claim can be tested.</summary>
    public const string FailSentinel = "__MODFAIL__";

    public static readonly ChatTokenUsage Usage = new(80, 20, 100);

    public Task<ContentModerationVerdict> ClassifyAsync(
        ModerationSubject subject, CancellationToken cancellationToken = default)
    {
        var text = string.Join(" ", subject.Sections.Select(s => s.Text));

        if (text.Contains(FailSentinel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Simulated moderation classifier failure.");
        }

        if (text.Contains(LowConfidenceSentinel, StringComparison.Ordinal))
        {
            return Task.FromResult(new ContentModerationVerdict(
                ShouldFlag: true,
                Reason: ReportReason.Spam,
                Confidence: 0.10,
                Rationale: "[fake-classifier] weak signal, below the threshold.",
                Usage: Usage));
        }

        if (text.Contains(FlagSentinel, StringComparison.Ordinal))
        {
            return Task.FromResult(new ContentModerationVerdict(
                ShouldFlag: true,
                Reason: ReportReason.Inappropriate,
                Confidence: 0.95,
                Rationale: "[fake-classifier] flagged for the test sentinel.",
                Usage: Usage));
        }

        return Task.FromResult(new ContentModerationVerdict(
            ShouldFlag: false,
            Reason: ReportReason.Other,
            Confidence: 0d,
            Rationale: "[fake-classifier] nothing found.",
            Usage: Usage));
    }
}
