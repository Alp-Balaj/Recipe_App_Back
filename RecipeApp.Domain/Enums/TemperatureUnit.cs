namespace RecipeApp.Domain.Enums;

/// <summary>
/// The unit a step's <see cref="RecipeApp.Domain.ValueObjects.StepTemperature"/> is written in
/// (stream J).
///
/// Two members, and the shortness is the same decision <see cref="UnitOfMeasure"/> made: a
/// member is only worth admitting if something can be done with it. Celsius and Fahrenheit
/// convert into each other by arithmetic, so a reader can always be shown the one they think
/// in. Gas mark was considered and left out — it is a lookup table rather than a conversion,
/// its steps are coarse (mark 4 is a 15 °C band), and an author who wants it can write it in
/// the step prose, which is exactly where an unconvertible value belongs.
///
/// PERSISTED BY NAME inside the Steps jsonb column — see RecipeAppDataSource. Members may be
/// APPENDED freely; reordering or renaming one is a data migration, not an edit.
/// </summary>
public enum TemperatureUnit
{
    Celsius,
    Fahrenheit,
}
