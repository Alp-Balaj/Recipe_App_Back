using RecipeApp.Application.Chat.Abstractions;

namespace RecipeApp.UnitTests;

// Unit tests for the AiBudget arithmetic (ai-quotas, Stream B) — the pure half of the quota
// system. The aggregation that feeds it (AiUsageService) is one EF query exercised by the
// chat integration tests; the decisions (remaining, exhaustion, both-limits semantics) live
// here where every boundary is cheap to pin down.
public class AiBudgetTests
{
    private static readonly DateTime Reset = new(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc);

    private static AiBudget Budget(int callsUsed = 0, long tokensUsed = 0, int callLimit = 50, long tokenLimit = 250_000) =>
        new(callLimit, callsUsed, tokenLimit, tokensUsed, Reset);

    [Fact]
    public void FreshDay_NothingUsed_FullBudgetRemains()
    {
        var budget = Budget();

        Assert.False(budget.IsExhausted);
        Assert.Equal(50, budget.CallsRemaining);
        Assert.Equal(250_000, budget.TokensRemaining);
    }

    [Fact]
    public void OneCallBelowTheLimit_IsNotExhausted()
    {
        // The gate must let the LAST budgeted call through: 49 of 50 used still allows one.
        var budget = Budget(callsUsed: 49);

        Assert.False(budget.IsExhausted);
        Assert.Equal(1, budget.CallsRemaining);
    }

    [Fact]
    public void AtTheCallLimit_IsExhausted()
    {
        var budget = Budget(callsUsed: 50);

        Assert.True(budget.IsExhausted);
        Assert.Equal(0, budget.CallsRemaining);
    }

    [Fact]
    public void AtTheTokenLimit_IsExhausted_EvenWithCallsLeft()
    {
        // Tokens are only known after a call returns, so the token line is crossed once and
        // enforced on the NEXT call — reaching the limit exactly must already refuse.
        var budget = Budget(callsUsed: 3, tokensUsed: 250_000);

        Assert.True(budget.IsExhausted);
        Assert.Equal(47, budget.CallsRemaining);
        Assert.Equal(0, budget.TokensRemaining);
    }

    [Fact]
    public void OverTheTokenLimit_RemainingClampsToZero_NeverNegative()
    {
        // An expensive final call can overshoot; the indicator must not show a negative number.
        var budget = Budget(tokensUsed: 260_000);

        Assert.True(budget.IsExhausted);
        Assert.Equal(0, budget.TokensRemaining);
    }

    [Fact]
    public void LimitsAndResetTime_SurfaceUnchanged()
    {
        var budget = Budget(callsUsed: 7, tokensUsed: 1_050);

        Assert.Equal(50, budget.DailyCallLimit);
        Assert.Equal(250_000, budget.DailyTokenLimit);
        Assert.Equal(7, budget.CallsUsed);
        Assert.Equal(1_050, budget.TokensUsed);
        Assert.Equal(Reset, budget.ResetsAtUtc);
    }
}
