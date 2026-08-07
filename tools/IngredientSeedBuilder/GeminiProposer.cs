using System.Net.Http.Json;
using System.Text.Json;
using RecipeApp.Application.MealPlanning;

namespace RecipeApp.Tools.IngredientSeedBuilder;

/// <summary>
/// Proposes a catalogue ingredient for a name the alias table cannot resolve.
///
/// This is the upgrade path <see cref="IngredientKey"/> already sanctions — "an LLM
/// canonicaliser may later supply a better key BEHIND this same interface" — and the two
/// words that make it safe are OFFLINE and PROPOSES.
///
/// OFFLINE: it runs here, at seed-build time, over a batch, by hand. Never on a request.
/// There is no latency to pay, no per-user cost, no quota lane to exhaust, and the output is
/// a file somebody reads rather than a decision nobody sees.
///
/// PROPOSES: nothing it returns is believed. The model is asked for an ingredient NAME, not
/// an id, and that name is then matched EXACTLY against the catalogue here — the same
/// defensive pattern the chat and propose-week lanes use, and the reason a hallucinated
/// ingredient becomes an empty proposal rather than a wrong alias. Everything that survives
/// still lands in the review file with Accepted = null, waiting for a human.
/// </summary>
public sealed class GeminiProposer
{
    // Batches of names per call. Small enough that a thinking spike cannot truncate the JSON
    // mid-string (the failure GeminiOptions documents at length), large enough that the
    // candidate list — much the biggest part of the prompt — is paid for once per 25 names
    // rather than once per name.
    private const int BatchSize = 25;

    private const string SystemPrompt =
        """
        You map ingredient names written on home recipes onto entries in a fixed catalogue of
        generic foods derived from USDA FoodData Central.

        For each name you are given, choose the ONE catalogue entry a shopper would consider
        the same ingredient, or return an empty string if none of them is.

        Rules, in order of importance:
        - "ingredient" MUST be copied character-for-character from the candidate list, or be
          the empty string. Never invent, abbreviate or re-spell an entry.
        - Return the empty string whenever you are choosing between plausible options rather
          than recognising the same food. A missing mapping costs a row on a shopping list; a
          wrong one silently rewrites what the app claims a dish contains.
        - Do NOT map a name onto a food it merely resembles or is made from. "Peanut butter"
          is not "Butter". "Coconut milk" is not "Milk". "Rice vinegar" is not "Rice".
        - Preparation and form are usually not identity: "minced garlic" is garlic, "cooked
          rice" is rice. But a different PRODUCT is not: "spring onions" are not "onions",
          and tinned is not fresh where the catalogue distinguishes them.
        - "confidence" is "high" only when the two names denote the same food beyond
          argument; "medium" when a shopper would agree but a nutritionist might quibble;
          "low" when you are unsure. Prefer an empty string to a "low".
        - "why" is one short clause a reviewer can check at a glance.
        """;

    private static readonly object ResponseSchema = new
    {
        type = "OBJECT",
        properties = new
        {
            proposals = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        name = new { type = "STRING" },
                        ingredient = new { type = "STRING" },
                        confidence = new { type = "STRING" },
                        why = new { type = "STRING" },
                    },
                    required = new[] { "name", "ingredient", "confidence", "why" },
                },
            },
        },
        required = new[] { "proposals" },
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public GeminiProposer(HttpClient http, string apiKey, string model)
    {
        _http = http;
        _apiKey = apiKey;
        _model = model;
    }

    public async Task<List<AliasProposal>> ProposeAsync(
        IReadOnlyList<string> unresolvedNames,
        IReadOnlyList<SeedIngredient> catalogue,
        CancellationToken ct = default)
    {
        // Exact, case-insensitive: the model is told to copy a candidate verbatim, and this
        // is where that instruction is enforced rather than trusted. A name that is not in
        // here becomes a proposal with no ingredient, which the merge gate refuses.
        var byName = new Dictionary<string, SeedIngredient>(StringComparer.OrdinalIgnoreCase);
        foreach (var ingredient in catalogue)
        {
            byName.TryAdd(ingredient.Name, ingredient);
        }

        var candidateList = string.Join('\n', catalogue.Select(i => i.Name).Order(StringComparer.OrdinalIgnoreCase));
        var proposals = new List<AliasProposal>(unresolvedNames.Count);

        for (var offset = 0; offset < unresolvedNames.Count; offset += BatchSize)
        {
            var batch = unresolvedNames.Skip(offset).Take(BatchSize).ToList();
            Console.WriteLine($"  proposing for {batch.Count} name(s) [{offset + 1}-{offset + batch.Count} of {unresolvedNames.Count}]");

            var answers = await CallAsync(batch, candidateList, ct);

            foreach (var name in batch)
            {
                answers.TryGetValue(name, out var answer);

                var proposed = answer?.Ingredient ?? string.Empty;
                var matched = proposed.Length > 0 && byName.TryGetValue(proposed, out var ingredient) ? ingredient : null;

                proposals.Add(new AliasProposal(
                    Name: name,
                    MatchKey: IngredientKey.For(name),
                    ProposedIngredient: matched?.Name ?? string.Empty,
                    ProposedIngredientId: matched?.Id,
                    Confidence: matched is null ? "none" : Normalise(answer?.Confidence),
                    Why: matched is null
                        ? (proposed.Length == 0
                            ? answer is null ? "the model returned nothing for this name" : "the model found no catalogue entry for it"
                            : $"the model answered \"{proposed}\", which is not a catalogue entry")
                        : answer?.Why ?? string.Empty,
                    Accepted: null));
            }
        }

        return proposals;
    }

    private async Task<Dictionary<string, Answer>> CallAsync(
        IReadOnlyList<string> batch, string candidateList, CancellationToken ct)
    {
        var userMessage =
            $"""
             CANDIDATE CATALOGUE ENTRIES
             {candidateList}

             NAMES TO MAP
             {string.Join('\n', batch)}

             Return one proposal per name above, with "name" copied exactly as given.
             """;

        var requestBody = new
        {
            systemInstruction = new { parts = new[] { new { text = SystemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = userMessage } } } },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseSchema = ResponseSchema,
                maxOutputTokens = 8192,
            },
        };

        // Key as a query parameter, never in the path — the same choice GeminiMessageCaller
        // makes so it cannot land in path-based request logs. It is also never printed.
        var uri = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

        using var response = await _http.PostAsJsonAsync(uri, requestBody, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Status only. The Gemini error body can echo the request, and the URI carries
            // the key — neither belongs in a console a person will paste into an issue.
            throw new InvalidOperationException($"Gemini generateContent returned HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var text = ExtractText(document.RootElement);
        using var parsed = JsonDocument.Parse(text);

        var answers = new Dictionary<string, Answer>(StringComparer.OrdinalIgnoreCase);
        if (parsed.RootElement.TryGetProperty("proposals", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var name = Text(item, "name");
                if (name.Length > 0)
                {
                    answers[name] = new Answer(Text(item, "ingredient"), Text(item, "confidence"), Text(item, "why"));
                }
            }
        }

        return answers;
    }

    private static string ExtractText(JsonElement root)
    {
        if (root.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0 &&
            candidates[0].TryGetProperty("content", out var content) &&
            content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString()!;
                }
            }
        }

        throw new InvalidOperationException("Gemini response contained no text part.");
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : string.Empty;

    private static string Normalise(string? confidence) =>
        confidence?.Trim().ToLowerInvariant() switch
        {
            "high" => "high",
            "medium" => "medium",
            "low" => "low",
            _ => "unstated",
        };

    private sealed record Answer(string Ingredient, string Confidence, string Why);
}
