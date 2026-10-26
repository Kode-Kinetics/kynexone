using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Setup;
using Zayra.Api.Controllers;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// The old "Policy starter" setup studio is gone from the UI (#268). Its backend (api/setup-assistant/preview
/// and /apply, the model-backed draft generator) had no remaining caller and is removed. What still lives under
/// the same route prefix is the policy-intake family, which sits behind the policy_intake opt-in flag.
/// </summary>
public sealed class RetiredSetupStudioBackendTests
{
    private static readonly Assembly Api = typeof(CompanyProfile).Assembly;

    private static IEnumerable<string> Routes() =>
        Api.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t =>
            {
                var prefix = t.GetCustomAttributes<RouteAttribute>().Select(r => r.Template).FirstOrDefault() ?? "";
                return t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .SelectMany(m => m.GetCustomAttributes().OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>())
                    .Select(a => (prefix.TrimEnd('/') + "/" + (a.Template ?? "")).TrimEnd('/').ToLowerInvariant());
            });

    [Theory]
    [InlineData("Zayra.Api.Controllers.SetupAssistantController")]
    [InlineData("Zayra.Api.Application.Setup.ISetupAssistantService")]
    [InlineData("Zayra.Api.Infrastructure.Setup.SetupAssistantService")]
    [InlineData("Zayra.Api.Application.Setup.SetupPreviewResult")]
    [InlineData("Zayra.Api.Infrastructure.Setup.SetupStatutoryDefaults")]
    [InlineData("Zayra.Api.Application.Setup.ISetupStatutoryDefaults")]
    public void TheStudioGeneratorTypesAreGone(string typeName) =>
        Api.GetType(typeName).Should().BeNull($"{typeName} only served the retired studio");

    [Fact]
    public void NoRouteServesStudioPreviewOrApply()
    {
        var routes = Routes().ToList();
        routes.Should().NotContain("api/setup-assistant/preview");
        routes.Should().NotContain("api/setup-assistant/apply");
    }

    [Fact]
    public void ThePolicyIntakeRoutesBehindTheKillSwitchAreStillThere()
    {
        var routes = Routes().ToList();
        routes.Should().Contain("api/setup-assistant/policy/extract");
        routes.Should().Contain("api/setup-assistant/policy/reviews");
    }

    [Fact]
    public void CompanyProfileNoLongerCarriesTheUnreadApprovalModelOrStrictEntityScope()
    {
        var props = typeof(CompanyProfile).GetProperties().Select(p => p.Name).ToList();
        props.Should().NotContain("ApprovalModel");
        props.Should().NotContain("StrictEntityScope");
    }

    [Fact]
    public void TheModelFreeCompanyDefaultsTheCreateCompanyWizardStartsFromRemainWithANewHome()
    {
        var type = Api.GetType("Zayra.Api.Infrastructure.Setup.SetupDraftDefaults");
        type.Should().NotBeNull();
        var draft = (SetupDraft)type!.GetMethod("DeterministicCompanyDefaults")!.Invoke(null, ["SA", "SAR", "Acme"])!;
        draft.Departments.Should().NotBeEmpty();
        draft.WorkingWeek!.WorkWeek.Should().Be("Sun-Thu");
        draft.HolidayCalendar.Should().NotBeNull();
    }

    [Fact]
    public void TheDraftAttendancePolicyNoLongerCarriesTheFiveSettingsNothingReads()
    {
        var props = typeof(DraftAttendancePolicy).GetProperties().Select(p => p.Name).ToList();
        props.Should().NotContain(["LateThresholdMinutes", "AbsentThresholdMinutes", "RoundingRule", "RequiresOvertimeApproval", "AllowAbsenceToLeaveConversion"]);
        props.Should().Contain(["Code", "Name", "GraceMinutes", "EarlyExitThresholdMinutes", "HalfDayThresholdMinutes", "StandardWorkMinutes", "BreakMinutes"]);
    }

    [Fact]
    public void TheEntityAndTheModelNoLongerMapTheSixteenUnreadColumns()
    {
        string[] hr = ["UseDeptHeadApproval", "UseHrFinalApproval", "UseSupervisorBeforeManager", "AllowDottedLineApproval", "AutoCreateDeptOnImport",
            "AutoCreateDesignationOnImport", "RequireImportPreviewBeforeCommit", "AllowCrossDeptManager", "AllowCrossLocationManager",
            "RequireCostCenterForPayroll", "RequireGradeForApprovalPolicy"];
        string[] attendance = ["LateThresholdMinutes", "AbsentThresholdMinutes", "RoundingRule", "RequiresOvertimeApproval", "AllowAbsenceToLeaveConversion"];
        typeof(Zayra.Api.Domain.Entities.TenantHrConfig).GetProperties().Select(p => p.Name).Should().NotContain(hr);
        typeof(Zayra.Api.Models.AttendancePolicy).GetProperties().Select(p => p.Name).Should().NotContain(attendance);
    }
}
