using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace RecipeApp.Infrastructure.Persistence;

/// <summary>
/// The ONE place an Npgsql data source for this application is configured. Three callers
/// build one — the API host, the design-time factory EF's tooling uses, and the integration
/// test factory — and before this existed each repeated the setup independently.
///
/// That duplication is why this class exists rather than three inline builders. The jsonb
/// enum contract below is invisible when it is wrong: nothing throws, nothing warns, the row
/// simply lands with a number in it. A fourth caller that forgets one line would corrupt data
/// silently and only at read time, months later. Route every data source through here.
/// </summary>
public static class RecipeAppDataSource
{
    /// <summary>
    /// Enum members inside jsonb MUST serialize as strings (stream G, sharp edge 1).
    ///
    /// <c>Recipe.Ingredients</c> (each carrying a <c>UnitOfMeasure</c>), <c>Recipe.Tags</c>
    /// and <c>User.DietaryRestrictions</c> are jsonb columns whose CLR shapes contain enums.
    /// System.Text.Json writes an enum as its INTEGER value by default, so without this
    /// converter a stored ingredient reads <c>"Unit": 4</c> rather than <c>"Unit": "Cup"</c>
    /// — and the integer is positional. Inserting a member into the middle of
    /// <see cref="RecipeApp.Domain.Enums.UnitOfMeasure"/>, or reordering two, would then
    /// silently reinterpret every recipe already in the table: cups become tablespoons with
    /// no migration, no error and no way to tell afterwards which reading was intended.
    ///
    /// Strings make the enum member NAME the persisted contract, which is the same thing
    /// every scalar enum column in ApplicationDbContext already does via
    /// <c>HasConversion&lt;string&gt;()</c>. This is that rule extended to the jsonb columns,
    /// which EF's conversions do not reach — Npgsql serializes those itself.
    ///
    /// The name is also what makes the SearchVector generated column and any hand-written
    /// jsonb predicate legible, and it survives a member being REMOVED as a loud parse
    /// failure rather than a quiet shift.
    /// </summary>
    private static readonly JsonSerializerOptions JsonbOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static NpgsqlDataSourceBuilder Configure(NpgsqlDataSourceBuilder builder)
    {
        // Required for the POCO-typed jsonb columns (List<RecipeIngredient> and friends):
        // without it Npgsql refuses to map an arbitrary CLR type to jsonb at all.
        builder.EnableDynamicJson();
        builder.ConfigureJsonOptions(JsonbOptions);
        return builder;
    }

    public static NpgsqlDataSource Build(string connectionString) =>
        Configure(new NpgsqlDataSourceBuilder(connectionString)).Build();
}
