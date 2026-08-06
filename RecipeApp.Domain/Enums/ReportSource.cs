namespace RecipeApp.Domain.Enums;

// Stream X, decision D20: who authored a report. Stream D's Report was shaped for a human —
// ReporterId is a non-nullable FK with a required navigation — and the auto-flag has to land
// in the SAME Open queue GetReportsAsync already serves, not a second surface.
//
// The reporter column keeps carrying a real user either way (see SystemUsers): this
// discriminator is what tells a triaging admin, and any future query, that the row came from
// the classifier rather than from a person. Deriving that from "ReporterId == a magic Guid"
// would work and was rejected — an implicit convention costs the same storage as an explicit
// column and cannot be filtered on without hardcoding the id into the query.
public enum ReportSource
{
    // A person pressed Report. Every row written before stream X is this, which is why it is
    // the enum's default and the column's backfill value.
    Human,

    // The moderation classifier flagged it out-of-band after the content was created.
    // Confidence is non-null exactly when a report is Automated.
    Automated,
}
