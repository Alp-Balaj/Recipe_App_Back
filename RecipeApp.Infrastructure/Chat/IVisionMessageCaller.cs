using RecipeApp.Application.Chat.Abstractions;

namespace RecipeApp.Infrastructure.Chat;

// ══ DECISION D4 ═══════════════════════════════════════════════════════════════════════════
// The vision seam is a SIBLING of IChatMessageCaller, not a parameter added to it.
//
// The tempting alternative was one interface with an optional images argument, and it is worth
// naming why that is wrong rather than merely disfavoured. IChatMessageCaller has four
// implementations' worth of callers today — chat, propose-week, generation, cook mode, the
// moderation classifier — and NONE of them will ever send an image. Widening its one method
// would hand every one of those call sites a parameter that must always be null, and would
// hand every future fake a branch that is never exercised. A seam that most of its callers
// must ignore has stopped describing what it does.
//
// They also differ in a way the type system should carry: this one CANNOT be called without
// image bytes, which is the whole of its contract, and a nullable parameter cannot say that.
//
// ── DELIBERATELY NOT NAMED FOR IMPORT ────────────────────────────────────────────────────
// Stream L happens to be what brings this into existence, and stream N (the food scanner)
// reuses it rather than building its own. So there is nothing about recipes, imports or pages
// in this file: it is "send images and a prompt, get structured JSON back", which is equally
// true of reading a cookbook page and of identifying what is in a photograph of a fridge.
// Naming it IRecipeImportVisionCaller would have forced N to either accept a misleading name
// or add a second identical interface.
//
// Provider-neutral for the same reason IChatMessageCaller is: Gemini is today's implementation
// and nothing above this seam knows it.
public interface IVisionMessageCaller
{
    /// <summary>
    /// Sends one or more images with a prompt and returns the model's raw structured-output
    /// JSON text plus the provider-reported token usage (null when the provider omitted it).
    /// Parsing and validating the JSON is the caller's job — the text is returned verbatim,
    /// exactly as <see cref="IChatMessageCaller"/> does it.
    /// </summary>
    /// <param name="images">
    /// At least one. Bytes rather than URLs, deliberately: asking the provider to fetch a
    /// user-supplied address would move the SSRF problem onto somebody else's network and out
    /// of reach of the guard that solves it here.
    /// </param>
    Task<ChatMessageCall> CreateJsonMessageFromImagesAsync(
        string systemPrompt,
        IReadOnlyList<VisionImagePart> images,
        string userMessage,
        object? responseSchema = null,
        CancellationToken cancellationToken = default);
}

/// <summary>One image in a vision request: the raw bytes and the MIME type describing them.</summary>
public sealed record VisionImagePart(byte[] Content, string ContentType);
