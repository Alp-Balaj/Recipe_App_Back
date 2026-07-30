namespace RecipeApp.API;

// Governor (stream D): named authorization policies, mirroring the RateLimitPolicies
// convention. AdminOnly LAYERS ON TOP of the deny-by-default fallback policy (an
// authenticated user is still required first) and additionally demands the Admin role
// claim the JWT carries since stream D.
public static class AuthorizationPolicies
{
    public const string AdminOnly = "AdminOnly";
}
