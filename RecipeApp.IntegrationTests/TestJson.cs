using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecipeApp.IntegrationTests;

/// <summary>
/// Mirrors the API's wire format (global JsonStringEnumConverter registered in
/// Program.cs) so tests can round-trip bodies containing enums.
/// </summary>
public static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
