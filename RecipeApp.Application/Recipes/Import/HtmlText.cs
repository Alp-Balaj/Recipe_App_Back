using System.Net;
using System.Text.RegularExpressions;

namespace RecipeApp.Application.Recipes.Import;

/// <summary>
/// The small amount of HTML handling stream L needs: pulling out JSON-LD script blocks, and
/// reducing a page to the visible text the extraction model reads when there are none.
///
/// REGEX, NOT A PARSER, AND THAT IS A SCOPED DECISION. Parsing HTML with regular expressions is
/// famously wrong, and it is wrong here too — for the general case. What saves it is that
/// neither job is the general case:
///
///   * The JSON-LD job only has to find the boundaries of a script element whose content is
///     JSON by specification. The CONTENT is then handed to System.Text.Json, which is a real
///     parser, and every malformed block is skipped rather than guessed at. A regex that
///     mis-frames a block produces invalid JSON and loses that block; it cannot produce a
///     WRONG recipe.
///   * The visible-text job feeds a language model, which is the most markup-tolerant consumer
///     imaginable. Leftover angle brackets cost a few tokens; they do not corrupt an answer.
///
/// The alternative is a package reference (AngleSharp, HtmlAgilityPack) added to the
/// Application layer, which is where this project keeps its framework-free code. When import
/// needs real DOM queries — microdata, RDFa, or CSS-selector scraping of unstructured pages —
/// that dependency becomes correct and belongs in Infrastructure behind the fetcher seam. It
/// is not needed for either job above.
/// </summary>
public static class HtmlText
{
    // Elements whose content is never prose. Dropped WITH their content, unlike every other
    // tag — leaving a page's JavaScript in the text handed to the model is both expensive and
    // an invitation to read instructions out of somebody else's source code.
    private static readonly Regex NonProseElements = new(
        @"<(script|style|noscript|svg|template|head)\b[^>]*>.*?</\1\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex JsonLdBlock = new(
        @"<script\b[^>]*\btype\s*=\s*[""']application/ld\+json[""'][^>]*>(?<json>.*?)</script\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Block-level boundaries become newlines before tags are stripped, so an ingredient list
    // does not collapse into one run-on line the model has to re-segment.
    private static readonly Regex BlockBoundary = new(
        @"</?(p|div|br|li|ul|ol|tr|td|th|h1|h2|h3|h4|h5|h6|section|article|header|footer|figcaption)\b[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AnyTag = new(@"<[^>]+>", RegexOptions.Compiled | RegexOptions.Singleline);

    // Tags are replaced by a SPACE rather than nothing, so "<b>chips</b>." would otherwise come
    // out as "chips ." — inline markup wrapping the last word of a sentence is extremely common
    // ("Proper <b>chips</b>."). Removing the space instead would fuse words across a tag
    // boundary ("<b>salt</b><i>pepper</i>" → "saltpepper"), so the space goes in first and the
    // stranded ones are tidied here.
    private static readonly Regex SpaceBeforePunctuation = new(@" +(?=[.,;:!?%)\]])", RegexOptions.Compiled);
    private static readonly Regex SpaceAfterOpening = new(@"(?<=[(\[]) +", RegexOptions.Compiled);
    private static readonly Regex BlankRuns = new(@"[ \t]*\n[ \t]*(?:\n[ \t]*)+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Every <c>application/ld+json</c> block on the page, in document order, still encoded.
    /// A page may carry several — one for the site, one for breadcrumbs, one for the recipe —
    /// so the caller tries all of them rather than assuming the first is the interesting one.
    /// </summary>
    public static IEnumerable<string> JsonLdBlocks(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            yield break;
        }

        foreach (Match match in JsonLdBlock.Matches(html))
        {
            var json = match.Groups["json"].Value.Trim();

            // Some CMSs wrap the payload in a CDATA section or an HTML comment; both are legal
            // in the wild and neither is JSON.
            json = json.Trim();
            if (json.StartsWith("<!--"))
            {
                json = json[4..];
                var close = json.IndexOf("-->", StringComparison.Ordinal);
                if (close >= 0)
                {
                    json = json[..close];
                }
            }

            json = json.Replace("<![CDATA[", string.Empty).Replace("]]>", string.Empty).Trim();

            if (json.Length > 0)
            {
                yield return json;
            }
        }
    }

    /// <summary>
    /// The page's visible text, for the extraction model. Not exact, and does not need to be —
    /// see the class comment.
    /// </summary>
    /// <param name="maxLength">
    /// Hard ceiling on what is handed to the model. A recipe blog is mostly life story, advert
    /// markup and a comment section, and the recipe is reliably near the top; sending the whole
    /// document would multiply the cost of the fallback lane for text that is not the recipe.
    /// </param>
    public static string VisibleText(string? html, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = NonProseElements.Replace(html, " ");
        text = BlockBoundary.Replace(text, "\n");
        text = AnyTag.Replace(text, " ");
        text = WebUtility.HtmlDecode(text);

        text = text.Replace(' ', ' ').Replace("\r\n", "\n").Replace('\r', '\n');
        text = Spaces.Replace(text, " ");
        text = BlankRuns.Replace(text, "\n\n");
        text = string.Join('\n', text.Split('\n').Select(line => line.Trim()));
        text = BlankRuns.Replace(text, "\n\n").Trim();

        return text.Length <= maxLength ? text : text[..maxLength];
    }

    /// <summary>
    /// Strips markup from a single field's value — schema.org string fields regularly carry
    /// inline <c>&lt;p&gt;</c> and <c>&lt;a&gt;</c> tags, and entity-encoded text besides.
    /// </summary>
    public static string PlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = WebUtility.HtmlDecode(AnyTag.Replace(value, " "));
        text = Spaces.Replace(text.Replace(' ', ' '), " ");
        text = SpaceBeforePunctuation.Replace(text, string.Empty);
        return SpaceAfterOpening.Replace(text, string.Empty).Trim();
    }
}
