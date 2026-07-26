using FluentAssertions;
using Xunit;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Tests;

public class IbanValidatorTests
{
    // The IBAN reported from Neon as blocking a payroll run. Confirms the validator is right (the value
    // is genuinely invalid) and that the check-digit fix yields the exact valid IBAN we hand to the user.
    [Fact]
    public void ReportedIban_IsInvalid_AndCheckDigitFixProducesTheExpectedValidIban()
    {
        const string bad = "SA4420000009876543219876";
        IbanValidator.IsValid(bad).Should().BeFalse("the reported IBAN fails ISO 13616 mod-97");

        var corrected = IbanValidator.WithValidCheckDigits(bad);
        corrected.Should().Be("SA4820000009876543219876");
        IbanValidator.IsValid(corrected).Should().BeTrue();
    }

    // Every seeded demo IBAN now routes through WithValidCheckDigits, so whatever wrong check digits a
    // literal had, the persisted value is always a valid IBAN with the country code + BBAN preserved.
    [Theory]
    [InlineData("SA4420000009876543219876")]
    [InlineData("SA6020000000000000000001")]
    [InlineData("SA3260000007777777777777")]
    [InlineData("SA4400000000000000000000")]
    public void WithValidCheckDigits_OutputIsAlwaysValid_AndPreservesCountryAndBban(string input)
    {
        var corrected = IbanValidator.WithValidCheckDigits(input);

        IbanValidator.IsValid(corrected).Should().BeTrue();
        corrected[..2].Should().Be(input[..2]);      // country code unchanged
        corrected[4..].Should().Be(input[4..]);      // BBAN (account body) unchanged
        corrected.Length.Should().Be(input.Length);  // only the 2 check digits change
    }

    [Fact]
    public void WithValidCheckDigits_IsIdempotent()
    {
        var once = IbanValidator.WithValidCheckDigits("SA4420000009876543219876");
        IbanValidator.WithValidCheckDigits(once).Should().Be(once);
    }
}
