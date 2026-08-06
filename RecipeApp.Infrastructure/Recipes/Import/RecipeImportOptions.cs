namespace RecipeApp.Infrastructure.Recipes.Import;

// Configuration for stream L's import lane. Every value is a defensive ceiling rather than a
// preference — the bound exists so a hostile or merely enormous page cannot cost the server
// more than a known amount of memory, time or money.
public sealed class RecipeImportOptions
{
    public const string SectionName = "RecipeImport";

    /// <summary>
    /// How long the whole page fetch may take, including redirects. Shorter than the Gemini
    /// client's 30 s: a recipe blog that has not answered in ten seconds is not going to.
    /// </summary>
    public int FetchTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Ceiling on a fetched page. Recipe blogs are bloated — 2 MB of HTML is already a very
    /// large page — and the body is read through a counting stream so the limit binds on what
    /// is actually transferred rather than on a Content-Length header the server may be lying
    /// about or omitting.
    /// </summary>
    public int MaxPageBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    /// Ceiling on a re-hosted image. Matches ImageUploadRules.MaxSizeBytes (5 MB) on purpose:
    /// an imported photo must not be able to occupy more storage than one a user could upload
    /// through the front door.
    /// </summary>
    public int MaxImageBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// Redirect hops followed, each re-validated against the address policy. Three covers the
    /// real cases (http→https, apex→www, a link shortener) without letting a redirect chain
    /// become a way to spend the fetch timeout.
    /// </summary>
    public int MaxRedirects { get; set; } = 3;

    /// <summary>
    /// How much page text reaches the extraction model. A recipe blog is mostly life story,
    /// advertising markup and comments, and the recipe is reliably near the top; sending the
    /// whole document would multiply the fallback lane's cost for text that is not the recipe.
    /// </summary>
    public int MaxExtractionTextLength { get; set; } = 24_000;

    /// <summary>
    /// Sent on every outbound fetch. Identifying the bot honestly is both good manners and
    /// practical: an unset or browser-impersonating agent is what most CDNs block first.
    /// </summary>
    public string UserAgent { get; set; } = "RecipeAppImporter/1.0 (+recipe import; respects robots)";
}
