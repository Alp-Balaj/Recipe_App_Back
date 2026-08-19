using RecipeApp.Infrastructure.Auth;

namespace RecipeApp.UnitTests;

// Accounts (KAN-21, ADR-0008). The decision under test is the ABSENCE of a lockout, so the
// most important assertion in this file is the last one: however many times an account
// fails, the wait is bounded and the account is still reachable.
//
// Time moves through the `now` overloads rather than through a clock abstraction — see the
// note on SignInBackoff for why this state cannot use the rewrite-the-timestamps trick the
// row-backed tests use.
public class SignInBackoffTests
{
    private static readonly DateTime T0 = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_first_three_failures_cost_nothing()
    {
        var backoff = new SignInBackoff();

        for (var i = 0; i < SignInBackoff.FreeFailures; i++)
        {
            backoff.RecordFailure("k", T0);
            Assert.Null(backoff.RetryAfter("k", T0));
        }
    }

    [Fact]
    public void The_fourth_failure_starts_the_curve_and_it_doubles()
    {
        var backoff = new SignInBackoff();

        for (var i = 0; i < 4; i++)
        {
            backoff.RecordFailure("k", T0);
        }
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.RetryAfter("k", T0));

        backoff.RecordFailure("k", T0);
        Assert.Equal(TimeSpan.FromSeconds(4), backoff.RetryAfter("k", T0));

        backoff.RecordFailure("k", T0);
        Assert.Equal(TimeSpan.FromSeconds(8), backoff.RetryAfter("k", T0));
    }

    [Fact]
    public void The_wait_is_what_is_LEFT_of_the_delay_not_the_whole_of_it()
    {
        var backoff = new SignInBackoff();
        for (var i = 0; i < 5; i++)
        {
            backoff.RecordFailure("k", T0);
        }

        // Four seconds owed, three already served.
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.RetryAfter("k", T0.AddSeconds(3)));
        Assert.Null(backoff.RetryAfter("k", T0.AddSeconds(4)));
    }

    [Fact]
    public void The_delay_is_capped_and_an_account_is_never_locked_out()
    {
        var backoff = new SignInBackoff();

        // Far past the point where doubling would overflow an int shift, which is the shape
        // of bug that turns a ceiling into a negative delay and lets everything through.
        for (var i = 0; i < 200; i++)
        {
            backoff.RecordFailure("k", T0);
        }

        var wait = backoff.RetryAfter("k", T0);
        Assert.Equal(SignInBackoff.MaxDelay, wait);

        // The property ADR-0008 rests on: waiting the delay out always reopens the door.
        // Nothing this class can do puts an account beyond reach.
        Assert.Null(backoff.RetryAfter("k", T0.Add(SignInBackoff.MaxDelay)));
    }

    [Fact]
    public void Thirty_quiet_minutes_forgets_everything_rather_than_resuming_the_curve()
    {
        var backoff = new SignInBackoff();
        for (var i = 0; i < 10; i++)
        {
            backoff.RecordFailure("k", T0);
        }

        var later = T0.Add(SignInBackoff.Decay);
        Assert.Null(backoff.RetryAfter("k", later));

        // And the NEXT failure starts from one, rather than picking the old curve back up.
        backoff.RecordFailure("k", later);
        Assert.Null(backoff.RetryAfter("k", later));
    }

    [Fact]
    public void A_success_clears_the_account_immediately()
    {
        var backoff = new SignInBackoff();
        for (var i = 0; i < 6; i++)
        {
            backoff.RecordFailure("k", T0);
        }
        Assert.NotNull(backoff.RetryAfter("k", T0));

        backoff.Clear("k");

        Assert.Null(backoff.RetryAfter("k", T0));
    }

    [Fact]
    public void Failures_are_counted_per_key_so_one_account_cannot_slow_another_down()
    {
        var backoff = new SignInBackoff();
        for (var i = 0; i < 6; i++)
        {
            backoff.RecordFailure("victim", T0);
        }

        Assert.NotNull(backoff.RetryAfter("victim", T0));
        Assert.Null(backoff.RetryAfter("bystander", T0));
    }

    [Fact]
    public void An_unknown_identifier_accrues_a_key_of_its_own()
    {
        // The enumeration guard: if only real accounts were counted, a 429 would mean "this
        // one exists". Both keys are ordinary strings and both accrue.
        var backoff = new SignInBackoff();
        for (var i = 0; i < 5; i++)
        {
            backoff.RecordFailure(SignInBackoff.KeyForIdentifier("nobody@example.com"), T0);
        }

        Assert.NotNull(backoff.RetryAfter(SignInBackoff.KeyForIdentifier("nobody@example.com"), T0));
    }

    [Fact]
    public void An_identifier_key_ignores_case_and_surrounding_space()
    {
        Assert.Equal(
            SignInBackoff.KeyForIdentifier("Cook@Example.com"),
            SignInBackoff.KeyForIdentifier("  cook@example.com "));
    }

    [Fact]
    public void The_password_curve_and_the_code_curve_are_separate()
    {
        // One person fumbling their password and then their code is one person having one
        // bad minute; compounding the two would make an honest mistake cost twice.
        var userId = Guid.NewGuid();
        Assert.NotEqual(SignInBackoff.KeyForAccount(userId), SignInBackoff.KeyForPassword(userId));
    }

    [Fact]
    public void An_account_has_ONE_password_curve_however_many_names_it_signs_in_under()
    {
        // An account has a username AND an email, and both sign in. Counting per submitted
        // string would hand an attacker two free-failure allowances and two curves per
        // victim for the cost of alternating between them.
        var userId = Guid.NewGuid();
        Assert.Equal(SignInBackoff.KeyForPassword(userId), SignInBackoff.KeyForPassword(userId));
        Assert.NotEqual(SignInBackoff.KeyForPassword(userId), SignInBackoff.KeyForPassword(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 2)]
    [InlineData(5, 4)]
    [InlineData(6, 8)]
    [InlineData(11, 256)]
    [InlineData(12, 300)]
    [InlineData(50, 300)]
    public void The_curve_is_two_seconds_doubling_to_a_five_minute_ceiling(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), SignInBackoff.DelayAfter(failures));
    }
}
