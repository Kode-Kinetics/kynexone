using FluentAssertions;
using Zayra.Api.Application.Employees.Onboarding;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

public class PhoneNormalizerTests
{
    [Fact]
    public void PhoneE164_Vectors_ForSaudiCompany()
    {
        foreach (var (input, expected) in HireVectors.Pairs("phoneE164", "cases", "input", "normalised"))
            PhoneNormalizer.Normalize(input, "SA", PhoneKind.Mobile).Should().Be(expected, $"input '{input}'");
    }

    [Fact]
    public void Mobile0412345678_IsRejected_ButLandlineAcceptedOnlyAsLandline()
    {
        PhoneNormalizer.Normalize("0412345678", "SA", PhoneKind.Mobile).Should().BeNull();
        PhoneNormalizer.Normalize("0112345678", "SA", PhoneKind.Mobile).Should().BeNull();
        PhoneNormalizer.Normalize("0112345678", "SA", PhoneKind.Landline).Should().Be("+966112345678");
        PhoneNormalizer.Normalize("0512345678", "SA", PhoneKind.Landline).Should().BeNull();
    }

    [Fact]
    public void NonSaudiCompany_RequiresInternationalPrefix()
    {
        PhoneNormalizer.Normalize("+971501234567", "AE", PhoneKind.Mobile).Should().Be("+971501234567");
        PhoneNormalizer.Normalize("0501234567", "AE", PhoneKind.Mobile).Should().BeNull();
        PhoneNormalizer.Normalize(null, "SA", PhoneKind.Mobile).Should().BeNull();
        PhoneNormalizer.Normalize("  ", "SA", PhoneKind.Mobile).Should().BeNull();
    }
}
