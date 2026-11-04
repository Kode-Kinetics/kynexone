using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

public class ReconDriverMapTests
{
    [Theory]
    [InlineData("Salary", "BASIC", null, null, ReconDriver.Package)]
    [InlineData("Salary", "HOUSING", null, null, ReconDriver.Package)]
    [InlineData("Arrears", "ARREARS_BASIC", null, null, ReconDriver.Arrears)]
    [InlineData("Overtime", "OVERTIME", null, null, ReconDriver.Variable)]
    [InlineData("Bonus", "BONUS_ANNUAL", null, null, ReconDriver.Variable)]
    [InlineData("Adjustment", "ADJ_TAXI", null, null, ReconDriver.Variable)]
    [InlineData("Attendance", "ABSENCE", null, null, ReconDriver.Attendance)]
    [InlineData("Leave", "UNPAID_LEAVE", null, null, ReconDriver.Attendance)]
    [InlineData("Loan", "LOAN_EMI", null, null, ReconDriver.Loans)]
    [InlineData("Recovery", "RECEIVABLE_RECOVERY", null, null, ReconDriver.Loans)]
    [InlineData("Statutory", "GOSI-ANN-EE", null, null, ReconDriver.Statutory)]
    [InlineData("Tax", "INCOME_TAX", null, null, ReconDriver.Statutory)]
    public void TheLineSource_DecidesTheDriver(string source, string code, string? provider, string? calc, ReconDriver expected) =>
        ReconDriverMap.Classify(source, code, provider is null && calc is null ? null : new ComponentHint(code, provider, calc))
            .Should().Be(expected);

    [Theory]
    [InlineData("Salary", "TELEWORK", "Integration", ReconDriver.Variable)]       // a provider-fed component is variable
    [InlineData("Salary", "TELEWORK", null, ReconDriver.Package)]                 // plain salary-sourced is package
    public void ASalarySourcedLineWithAProviderKey_IsVariable(string source, string code, string? provider, ReconDriver expected) =>
        ReconDriverMap.Classify(source, code, new ComponentHint(code, provider, "Fixed")).Should().Be(expected);

    [Theory]
    [InlineData("Fixed")]
    [InlineData("StructureField")]
    [InlineData("PercentOfBasic")]
    [InlineData("PercentOfGross")]
    public void AnUnknownSource_WithAFixedStyleCatalogCalcMethod_IsPackage(string calc) =>
        ReconDriverMap.Classify("Mystery", "X", new ComponentHint("X", null, calc)).Should().Be(ReconDriver.Package);

    [Fact]
    public void AnUnknownSource_WithAProviderKey_IsVariable() =>
        ReconDriverMap.Classify("Mystery", "X", new ComponentHint("X", "Bonus", "Integration")).Should().Be(ReconDriver.Variable);

    [Theory]
    [InlineData("Mystery", "X")]
    [InlineData("", "X")]
    [InlineData(null, "X")]
    [InlineData("Settlement", "EOSB_GRATUITY")]
    public void AnythingUnrecognised_IsOther_NeverGuessed(string? source, string code) =>
        ReconDriverMap.Classify(source, code, null).Should().Be(ReconDriver.Other);

    [Fact]
    public void TheSourceMatch_IgnoresCase() =>
        ReconDriverMap.Classify("loan", "LOAN_EMI", null).Should().Be(ReconDriver.Loans);

    [Fact]
    public void BucketNames_AreTheContractProperties()
    {
        ReconDriverMap.BucketName(ReconDriver.Package).Should().Be("package");
        ReconDriverMap.BucketName(ReconDriver.Proration).Should().Be("proration");
        ReconDriverMap.BucketName(ReconDriver.Loans).Should().Be("loans");
        ReconDriverMap.BucketName(ReconDriver.Other).Should().Be("other");
    }
}
