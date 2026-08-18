namespace RecipeApp.Application.Auth.Dtos;

// Accounts (KAN-20): the active-devices list.
//
// ADR-0009 chose a LIST over a single "sign out everywhere" button, because knowing which
// device to drop is most of the value — a panic button is what you reach for when the list
// has already failed you.
public record SessionSummary(
    Guid Id,
    // A human-readable device label derived from the User-Agent, never the raw string.
    string Label,
    DateTime CreatedAt,
    DateTime LastSeenAtUtc,
    // True for the session making the request — the one row that must not read as droppable
    // like any other.
    bool Current);
