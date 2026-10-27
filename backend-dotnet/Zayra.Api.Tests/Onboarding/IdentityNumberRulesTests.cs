using FluentAssertions;
using Zayra.Api.Application.Employees.Onboarding;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

public class IdentityNumberRulesTests
{
    [Fact]
    public void IdentityRules_SaudiWithIqamaPrefix_Hard_ExpatWithNationalPrefix_Warning()
    {
        var saudiWithIqama = IdentityNumberRules.Check("2000000006", IdentityKinds.IdNumber, NationalityClass.Saudi);
        saudiWithIqama.HardErrors.Should().Contain(EmployeeFieldErrors.IdPrefixNationalityMismatch);

        var expatWithNational = IdentityNumberRules.Check("1000000008", IdentityKinds.IdNumber, NationalityClass.Expat);
        expatWithNational.HardErrors.Should().BeEmpty();
        expatWithNational.Warnings.Should().Contain(EmployeeWarningCodes.IdPrefixNationalityMismatch);

        IdentityNumberRules.Check("1000000008", IdentityKinds.IdNumber, NationalityClass.Saudi).HardErrors.Should().BeEmpty();
        IdentityNumberRules.Check("2000000006", IdentityKinds.IqamaNumber, NationalityClass.Expat).HardErrors.Should().BeEmpty();
    }

    [Fact]
    public void BadChecksum_IsHard_AndFormatIsHard()
    {
        IdentityNumberRules.Check("1000000009", IdentityKinds.IdNumber, NationalityClass.Saudi)
            .HardErrors.Should().Contain(EmployeeFieldErrors.IdChecksumInvalid);
        IdentityNumberRules.Check("2000000007", IdentityKinds.IqamaNumber, NationalityClass.Expat)
            .HardErrors.Should().Contain(EmployeeFieldErrors.IdChecksumInvalid);
        foreach (var v in HireVectors.Strings("saudiNationalId", "invalidFormat").Where(x => x.Length > 0))
            IdentityNumberRules.Check(v, IdentityKinds.IdNumber, null).HardErrors.Should().Contain(EmployeeFieldErrors.IdFormatInvalid, v);
        // Wrong leading digit for the class is a format failure, not a checksum one.
        IdentityNumberRules.Check("3000000004", IdentityKinds.IdNumber, null).HardErrors.Should().Contain(EmployeeFieldErrors.IdFormatInvalid);
        IdentityNumberRules.Check("1000000008", IdentityKinds.IqamaNumber, null).HardErrors.Should().Contain(EmployeeFieldErrors.IdFormatInvalid);
    }

    [Fact]
    public void ArabicIndicDigits_AreNormalisedBeforeValidation()
    {
        foreach (var (input, expected) in HireVectors.DigitNormalisation())
        {
            var r = IdentityNumberRules.Check(input, IdentityKinds.IdNumber, NationalityClass.Saudi);
            r.Normalised.Should().Be(expected);
            r.HardErrors.Should().BeEmpty();
        }
    }

    [Fact]
    public void BorderNumber_FormatHard_ChecksumAdvisoryOnly()
    {
        foreach (var v in HireVectors.Strings("borderNumber", "valid"))
            IdentityNumberRules.Check(v, IdentityKinds.BorderNumber, NationalityClass.Expat).HardErrors.Should().BeEmpty();
        foreach (var v in HireVectors.Strings("borderNumber", "advisoryChecksumFailure"))
            IdentityNumberRules.Check(v, IdentityKinds.BorderNumber, NationalityClass.Expat).HardErrors.Should().BeEmpty();
        foreach (var v in HireVectors.Strings("borderNumber", "invalidFormat"))
            IdentityNumberRules.Check(v, IdentityKinds.BorderNumber, NationalityClass.Expat).HardErrors.Should().Contain(EmployeeFieldErrors.IdFormatInvalid);
    }

    [Fact]
    public void BlankValue_IsNotAnError()
    {
        var r = IdentityNumberRules.Check("  ", IdentityKinds.IdNumber, NationalityClass.Saudi);
        r.HardErrors.Should().BeEmpty();
        r.Normalised.Should().BeEmpty();
    }
}
