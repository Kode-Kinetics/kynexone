using FluentAssertions;
using Zayra.Api.Application.Setup;
using Zayra.Api.Infrastructure.Setup;

namespace Zayra.Api.Tests;

public sealed class SetupConfigurationTests
{
    internal static DraftGrade Grade(string code = "CUSTOM") => new(code, "Custom grade", "Staff", 2, 4000, 5000, 6000, "SAR");
    internal static DraftLeavePolicy Leave(string? grade = "CUSTOM") => new(
        "Annual custom", "ANNUAL", 24, "Monthly", true, 0, 1, 30, 7, false, false, true, "Full", true, grade, "OPS", "Permanent");
    internal static DraftBenefitPlan Benefit(string? grade = "CUSTOM") => new(
        "MEDICAL", "Medical plan", "Medical", "SAR", "2026-01-01", null, true, grade is null ? [] : [grade]);
    internal static CompanyProfile Profile(SetupConfiguration? configuration = null) => new(
        "SA", "Retail", "51-200", "SAR", null, "Policy Co", "Riyadh", "Functional", "GradeBased",
        new(true, true, true, true, true, true, Benefits: true), Configuration: configuration);
    [Fact]
    public void InvalidManualValuesAndUnsupportedLeaveYearAreRefusedAtValidation()
    {
        SetupDraftDefaults.ValidateConfiguration(Profile(new(Grades: [Grade() with { MinSalary = 7000 }]))).Should().NotBeEmpty();
        SetupDraftDefaults.ValidateConfiguration(Profile() with { PayCycle = "Weekly" }).Should().Contain(e => e.Contains("monthly salary amounts"));
        SetupDraftDefaults.ValidateConfiguration(Profile() with { LeaveYearBasis = "JoiningDate" }).Should().Contain(e => e.Contains("calendar leave years"));
        SetupDraftDefaults.ValidateConfiguration(Profile(new(PolicySourceText: new string('x', 12001)))).Should().NotBeEmpty();
    }

    [Fact]
    public void AmbiguousLeaveScopesPrecisionAndSalaryValuesFailClosed()
    {
        var overlap = SetupDraft.Empty() with { LeavePolicies = [Leave() with { EmploymentType = "" }, Leave(null)] };
        SetupDraftDefaults.ValidateDraftValues(overlap, "SAR").Should().Contain(e => e.Contains("overlap"));
        var nested = SetupDraft.Empty() with { LeavePolicies = [Leave(null) with { DepartmentCode = null, EmploymentType = null }, Leave()] };
        SetupDraftDefaults.ValidateDraftValues(nested, "SAR").Should().BeEmpty();
        foreach (var bad in new[]
        {
            SetupDraft.Empty() with { Grades = [Grade() with { MidSalary = 5000.555m }] },
            SetupDraft.Empty() with { LeavePolicies = [Leave() with { EmploymentType = " Permanent " }] },
            SetupDraft.Empty() with { PayComponents = [new("BASIC", "Basic", "Earning", "Fixed", -1, 0, false)] },
            SetupDraft.Empty() with { GradePayComponents = [new("CUSTOM", "BASIC", "Basic", "Earning", "Fixed", 1000, 101, false, "Monthly")] },
        }) SetupDraftDefaults.ValidateDraftValues(bad, "SAR").Should().NotBeEmpty();
    }
}
