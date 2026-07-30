namespace RecipeApp.Domain.Enums;

// Governor (stream D, band 03): exactly two roles. Admin is additive on top of the
// deny-by-default authorization setup — a named policy tightens /admin/* further, and
// nothing user-facing ever branches on the role. Stored as text like every other enum.
public enum UserRole
{
    User,
    Admin,
}
