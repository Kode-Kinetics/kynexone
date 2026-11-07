using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// PAY-08 — a nationality that is not Saudi, GCC or a known country is never silently treated as an expat for GOSI:
/// the run carries a named finding. The finding is an Error that an approver may override with a reason
/// (the same class as the other GOSI-classification judgements); a blank nationality stays MISSING_NATIONALITY.
/// </summary>
public class PayrollNationalityUnrecognisedTests
{
    private static List<PayrollValidationResult> Run(string nationality, string country = "SA")
    {
        var tid = Guid.NewGuid();
        var run = new PayrollRun { Id = Guid.NewGuid(), TenantId = tid, Year = 2026, Month = 6, Status = "Processed",
            TotalGrossSalary = 10_000m, TotalDeductions = 1_000m, TotalNetSalary = 9_000m };
        var emp = new Employee { Id = 1, TenantId = tid, EmployeeCode = "EMP001", FullName = "N", Nationality = nationality, Status = "Active" };
        var slip = new PayrollSlip { Id = Guid.NewGuid(), TenantId = tid, RunId = run.Id, EmployeeId = 1, EmployeeCode = "EMP001",
            EmployeeName = "N", GrossSalary = 10_000m, Deductions = 1_000m, NetSalary = 9_000m, BasicSalary = 8_000m, HousingAllowance = 2_000m };
        var company = new Company { Id = Guid.NewGuid(), TenantId = tid, LegalNameEn = "Co", CountryCode = country, DefaultCurrency = "SAR", IsActive = true };
        return PayrollValidationEngine.Run(new PayrollValidationContext(run, [slip], [emp], [], [], [], [], company));
    }

    [Theory]
    [InlineData("Suadi")]          // typo for Saudi: must not become an expat who pays no GOSI
    [InlineData("-")]
    [InlineData("Atlantean")]
    public void Unrecognised_nationality_is_flagged_by_name(string nationality)
    {
        var finding = Run(nationality).Should().ContainSingle(r => r.Code == "NATIONALITY_UNRECOGNISED").Subject;
        finding.Severity.Should().Be("Error");
        finding.EmployeeId.Should().Be(1);
        finding.Message.Should().Contain(nationality);
    }

    [Theory]
    [InlineData("Saudi")]
    [InlineData("KSA")]
    [InlineData("Kuwaiti")]
    [InlineData("Egyptian")]
    [InlineData("India")]
    [InlineData("PK")]
    public void Recognised_nationalities_are_not_flagged(string nationality)
        => Run(nationality).Should().NotContain(r => r.Code == "NATIONALITY_UNRECOGNISED");

    [Fact]
    public void Blank_nationality_stays_the_existing_missing_warning()
    {
        var results = Run("");
        results.Should().NotContain(r => r.Code == "NATIONALITY_UNRECOGNISED");
        results.Should().Contain(r => r.Code == "MISSING_NATIONALITY" && r.Severity == "Warning");
    }

    [Fact]
    public void Outside_ksa_the_gosi_classification_finding_does_not_apply()
        => Run("Atlantean", country: "AE").Should().NotContain(r => r.Code == "NATIONALITY_UNRECOGNISED");

    [Fact]
    public void The_finding_is_overridable_with_a_reason_and_default_deny_holds_for_other_codes()
    {
        PayrollValidationOverridePolicy.IsOverridable("NATIONALITY_UNRECOGNISED").Should().BeTrue();
        PayrollValidationOverridePolicy.IsOverridable("SOME_FUTURE_CODE").Should().BeFalse();
    }
}
