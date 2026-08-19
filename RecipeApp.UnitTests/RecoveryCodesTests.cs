using RecipeApp.Infrastructure.Auth;

namespace RecipeApp.UnitTests;

// Accounts (KAN-21): the codes that answer a challenge when the phone is gone.
public class RecoveryCodesTests
{
    [Fact]
    public void Ten_codes_are_issued_and_no_two_are_the_same()
    {
        var codes = RecoveryCodes.Generate();

        Assert.Equal(RecoveryCodes.Count, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void A_code_carries_no_character_that_can_be_misread()
    {
        // The whole reason the alphabet is not plain base32: these are transcribed by hand
        // from a printout, and I/1, L/1, O/0 are the transcription errors that cost access.
        foreach (var code in RecoveryCodes.Generate())
        {
            Assert.DoesNotContain('I', code);
            Assert.DoesNotContain('L', code);
            Assert.DoesNotContain('O', code);
            Assert.DoesNotContain('U', code);
            Assert.Matches("^[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$", code);
        }
    }

    [Fact]
    public void The_same_code_typed_carelessly_hashes_the_same_way()
    {
        var code = RecoveryCodes.Generate(1)[0];
        var expected = RecoveryCodes.Hash(code);

        Assert.Equal(expected, RecoveryCodes.Hash(code.ToLowerInvariant()));
        Assert.Equal(expected, RecoveryCodes.Hash(code.Replace("-", string.Empty)));
        Assert.Equal(expected, RecoveryCodes.Hash($"  {code.Replace("-", " ")}  "));
    }

    [Fact]
    public void The_plaintext_is_not_recoverable_from_the_digest()
    {
        var code = RecoveryCodes.Generate(1)[0];

        var hash = RecoveryCodes.Hash(code);

        Assert.DoesNotContain(RecoveryCodes.Normalise(code), hash);
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void A_recovery_code_and_a_TOTP_code_are_never_mistaken_for_each_other()
    {
        // ADR-0008's condition rests on this: backoff gates codes and must not gate recovery
        // codes, and shape is the only thing available to tell them apart before a lookup.
        foreach (var code in RecoveryCodes.Generate())
        {
            Assert.True(RecoveryCodes.LooksLikeCode(code));
            Assert.False(Totp.LooksLikeCode(code));
        }

        Assert.False(RecoveryCodes.LooksLikeCode("123456"));
        Assert.False(RecoveryCodes.LooksLikeCode("ABCDE-FGHI"));  // I is not in the alphabet
        Assert.False(RecoveryCodes.LooksLikeCode(string.Empty));
    }
}
