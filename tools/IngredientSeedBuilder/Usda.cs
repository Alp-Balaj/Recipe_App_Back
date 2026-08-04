using System.Text;

namespace RecipeApp.Tools.IngredientSeedBuilder;

/// <summary>
/// Reading the FoodData Central CSV bulk export. Minimal on purpose — the files are
/// well-formed RFC 4180 with every field quoted, so a full CSV library would be a
/// dependency bought for nothing.
/// </summary>
public static class Csv
{
    /// <summary>Streams rows as (header -> value) maps. Handles quoted fields and embedded commas/newlines.</summary>
    public static IEnumerable<Dictionary<string, string>> Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        var header = ParseLine(reader);
        if (header is null)
        {
            yield break;
        }

        while (true)
        {
            var fields = ParseLine(reader);
            if (fields is null)
            {
                break;
            }

            var row = new Dictionary<string, string>(header.Count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count; i++)
            {
                row[header[i]] = i < fields.Count ? fields[i] : string.Empty;
            }
            yield return row;
        }
    }

    private static List<string>? ParseLine(StreamReader reader)
    {
        if (reader.EndOfStream)
        {
            return null;
        }

        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        while (true)
        {
            var next = reader.Read();
            if (next < 0)
            {
                fields.Add(field.ToString());
                return fields;
            }

            var c = (char)next;

            if (inQuotes)
            {
                if (c == '"')
                {
                    // A doubled quote inside a quoted field is a literal quote.
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else { inQuotes = false; }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    return fields;
                default:
                    field.Append(c);
                    break;
            }
        }
    }
}

/// <summary>One FoodData Central food, with only the columns this tool uses.</summary>
public sealed class UsdaFood
{
    public required int FdcId { get; init; }
    public required string Description { get; init; }
    public required string DataType { get; init; }
    public int? CategoryId { get; init; }

    /// <summary>The comma-separated parts of the description, trimmed and non-empty.</summary>
    public required IReadOnlyList<string> Segments { get; init; }

    // Nutrition per 100 g, as FDC publishes it.
    public double? Kcal { get; set; }
    public double? ProteinG { get; set; }
    public double? FatG { get; set; }
    public double? CarbsG { get; set; }
    public double? FibreG { get; set; }

    public double? GramsPerMillilitre { get; set; }
    public double? GramsPerPiece { get; set; }
}
