using System.Reflection;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

public sealed class InvitationPasswordUnicodePolicyTests
{
    [Fact]
    public void Policy_CountsUnicodeScalars_NotUtf16CodeUnits()
    {
        Assert.IsType<InvalidOperationException>(Validate("Aa1!aaaa\U0001F600"));
        Assert.Null(Validate("Aa1!aaaaa\U0001F600"));
    }

    [Fact]
    public void Policy_UsesUnicodeRuneCategories()
    {
        Assert.Null(Validate("\U00010400\U00010428\u0661!aaaaaa"));
    }

    [Theory]
    [InlineData("Aa1!aaaaa\u200D")]
    [InlineData("Aa1!aaaaa\n")]
    public void Policy_RejectsFormatAndControlScalars(string password)
    {
        Assert.IsType<InvalidOperationException>(Validate(password));
    }

    [Fact]
    public void Policy_RejectsInvalidUtf16()
    {
        var password = "Aa1!aaaaa" + '\uD800';

        Assert.IsType<InvalidOperationException>(Validate(password));
    }

    [Fact]
    public void Policy_DoesNotClampStoredMinimumTo64()
    {
        var policy = new SecuritySetting { PasswordMinLength = 65 };

        Assert.IsType<InvalidOperationException>(Validate("Aa1!" + new string('a', 60), policy));
        Assert.Null(Validate("Aa1!" + new string('a', 61), policy));
    }

    [Fact]
    public void Policy_DoesNotTreatUnicodeMarksAsSpecialCharacters()
    {
        var policy = new SecuritySetting
        {
            PasswordRequireUppercase = false,
            PasswordRequireLowercase = false,
            PasswordRequireDigit = false,
            PasswordRequireSpecial = true
        };

        Assert.IsType<InvalidOperationException>(Validate("aaaaaaaaa\u0301", policy));
        Assert.Null(Validate("aaaaaaaaa\u20AC", policy));
    }

    private static Exception? Validate(string password, SecuritySetting? policy = null)
    {
        var method = typeof(AuthService).GetMethod(
            "ValidatePasswordAgainstPolicy",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        try
        {
            method!.Invoke(null, [password, policy]);
            return null;
        }
        catch (TargetInvocationException exception)
        {
            return exception.InnerException;
        }
    }
}
