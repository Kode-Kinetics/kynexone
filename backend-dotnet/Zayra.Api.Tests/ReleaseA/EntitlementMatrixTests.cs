using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Release A slice R1 — benefits by grade. The Masar Holding demo matrix (plan §5) drives the tests: Masar Facility
/// Services adopts the group grid; Masar Logistics skips Education and tailors G1 per diem to SAR 200.
/// </summary>
public class EntitlementMatrixTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static readonly DateOnly Nov1 = new(2026, 11, 1);
    private static readonly DateOnly Dec1 = new(2026, 12, 1);

    // ── Matrix CRUD and publish ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishingTheMasarGrid_WritesOneCellPerGradeAndBenefit_AuditsEach_AndLeavesNoGaps()
    {
        await using var h = await H.Create();
        var result = await h.PublishGroup(Nov1, h.MasarGrid());

        var body = Ok<PublishMatrixResult>(result);
        body.Published.Should().Be(35);
        body.GapsRemaining.Should().Be(0);
        (await h.Db.GradeEntitlements.CountAsync()).Should().Be(35);
        (await h.Db.AuditLogs.CountAsync(a => a.Action == EntitlementMatrixService.AuditPublished)).Should().Be(35);
        (await h.Db.GradeEntitlements.AllAsync(c => c.CompanyId == null && c.EffectiveFrom == Nov1 && c.SourceRule == EntitlementMatrixService.SourceRuleMatrix))
            .Should().BeTrue();

        // Before 1 Nov nothing is in force yet, and every gap says when its published value starts; from 1 Nov none remain.
        (await h.Read(null, Today)).Gaps.Should().HaveCount(35).And.OnlyContain(g => g.ScheduledFrom == Nov1);
        var matrix = await h.Read(null, Nov1);
        matrix.Gaps.Should().BeEmpty();
        matrix.Grades.Select(g => g.Code).Should().Equal("G1", "G2", "G3", "G4", "G5");
        var medicalG3 = matrix.Cells.Single(c => c.GradeId == h.G[3].Id && c.ComponentCode == "MEDICAL");
        (medicalG3.CoverageTier, medicalG3.DependantScope, medicalG3.LimitPeriod).Should().Be(("B", "Family", "PerTerm"));
        matrix.Components.Single(c => c.Code == "HOUSING").FloorReason!.Code.Should().Be(ReleaseABlockReasons.EntitlementFloorHousing);
        matrix.Components.Single(c => c.Code == "MEDICAL").Should().Match<MatrixComponentDto>(c => c.IsFloor && !c.CanBeSkipped && c.Group == "Contract");
        matrix.Components.Single(c => c.Code == "EDUCATION").CanBeSkipped.Should().BeTrue();
        // A loan facility is listed (read-only) only once a loan type uses it, under the loan type's own name.
        matrix.Components.Should().NotContain(c => c.IsLoanFacility);
        h.Db.LoanTypes.Add(new LoanType { TenantId = h.Tid, Code = "HOUSING_ADVANCE", NameEn = "Housing advance", NameAr = "سلفة السكن",
            MaxInstallments = 12, GradeLimited = true, EntitlementComponentCode = "LOAN_HOUSING_ADVANCE" });
        await h.Db.SaveChangesAsync();
        (await h.Read(null, Nov1)).Components.Single(c => c.IsLoanFacility)
            .Should().Match<MatrixComponentDto>(c => c.Code == "LOAN_HOUSING_ADVANCE" && !c.CanBeSkipped && c.Group == "Facility");
    }

    [Fact]
    public async Task GradeStandard_ReadsTheMatrix_ForTheResolver()
    {
        await using var h = await H.Create();
        Ok<PublishMatrixResult>(await h.PublishGroup(Nov1, h.MasarGrid()));

        var g3 = await h.Service.GradeStandardLinesAsync(h.Tid, h.G[3].Id, h.Facility.Id, Nov1, default);
        g3.Select(l => l.ComponentCode).Should().Equal("AIR_TICKET", "EDUCATION", "HOUSING", "MEDICAL", "OTHER_ALLOWANCES", "PER_DIEM", "TRANSPORT");
        var housing = g3.Single(l => l.ComponentCode == "HOUSING");
        (housing.Class, housing.Floor, housing.ValueType, housing.Rate, housing.Offered).Should().Be(("QiwaWage", "Housing", "PercentOfBasic", 0.25m, true));
        var ticket = g3.Single(l => l.ComponentCode == "AIR_TICKET");
        (ticket.Quantity, ticket.CoverageTier, ticket.NationalityScope).Should().Be(((short?)1, "Economy", "NonSaudi"));
        g3.Single(l => l.ComponentCode == "EDUCATION").Eligible.Should().BeFalse();
        g3.Single(l => l.ComponentCode == "PER_DIEM").Should().Match<Zayra.Api.Application.Entitlements.GradeStandardLine>(
            l => l.Amount == 250m && l.Class == "Facility" && !l.IsCompanyOverride);
        (await h.Service.GradeStandardLinesAsync(h.Tid, h.G[3].Id, h.Facility.Id, Today, default)).Should().BeEmpty("nothing is in force before 1 Nov");
    }

    [Fact]
    public async Task Publish_IsCloseOnly_FutureDated_AndNeverOverwrites()
    {
        await using var h = await H.Create();
        Ok<PublishMatrixResult>(await h.PublishGroup(Nov1, h.MasarGrid()));

        // Raise G3 housing 25% → 30% from 1 Dec: the November version is closed on 30 Nov, a new one opens.
        var raise = Ok<PublishMatrixResult>(await h.PublishGroup(Dec1, [H.Pct(h.G[3], "HOUSING", 0.30m), H.Pct(h.G[4], "HOUSING", 0.25m)]));
        (raise.Published, raise.Unchanged).Should().Be((1, 1));
        var versions = await h.Db.GradeEntitlements.Where(c => c.GradeId == h.G[3].Id && c.PayComponentCode == "HOUSING").OrderBy(c => c.EffectiveFrom).ToListAsync();
        versions.Select(v => (v.Rate, v.EffectiveFrom, v.EffectiveTo)).Should().Equal((0.25m, Nov1, (DateOnly?)Dec1.AddDays(-1)), (0.30m, Dec1, (DateOnly?)null));

        // A date before a scheduled version is refused, as is a past date. (The same day again: see SameDayRepublish_….)
        Error(await h.PublishGroup(Nov1.AddDays(10), [H.Pct(h.G[3], "HOUSING", 0.35m)])).Should().Be("later_version_exists");
        Error(await h.PublishGroup(Today.AddDays(-1), [H.Pct(h.G[3], "HOUSING", 0.35m)])).Should().Be("effective_from_in_past");
        Error(await h.PublishGroup(Dec1, [])).Should().Be("no_cells");
    }

    [Fact]
    public async Task DryRun_WritesNothing_AndCountsWhoIsAffectedNowAndAtRenewal()
    {
        await using var h = await H.Create();
        h.Employee(h.Facility, h.G[1]); h.Employee(h.Facility, h.G[1]); h.Employee(h.Facility, h.G[3]);
        h.Employee(h.Logistics, h.G[1]);
        h.Employee(h.Logistics, h.G[1], status: EmployeeStatuses.Archived);
        await h.Db.SaveChangesAsync();
        Ok<PublishMatrixResult>(await h.PublishCompany(h.Logistics, Nov1, [H.Amount(h.G[1], "PER_DIEM", 200m)]));
        var before = await h.Db.GradeEntitlements.CountAsync();

        // Per diem is a facility (read when used): it reaches employees on the date. Housing is fixed per contract year.
        var dry = Ok<PublishMatrixResult>(await h.PublishGroup(Nov1, [H.Amount(h.G[1], "PER_DIEM", 175m), H.InKind(h.G[3], "HOUSING")], dryRun: true));
        dry.DryRun.Should().BeTrue();
        dry.Published.Should().Be(2);
        dry.AffectedNow.Should().Be(2, "the two Facility Services G1s; Masar Logistics has its own G1 per diem");
        dry.AffectedAtRenewal.Should().Be(1, "the Facility Services G3");
        (await h.Db.GradeEntitlements.CountAsync()).Should().Be(before);
        (await h.Db.AuditLogs.CountAsync(a => a.Action == EntitlementMatrixService.AuditPublished)).Should().Be(1);
    }

    // ── Statutory floors and permitted criteria ──────────────────────────────────────────────────

    [Theory]
    [InlineData("HOUSING", ReleaseABlockReasons.EntitlementFloorHousing)]
    [InlineData("TRANSPORT", ReleaseABlockReasons.EntitlementFloorTransport)]
    [InlineData("MEDICAL", ReleaseABlockReasons.EntitlementFloorMedical)]
    public async Task AFloorComponent_CannotBeMarkedNotOffered_ForAnyGrade(string code, string blockCode)
    {
        await using var h = await H.Create();
        var result = await h.PublishGroup(Nov1, [H.NotOffered(h.G[1], code)]);
        var error = CellErrors(result).Single();
        error.Code.Should().Be(blockCode);
        error.Reason!.TitleAr.Should().NotBeNullOrWhiteSpace();
        (await h.Db.GradeEntitlements.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Medical_CannotWaitForProbation_BeNationalityScoped_OrDropTheFamily()
    {
        await using var h = await H.Create();
        var probation = H.Tier(h.G[1], "CchiBasic"); probation.AfterProbation = true;
        var saudiOnly = H.Tier(h.G[2], "B"); saudiOnly.NationalityScope = NationalityScopes.Saudi; saudiOnly.NationalityBasis = "Policy";
        var employeeOnly = H.Tier(h.G[3], "A"); employeeOnly.DependantScope = DependantScopes.None;
        var errors = CellErrors(await h.PublishGroup(Nov1, [probation, saudiOnly, employeeOnly]));
        errors.Select(e => e.Code).Should().Equal(ReleaseABlockReasons.EntitlementFloorMedical, ReleaseABlockReasons.EntitlementFloorMedical,
            "dependants_not_allowed");
    }

    [Fact]
    public async Task ANationalityCondition_NeedsItsLegalBasis_AndBannedCriteriaAreRefusedOutLoud()
    {
        await using var h = await H.Create();
        var ticket = H.Ticket(h.G[1], 1, "Economy");
        ticket.NationalityScope = NationalityScopes.NonSaudi;
        CellErrors(await h.PublishGroup(Nov1, [ticket])).Single().Code.Should().Be(ReleaseABlockReasons.EntitlementReasonRequired);

        // A client that sends a banned condition (it has no column, so it would otherwise be dropped silently) is told no.
        var json = $$"""{"companyId":null,"effectiveFrom":"2026-11-01","cells":[{"gradeId":"{{h.G[2].Id}}","componentCode":"PER_DIEM","eligible":true,"valueType":"Amount","amount":150,"gender":"Male"}]}""";
        var request = JsonSerializer.Deserialize<PublishMatrixRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        CellErrors(await h.Controller().Publish(h.Service, request, false, default)).Single().Code.Should().Be(ReleaseABlockReasons.EntitlementBannedCriterion);
        EntitlementMatrixService.IsBannedCriterion("maritalStatus").Should().BeTrue();
        EntitlementMatrixService.IsBannedCriterion("minAge").Should().BeFalse("only properties that ARE the criterion are recognised");
        EntitlementMatrixService.IsBannedCriterion("ageFrom").Should().BeTrue();
    }

    [Fact]
    public async Task ValueTypesFollowTheComponent_AndLoanCellsAreEditedInLoans()
    {
        await using var h = await H.Create();
        var ticketAsMoney = new MatrixCellInput { GradeId = h.G[1].Id, ComponentCode = "AIR_TICKET", Eligible = true, ValueType = "Amount", Amount = 1500m };
        var educationCount = H.Amount(h.G[4], "EDUCATION", 10_000m); educationCount.Quantity = 2;
        var loan = H.Amount(h.G[2], "LOAN_HOUSING_ADVANCE", 9_000m);
        var housingTier = new MatrixCellInput { GradeId = h.G[1].Id, ComponentCode = "HOUSING", Eligible = true, ValueType = "CoverageTier", CoverageTier = "VIP" };
        CellErrors(await h.PublishGroup(Nov1, [ticketAsMoney, educationCount, loan, housingTier])).Select(e => e.Code)
            .Should().Equal("value_type_not_allowed", "invalid_value", "loan_component_read_only", "value_type_not_allowed");
    }

    // ── Adopt / skip / tailor per company ────────────────────────────────────────────────────────

    [Fact]
    public async Task Masar_FacilityServicesAdopts_LogisticsSkipsEducationAndTailorsG1PerDiem()
    {
        await using var h = await H.Create();
        Ok<PublishMatrixResult>(await h.PublishGroup(Nov1, h.MasarGrid()));
        Ok<PublishMatrixResult>(await h.PublishCompany(h.Logistics, Nov1, [H.Amount(h.G[1], "PER_DIEM", 200m)]));
        Ok<MatrixOfferingDto>(await h.SetOffering(h.Logistics, "EDUCATION", offered: false, Nov1));

        var logistics = await h.Read(h.Logistics.Id, Nov1);
        Mode(logistics, "EDUCATION").Should().Be(EntitlementMatrixService.Modes.Skipped);
        Mode(logistics, "PER_DIEM").Should().Be(EntitlementMatrixService.Modes.Tailored);
        Mode(logistics, "HOUSING").Should().Be(EntitlementMatrixService.Modes.Adopted);
        logistics.Gaps.Should().BeEmpty();
        var perDiemG1 = logistics.Cells.Single(c => c.GradeId == h.G[1].Id && c.ComponentCode == "PER_DIEM");
        (perDiemG1.Amount, perDiemG1.IsCompanyOverride, perDiemG1.Inherited).Should().Be((200m, true, false));
        logistics.Cells.Single(c => c.GradeId == h.G[2].Id && c.ComponentCode == "PER_DIEM").Inherited.Should().BeTrue();

        var facility = await h.Read(h.Facility.Id, Nov1);
        facility.Offerings.Should().OnlyContain(o => o.Mode == EntitlementMatrixService.Modes.Adopted);
        facility.Cells.Single(c => c.GradeId == h.G[1].Id && c.ComponentCode == "PER_DIEM").Amount.Should().Be(150m);

        // The resolver's read: the company cell beats the group cell; the skip hides Education for Logistics only.
        var logisticsG4 = await h.Service.GradeStandardLinesAsync(h.Tid, h.G[4].Id, h.Logistics.Id, Nov1, default);
        logisticsG4.Single(l => l.ComponentCode == "EDUCATION").Offered.Should().BeFalse();
        (await h.Service.GradeStandardLinesAsync(h.Tid, h.G[4].Id, h.Facility.Id, Nov1, default)).Single(l => l.ComponentCode == "EDUCATION").Offered.Should().BeTrue();
        (await h.Service.GradeStandardLinesAsync(h.Tid, h.G[1].Id, h.Logistics.Id, Nov1, default)).Single(l => l.ComponentCode == "PER_DIEM")
            .Should().Match<Zayra.Api.Application.Entitlements.GradeStandardLine>(l => l.Amount == 200m && l.IsCompanyOverride);

        // The skip is a company pay_components row with is_offered = false; the group row is untouched.
        var rows = await h.Db.PayComponents.Where(p => p.Code == "EDUCATION").ToListAsync();
        rows.Should().ContainSingle(p => p.CompanyId == h.Logistics.Id && !p.IsOffered && p.EffectiveFrom == Nov1 && p.ComponentType == PayComponentTypes.Benefit);
        rows.Should().ContainSingle(p => p.CompanyId == null && p.IsOffered);
        (await h.Db.AuditLogs.CountAsync(a => a.Action == EntitlementMatrixService.AuditSkipped)).Should().Be(1);
    }

    [Fact]
    public async Task UseGroupDefault_DropsTheCompanyValue_SoTheCompanyAdoptsAgain()
    {
        await using var h = await H.Create();
        Ok<PublishMatrixResult>(await h.PublishGroup(Nov1, h.MasarGrid()));
        Ok<PublishMatrixResult>(await h.PublishCompany(h.Logistics, Nov1, [H.Amount(h.G[1], "PER_DIEM", 200m)]));

        var revert = Ok<PublishMatrixResult>(await h.PublishCompany(h.Logistics, Dec1,
            [new MatrixCellInput { GradeId = h.G[1].Id, ComponentCode = "PER_DIEM", UseGroupDefault = true }]));
        revert.Reverted.Should().Be(1);
        Mode(await h.Read(h.Logistics.Id, Dec1), "PER_DIEM").Should().Be(EntitlementMatrixService.Modes.Adopted);
        Mode(await h.Read(h.Logistics.Id, Nov1), "PER_DIEM").Should().Be(EntitlementMatrixService.Modes.Tailored);
        (await h.Db.AuditLogs.CountAsync(a => a.Action == EntitlementMatrixService.AuditReverted)).Should().Be(1);
        Error(await h.PublishGroup(Dec1, [new MatrixCellInput { GradeId = h.G[1].Id, ComponentCode = "PER_DIEM", UseGroupDefault = true }]))
            .Should().Be("invalid_cells");
    }

    [Theory]
    [InlineData("HOUSING", ReleaseABlockReasons.EntitlementFloorHousing)]
    [InlineData("TRANSPORT", ReleaseABlockReasons.EntitlementFloorTransport)]
    [InlineData("MEDICAL", ReleaseABlockReasons.EntitlementFloorMedical)]
    public async Task SkippingAFloorComponent_IsRefusedWithTheLegalReason(string code, string blockCode)
    {
        await using var h = await H.Create();
        var result = await h.SetOffering(h.Logistics, code, offered: false, Nov1);
        Status(result).Should().Be(409);
        Error(result).Should().Be(blockCode);
        Json(result).GetProperty("reason").GetProperty("whyAr").GetString().Should().NotBeNullOrWhiteSpace();
        (await h.Db.PayComponents.AnyAsync(p => p.CompanyId != null)).Should().BeFalse();
    }

    [Fact]
    public async Task Offerings_StartOnAMonth_WageAndLoanRowsAreNotSwitchedHere_AndASkipCanBeWithdrawnOrEnded()
    {
        await using var h = await H.Create();
        Error(await h.SetOffering(h.Logistics, "EDUCATION", false, new DateOnly(2026, 11, 15))).Should().Be("effective_from_not_month_start");
        Error(await h.SetOffering(h.Logistics, "EDUCATION", false, new DateOnly(2026, 10, 1))).Should().Be("effective_from_in_past");
        Error(await h.SetOffering(h.Logistics, "OTHER_ALLOWANCES", false, Nov1)).Should().Be("wage_component_not_skippable");
        Error(await h.SetOffering(h.Logistics, "LOAN_HOUSING_ADVANCE", false, Nov1)).Should().Be("loan_offering_in_loans");
        Status(await h.SetOffering(h.Logistics, "NOT_A_BENEFIT", false, Nov1)).Should().Be(404);

        // A skip that has not started is withdrawn; skipping twice is a no-op.
        Ok<MatrixOfferingDto>(await h.SetOffering(h.Logistics, "AIR_TICKET", false, Nov1));
        Ok<MatrixOfferingDto>(await h.SetOffering(h.Logistics, "AIR_TICKET", false, Nov1));
        (await h.Db.PayComponents.CountAsync(p => p.CompanyId == h.Logistics.Id && !p.IsDeleted)).Should().Be(1);
        Ok<MatrixOfferingDto>(await h.SetOffering(h.Logistics, "AIR_TICKET", true, Nov1)).Offered.Should().BeTrue();
        (await h.Db.PayComponents.IgnoreQueryFilters().SingleAsync(p => p.CompanyId == h.Logistics.Id)).IsDeleted.Should().BeTrue("a skip that never started is withdrawn");

        // Skipping again on the same month reuses that row (the unique key counts deleted rows); a skip in force ends.
        Ok<MatrixOfferingDto>(await h.SetOffering(h.Logistics, "AIR_TICKET", false, Nov1));
        (await h.Db.PayComponents.IgnoreQueryFilters().CountAsync(p => p.CompanyId == h.Logistics.Id)).Should().Be(1);
        Ok<MatrixOfferingDto>(await h.SetOffering(h.Logistics, "AIR_TICKET", true, Dec1));
        var marker = await h.Db.PayComponents.SingleAsync(p => p.CompanyId == h.Logistics.Id);
        (marker.IsDeleted, marker.EffectiveFrom, marker.EffectiveTo).Should().Be((false, (DateOnly?)Nov1, (DateOnly?)Dec1.AddDays(-1)));
        Mode(await h.Read(h.Logistics.Id, Nov1), "AIR_TICKET").Should().Be(EntitlementMatrixService.Modes.Skipped);
        Mode(await h.Read(h.Logistics.Id, Dec1), "AIR_TICKET").Should().Be(EntitlementMatrixService.Modes.Adopted);
    }

    // ── Scope and permissions ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACompanyScopedAdmin_CannotPublishGroupCells_OrAnotherCompanysCells_ButCanPublishTheirOwn()
    {
        await using var h = await H.Create();
        var logisticsAdmin = h.Controller(companyScope: [h.Logistics.Id]);
        Error(await logisticsAdmin.Publish(h.Service, new PublishMatrixRequest(null, Nov1, [H.Amount(h.G[1], "PER_DIEM", 1m)]), false, default))
            .Should().Be("group_scope_required");
        Status(await logisticsAdmin.Publish(h.Service, new PublishMatrixRequest(h.Facility.Id, Nov1, [H.Amount(h.G[1], "PER_DIEM", 1m)]), false, default))
            .Should().Be(403);
        Status(await logisticsAdmin.SetOffering(h.Service, new SetOfferingRequest(h.Facility.Id, "EDUCATION", false, Nov1), default)).Should().Be(403);
        Status(await logisticsAdmin.ImportLegacy(h.Service, commit: false, effectiveFrom: null, default)).Should().Be(403);
        (await logisticsAdmin.Matrix(h.Service, h.Facility.Id, null, default)).Should().BeOfType<ForbidResult>();
        Ok<PublishMatrixResult>(await logisticsAdmin.Publish(h.Service, new PublishMatrixRequest(h.Logistics.Id, Nov1, [H.Amount(h.G[1], "PER_DIEM", 200m)]), false, default));
        (await h.Db.GradeEntitlements.SingleAsync()).CompanyId.Should().Be(h.Logistics.Id);
    }

    [Fact]
    public void EveryEndpoint_NamesItsPermission_ReadsTakeRead_WritesTakeManage()
    {
        var actions = typeof(EntitlementMatrixController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes().Any(a => a is Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute)).ToList();
        actions.Select(a => a.Name).Should().BeEquivalentTo("Components", "Matrix", "Publish", "SetOffering", "ImportLegacy");
        foreach (var action in actions)
        {
            var permissions = action.GetCustomAttribute<HasPermissionAttribute>()!.Permissions;
            var write = action.GetCustomAttributes().OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any(a => !a.HttpMethods.Contains("GET"));
            permissions.Should().Equal(new[] { write ? "entitlements.manage" : "entitlements.read" }, action.Name);
        }
    }

    [Fact]
    public async Task OnlyHrOwnersHoldEntitlementsManage_InTheSeededRoleBundles()
    {
        var (db, tid) = await SeededRoleBundles.NewTenantAsync("r1-matrix");
        await using var _ = db;
        foreach (var role in new[] { "Admin", "HR Director", "HR Manager" })
            (await SeededRoleBundles.PermissionsOfAsync(db, tid, role)).Should().Contain(["entitlements.read", "entitlements.manage"], role);
        foreach (var role in new[] { "HR Officer", "Finance", "Payroll Manager", "Employee" })
        {
            var held = await SeededRoleBundles.PermissionsOfAsync(db, tid, role);
            if (held.Length > 0) held.Should().NotContain("entitlements.manage", role);
        }
    }

    // ── Completeness ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddingAGrade_ShowsItAsAGapForEveryBenefit_AndPublishingWithGapsTellsHr()
    {
        await using var h = await H.Create();
        var hrDirector = h.Staff("HR Director");
        var officer = h.Staff("HR Officer");
        await h.Db.SaveChangesAsync();
        Ok<PublishMatrixResult>(await h.PublishGroup(Nov1, h.MasarGrid()));
        (await h.Db.Notifications.CountAsync()).Should().Be(0);

        var g6 = new Grade { TenantId = h.Tid, Code = "G6", Name = "Director", Level = 60 };
        h.Db.Grades.Add(g6);
        await h.Db.SaveChangesAsync();
        var gaps = (await h.Read(null, Nov1)).Gaps;
        gaps.Should().OnlyContain(g => g.GradeId == g6.Id);
        gaps.Select(g => g.ComponentCode).Should().BeEquivalentTo("HOUSING", "TRANSPORT", "OTHER_ALLOWANCES", "AIR_TICKET", "MEDICAL", "EDUCATION", "PER_DIEM");

        // A company's own G6 cells close a group gap only when EVERY company has one.
        Ok<PublishMatrixResult>(await h.PublishCompany(h.Logistics, Dec1, [H.Amount(g6, "PER_DIEM", 600m)]));
        (await h.Read(null, Dec1)).Gaps.Should().Contain(new MatrixGapDto(g6.Id, "PER_DIEM"));
        (await h.Read(h.Logistics.Id, Dec1)).Gaps.Should().NotContain(new MatrixGapDto(g6.Id, "PER_DIEM"));

        var result = Ok<PublishMatrixResult>(await h.PublishGroup(Dec1, [H.Amount(g6, "PER_DIEM", 600m)]));
        result.GapsRemaining.Should().Be(6);
        var notes = await h.Db.Notifications.Where(n => n.EntityName == EntitlementMatrixService.GapNotificationEntity).ToListAsync();
        notes.Should().ContainSingle(n => n.UserId == hrDirector.Id && n.EntityId == "group");
        notes.Should().NotContain(n => n.UserId == officer.Id);
    }

    // ── Legacy mechanisms: frozen, imported with a preview, nothing dropped ──────────────────────

    [Fact]
    public async Task LegacyWriters_AreFrozenForReleaseATenants_Only()
    {
        await using var h = await H.Create();
        var grades = new GradesController(null!, h.Db) { ControllerContext = h.Context() };
        var benefits = new BenefitsController(h.Db) { ControllerContext = h.Context() };
        var plan = new BenefitPlan { TenantId = h.Tid, Code = "MED", Name = "Medical", PlanType = "Medical" };
        h.Db.BenefitPlans.Add(plan);
        await h.Db.SaveChangesAsync();
        var line = new GradePayScaleComponentRequest("HOUSING", "Housing");

        Error(await grades.SetPayScale(h.G[1].Id, [line], default)).Should().Be("moved_to_benefits_by_grade");
        Error(await benefits.AddEligibility(plan.Id, new BenefitEligibilityRequest(null, h.G[1].Id, Nov1, null), default)).Should().Be("moved_to_benefits_by_grade");
        (await h.Db.GradePayScaleComponents.CountAsync()).Should().Be(0);
        (await h.Db.BenefitEligibilityRules.CountAsync()).Should().Be(0);

        // A tenant without Release A (Evostel's case) is untouched.
        h.Db.TenantFeatureFlags.Remove(await h.Db.TenantFeatureFlags.SingleAsync());
        await h.Db.SaveChangesAsync();
        (await grades.SetPayScale(h.G[1].Id, [line], default)).Should().BeOfType<OkObjectResult>();
        (await benefits.AddEligibility(plan.Id, new BenefitEligibilityRequest(null, h.G[1].Id, Nov1, null), default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task LegacyImport_PreviewEqualsCommit_MapsOnlyWhatItCanWithoutGuessing_AndIsIdempotent()
    {
        await using var h = await H.Create();
        h.PayScale(h.G[2], "HOUSING", "Housing", "PercentOfBasic", percentage: 25m);
        h.PayScale(h.G[2], "TRANSPORT", "Transport", "Fixed", amount: 500m);
        h.PayScale(h.G[2], "BASIC", "Basic", "Fixed", amount: 8000m);
        h.PayScale(h.G[2], "TICKET", "Air ticket", "Fixed", amount: 2400m, frequency: "Annual");
        h.PayScale(h.G[2], "INSURANCE", "Medical", "Fixed", amount: 3000m, frequency: "Annual");
        h.PayScale(h.G[4], "EDUCATION", "School fees", "Fixed", amount: 10_000m, frequency: "Annual");
        h.PayScale(h.G[4], "HOUSING", "Housing", "Fixed", amount: 0m);
        h.PayScale(h.G[5], "HOUSING", "Housing deduction", "Fixed", amount: 300m, type: "Deduction");
        var plan = new BenefitPlan { TenantId = h.Tid, Code = "MED-VIP", Name = "VIP medical", PlanType = "Medical" };
        h.Db.BenefitPlans.Add(plan);
        h.Db.BenefitEligibilityRules.Add(new BenefitEligibilityRule { TenantId = h.Tid, BenefitPlanId = plan.Id, GradeId = h.G[5].Id, EffectiveFrom = Today });
        await h.Db.SaveChangesAsync();

        var preview = Ok<LegacyImportResult>(await h.Controller().ImportLegacy(h.Service, commit: false, effectiveFrom: Nov1, default));
        preview.Committed.Should().BeFalse();
        (await h.Db.GradeEntitlements.CountAsync()).Should().Be(0, "a preview writes nothing");
        var outcomes = preview.Items.Select(i => (i.GradeCode, i.SourceCode, i.Outcome, i.ReasonCode)).ToList();
        outcomes.Should().BeEquivalentTo(new[]
        {
            ("G2", "HOUSING", "Import", (string?)null),
            ("G2", "TRANSPORT", "Import", null),
            ("G2", "BASIC", "Skip", "basic_salary"),
            ("G2", "TICKET", "Skip", "needs_ticket_details"),
            ("G2", "INSURANCE", "Skip", "needs_medical_class"),
            // A yearly school-fees figure says neither "per child" nor "for how many": listed for review, never imported.
            ("G4", "EDUCATION", "Skip", "needs_per_child_and_cap"),
            ("G4", "HOUSING", "Skip", "zero_amount"),
            // A deduction line with an allowance's name is not an allowance.
            ("G5", "HOUSING", "Skip", "not_an_allowance"),
            ("G5", "MED-VIP", "Skip", "eligibility_only"),
        });
        preview.ToImport.Should().Be(2);

        var commit = Ok<LegacyImportResult>(await h.Controller().ImportLegacy(h.Service, commit: true, effectiveFrom: Nov1, default));
        commit.Committed.Should().BeTrue();
        commit.Imported.Should().Be(2);
        commit.Items.Select(i => (i.SourceId, i.Outcome, i.ComponentCode)).Should().Equal(preview.Items.Select(i => (i.SourceId, i.Outcome, i.ComponentCode)));
        var cells = await h.Db.GradeEntitlements.ToListAsync();
        cells.Should().OnlyContain(c => c.SourceRule == EntitlementMatrixService.SourceRuleImport && c.CompanyId == null && c.EffectiveFrom == Nov1);
        cells.Select(c => (c.GradeId, c.PayComponentCode)).Should().BeEquivalentTo([(h.G[2].Id, "HOUSING"), (h.G[2].Id, "TRANSPORT")]);
        cells.Single(c => c.PayComponentCode == "HOUSING").Rate.Should().Be(0.25m);
        cells.Should().NotContain(c => c.PayComponentCode == "EDUCATION");

        // Nothing legacy is changed or dropped, and a second import finds the cells already set.
        (await h.Db.GradePayScaleComponents.CountAsync()).Should().Be(8);
        (await h.Db.BenefitEligibilityRules.CountAsync()).Should().Be(1);
        var again = Ok<LegacyImportResult>(await h.Controller().ImportLegacy(h.Service, commit: true, effectiveFrom: Nov1, default));
        again.Imported.Should().Be(0);
        again.Items.Where(i => i.Source == "PayScale" && i.Outcome == "Skip" && i.ReasonCode == "already_set").Should().HaveCount(2);
        (await h.Db.GradeEntitlements.CountAsync()).Should().Be(2);
    }

    [Fact]
    public void MapPayScaleLine_SkipsEveryNonEarningLine_AndNeverImportsEducation()
    {
        var grade = Guid.NewGuid();
        GradePayScaleComponent Line(string code, string type, string calc = "Fixed", decimal amount = 500m, string frequency = "Monthly") =>
            new() { GradeId = grade, ComponentCode = code, ComponentName = code, ComponentType = type, CalculationType = calc, Amount = amount, Frequency = frequency };
        foreach (var type in new[] { "Deduction", "Benefit", "EmployerContribution", "" })
            EntitlementMatrixService.MapPayScaleLine(Line("TRANSPORT", type)).Should().Match<(string? Code, string? ReasonCode, string? Reason, MatrixCellInput? Cell)>(
                m => m.ReasonCode == "not_an_allowance" && m.Cell == null, type);
        EntitlementMatrixService.MapPayScaleLine(Line("TRANSPORT", "earning")).Cell!.Amount.Should().Be(500m, "the type is compared without case");
        foreach (var (calc, frequency) in new[] { ("Fixed", "Annual"), ("Fixed", "Monthly"), ("PercentOfBasic", "Annual") })
            EntitlementMatrixService.MapPayScaleLine(Line("EDUCATION", "Earning", calc, 12_000m, frequency))
                .Should().Match<(string? Code, string? ReasonCode, string? Reason, MatrixCellInput? Cell)>(m => m.ReasonCode == "needs_per_child_and_cap" && m.Cell == null);
    }

    // ── R1 fix round: same-day re-publish, impact and gap counts, the paid-code skip ─────────────

    [Fact]
    public async Task SameDayRepublish_SupersedesAFutureVersionNothingUses_AuditsIt_AndRefusesOneInUseOrInForce()
    {
        await using var h = await H.Create();
        Ok<PublishMatrixResult>(await h.PublishGroup(Nov1, h.MasarGrid()));
        Ok<PublishMatrixResult>(await h.PublishGroup(Dec1, [H.Pct(h.G[3], "HOUSING", 0.30m)]));

        // Dec 1 has not started and nothing cites it: the correction replaces it on its own start date.
        var dry = Ok<PublishMatrixResult>(await h.PublishGroup(Dec1, [H.Pct(h.G[3], "HOUSING", 0.35m)], dryRun: true));
        (dry.Published, dry.Superseded).Should().Be((1, 1));
        (await h.Db.AuditLogs.CountAsync(a => a.Action == EntitlementMatrixService.AuditSuperseded)).Should().Be(0, "a dry run writes nothing");
        var fix = Ok<PublishMatrixResult>(await h.PublishGroup(Dec1, [H.Pct(h.G[3], "HOUSING", 0.35m)]));
        (fix.Published, fix.Superseded).Should().Be((1, 1));
        var versions = await h.Db.GradeEntitlements.Where(c => c.GradeId == h.G[3].Id && c.PayComponentCode == "HOUSING").OrderBy(c => c.EffectiveFrom).ToListAsync();
        versions.Select(v => (v.Rate, v.EffectiveFrom, v.EffectiveTo)).Should().Equal((0.25m, Nov1, (DateOnly?)Dec1.AddDays(-1)), (0.35m, Dec1, (DateOnly?)null));
        var audit = await h.Db.AuditLogs.SingleAsync(a => a.Action == EntitlementMatrixService.AuditSuperseded);
        audit.Metadata.Should().Contain("0.30").And.Contain(versions[1].Id.ToString());

        // Once an employee package cites it, it is history: the correction goes from the next day.
        h.Db.EmployeeEntitlements.Add(new EmployeeEntitlement { TenantId = h.Tid, CompanyId = h.Facility.Id, GradeEntitlementId = versions[1].Id,
            PayComponentCode = "HOUSING", EntitlementClass = "Contractual", ValueType = "PercentOfBasic", Rate = 0.35m, Source = "GradeDefault",
            EffectiveFrom = Dec1 });
        await h.Db.SaveChangesAsync();
        Error(await h.PublishGroup(Dec1, [H.Pct(h.G[3], "HOUSING", 0.40m)])).Should().Be("same_day_version_exists");

        // A version that starts today is in force: never replaced.
        Ok<PublishMatrixResult>(await h.PublishCompany(h.Logistics, Today, [H.Amount(h.G[1], "PER_DIEM", 160m)]));
        Error(await h.PublishCompany(h.Logistics, Today, [H.Amount(h.G[1], "PER_DIEM", 170m)])).Should().Be("same_day_version_exists");

        // A company's not-yet-started own value dropped for the group default on its start date: removed, counted as reverted.
        Ok<PublishMatrixResult>(await h.PublishCompany(h.Logistics, Dec1, [H.Amount(h.G[2], "PER_DIEM", 210m)]));
        var revert = Ok<PublishMatrixResult>(await h.PublishCompany(h.Logistics, Dec1,
            [new MatrixCellInput { GradeId = h.G[2].Id, ComponentCode = "PER_DIEM", UseGroupDefault = true }]));
        (revert.Reverted, revert.Superseded).Should().Be((1, 1));
        (await h.Db.GradeEntitlements.AnyAsync(c => c.CompanyId == h.Logistics.Id && c.GradeId == h.G[2].Id)).Should().BeFalse();
        Mode(await h.Read(h.Logistics.Id, Dec1), "PER_DIEM").Should().Be(EntitlementMatrixService.Modes.Tailored, "G1 keeps its own value");
        (await h.Read(h.Logistics.Id, Dec1)).Cells.Single(c => c.GradeId == h.G[2].Id && c.ComponentCode == "PER_DIEM").Inherited.Should().BeTrue();
    }

    [Fact]
    public async Task Impact_LeavesOutACompanyThatDoesNotOfferTheBenefit_OnTheEffectiveDate()
    {
        await using var h = await H.Create();
        h.Employee(h.Facility, h.G[4]); h.Employee(h.Logistics, h.G[4]); h.Employee(h.Logistics, h.G[4]);
        await h.Db.SaveChangesAsync();
        Ok<MatrixOfferingDto>(await h.SetOffering(h.Logistics, "EDUCATION", offered: false, Dec1));

        var education = H.Amount(h.G[4], "EDUCATION", 12_000m); education.DependantScope = "Children"; education.MaxDependants = 2;
        // From 1 Nov Logistics still offers Education; from 1 Dec it does not, so only Facility Services' G4 is reached.
        Ok<PublishMatrixResult>(await h.PublishGroup(Nov1, [education], dryRun: true)).AffectedAtRenewal.Should().Be(3);
        Ok<PublishMatrixResult>(await h.PublishGroup(Dec1, [education], dryRun: true)).AffectedAtRenewal.Should().Be(1);
        var own = await h.Controller().Publish(h.Service, new PublishMatrixRequest(h.Logistics.Id, Dec1, [education]), true, default);
        Ok<PublishMatrixResult>(own).AffectedAtRenewal.Should().Be(0, "Logistics does not offer Education from 1 Dec");
    }

    [Fact]
    public async Task GapNotification_CountsOnlyValuesWithNothingPublished()
    {
        await using var h = await H.Create();
        h.Staff("HR Director");
        await h.Db.SaveChangesAsync();
        // The whole grid from 1 Dec, then one Logistics value from 1 Nov: as of 1 Nov, 34 gaps remain for Logistics, all
        // with a value starting 1 Dec — none of them is "not set", so nobody is told they are.
        Ok<PublishMatrixResult>(await h.PublishGroup(Dec1, h.MasarGrid()));
        var result = Ok<PublishMatrixResult>(await h.PublishCompany(h.Logistics, Nov1, [H.Amount(h.G[1], "PER_DIEM", 140m)]));
        result.GapsRemaining.Should().Be(34);
        (await h.Db.Notifications.CountAsync()).Should().Be(0, "every remaining gap already has a value starting 1 Dec");

        h.Db.Grades.Add(new Grade { TenantId = h.Tid, Code = "G6", Name = "Director", Level = 60 });
        await h.Db.SaveChangesAsync();
        Ok<PublishMatrixResult>(await h.PublishCompany(h.Logistics, Dec1, [H.Amount(h.G[1], "PER_DIEM", 155m)]));
        (await h.Db.Notifications.SingleAsync()).Message.Should().StartWith("7 benefit value(s)", "only G6's seven are unset");
    }

    [Fact]
    public async Task SkippingABenefit_IsRefusedWhileAPaidPayComponentSharesItsCode()
    {
        await using var h = await H.Create();
        h.Db.PayComponents.Add(new PayComponent { TenantId = h.Tid, Code = "AIR_TICKET", NameEn = "Ticket allowance", NameAr = "بدل تذاكر",
            ComponentType = PayComponentTypes.Earning, CalcMethod = PayComponentCalcMethods.Fixed, Value = 1_500m });
        await h.Db.SaveChangesAsync();

        var result = await h.SetOffering(h.Logistics, "AIR_TICKET", offered: false, Nov1);
        Status(result).Should().Be(409);
        Error(result).Should().Be(ReleaseABlockReasons.EntitlementSkipPaidCode);
        Json(result).GetProperty("reason").GetProperty("titleAr").GetString().Should().NotBeNullOrWhiteSpace();
        (await h.Db.PayComponents.AnyAsync(p => p.CompanyId != null)).Should().BeFalse("no company row of any type is written");
        // A benefit with no paid twin is still switched off as before.
        Ok<MatrixOfferingDto>(await h.SetOffering(h.Logistics, "EDUCATION", offered: false, Nov1));
        Mode(await h.Read(h.Logistics.Id, Nov1), "EDUCATION").Should().Be(EntitlementMatrixService.Modes.Skipped);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmployeeImport_GradeSalaryStructure_ReadsTheMatrixForReleaseA_AndTheLegacyScaleOtherwise(bool releaseA)
    {
        await using var h = await H.Create();
        h.Db.Tenants.Add(new Tenant { Id = h.Tid, Name = "Masar", Slug = $"m-{h.Tid:N}" });
        h.Db.TenantSubscriptions.Add(new TenantSubscription { TenantId = h.Tid, MaxEmployees = 100, Plan = "Enterprise", Status = "Active" });
        h.PayScale(h.G[3], "HOUSING", "Housing", "Fixed", amount: 9_999m);
        if (!releaseA) h.Db.TenantFeatureFlags.Remove(await h.Db.TenantFeatureFlags.SingleAsync());
        await h.Db.SaveChangesAsync();
        if (releaseA) Ok<PublishMatrixResult>(await h.PublishGroup(Today, [H.Pct(h.G[3], "HOUSING", 0.25m), H.NotOffered(h.G[3], "OTHER_ALLOWANCES")]));

        var import = HrmHierarchyTests.BuildImportControllerInternal(h.Db, h.Tid);
        var csv = $"EmployeeCode,FullName,Grade,JoiningDate,BasicSalary,HousingAllowance\nM1,Imported Supervisor,G3,{Today:yyyy-MM-dd},8000,2000\n";
        var result = Assert.IsType<OkObjectResult>(await import.Import(new EmployeesController.ImportEmployeesRequest(csv), default));

        var structure = await h.Db.SalaryStructures.SingleAsync(s => s.Code == "GRADE-G3");
        var lines = await h.Db.SalaryComponents.Where(c => c.SalaryStructureId == structure.Id).ToListAsync();
        var assignment = await h.Db.EmployeeSalaryStructures.SingleAsync();
        (assignment.BasicSalary, assignment.HousingAllowance).Should().Be((8_000m, 2_000m), "each employee's own figures from the file are kept");
        if (releaseA)
        {
            lines.Select(l => (l.Code, l.CalculationType, l.Percentage)).Should().Equal([("HOUSING", "PercentOfBasic", 25m)],
                "the matrix, not the frozen 9,999 legacy line; not-offered and missing allowances get no line");
            JsonSerializer.Serialize(result.Value).Should().Contain("no transport allowance in Benefits by grade");
        }
        else lines.Select(l => (l.Code, l.Amount)).Should().Equal([("HOUSING", 9_999m)], "a tenant without Release A is unchanged");
    }

    /// <summary>
    /// The browser lane's route mocks (frontend/e2e/fixtures/benefits-by-grade/*.json) are these read models, serialized as
    /// the API serializes them. Set KYNEX_R1_FIXTURES_DIR to that folder to regenerate them; the assertions always run.
    /// </summary>
    [Fact]
    public async Task BrowserLaneFixtures_AreTheRealReadModels()
    {
        await using var h = await H.Create();
        h.Employee(h.Facility, h.G[3]); h.Employee(h.Facility, h.G[3]); h.Employee(h.Logistics, h.G[3]); h.Employee(h.Facility, h.G[5]);
        h.PayScale(h.G[2], "HOUSING", "Housing", "PercentOfBasic", percentage: 25m);
        h.PayScale(h.G[2], "TRANSPORT", "Transport", "Fixed", amount: 500m);
        h.PayScale(h.G[2], "BASIC", "Basic", "Fixed", amount: 8000m);
        h.PayScale(h.G[2], "TICKET", "Air ticket", "Fixed", amount: 2400m, frequency: "Annual");
        h.PayScale(h.G[4], "EDUCATION", "School fees", "Fixed", amount: 10_000m, frequency: "Annual");
        h.PayScale(h.G[5], "HOUSING", "Housing deduction", "Fixed", amount: 300m, type: "Deduction");
        await h.Db.SaveChangesAsync();
        // In force today, less three cells (G2 transport, G4 ticket, G5 per diem) so the grid shows what a gap looks like and
        // the legacy G2 transport line has somewhere to go.
        Ok<PublishMatrixResult>(await h.PublishGroup(Today, h.MasarGrid()
            .Where(c => !(c.GradeId == h.G[2].Id && c.ComponentCode == "TRANSPORT") && !(c.GradeId == h.G[4].Id && c.ComponentCode == "AIR_TICKET")
                && !(c.GradeId == h.G[5].Id && c.ComponentCode == "PER_DIEM")).ToList()));
        Ok<PublishMatrixResult>(await h.PublishCompany(h.Logistics, Today, [H.Amount(h.G[1], "PER_DIEM", 200m)]));
        Ok<MatrixOfferingDto>(await h.SetOffering(h.Logistics, "EDUCATION", offered: false, Nov1));
        Ok<PublishMatrixResult>(await h.PublishGroup(Nov1, [H.Pct(h.G[3], "HOUSING", 0.30m)]));

        var group = await h.Read(null, Today);
        var logistics = await h.Read(h.Logistics.Id, Today);
        var dryRun = Ok<PublishMatrixResult>(await h.PublishGroup(Nov1, [H.Pct(h.G[3], "HOUSING", 0.35m), H.Amount(h.G[5], "PER_DIEM", 500m)], dryRun: true));
        var legacy = Ok<LegacyImportResult>(await h.Controller().ImportLegacy(h.Service, commit: false, effectiveFrom: Today, default));

        group.Gaps.Should().HaveCount(3);
        legacy.ToImport.Should().Be(1, "G2 transport; G2 housing is already set in the grid");
        logistics.Offerings.Single(o => o.ComponentCode == "EDUCATION").ChangesOn.Should().Be(Nov1);
        (dryRun.Superseded, dryRun.AffectedNow, dryRun.AffectedAtRenewal).Should().Be((1, 1, 3));
        legacy.Items.Select(i => i.ReasonCode).Should().Contain(["not_an_allowance", "needs_per_child_and_cap", "basic_salary", "needs_ticket_details"]);

        if (Environment.GetEnvironmentVariable("KYNEX_R1_FIXTURES_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var companies = new[] { new { id = h.Facility.Id, name = h.Facility.LegalNameEn }, new { id = h.Logistics.Id, name = h.Logistics.LegalNameEn } };
        foreach (var (name, body) in new (string, object)[] { ("matrix-group", group), ("matrix-logistics", logistics), ("publish-dry-run", dryRun), ("legacy-preview", legacy), ("companies", companies) })
            await File.WriteAllTextAsync(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(body, json) + "\n");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static T Ok<T>(IActionResult result)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(200, JsonSerializer.Serialize(obj.Value));
        return obj.Value.Should().BeAssignableTo<T>().Subject;
    }

    private static int? Status(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode,
        StatusCodeResult s => s.StatusCode,
        _ => null,
    };

    private static JsonElement Json(IActionResult result) =>
        JsonSerializer.SerializeToElement(((ObjectResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string? Error(IActionResult result) => Json(result).GetProperty("error").GetString();

    private static List<MatrixCellError> CellErrors(IActionResult result)
    {
        Status(result).Should().Be(400);
        return JsonSerializer.Deserialize<List<MatrixCellError>>(Json(result).GetProperty("errors").GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static string Mode(EntitlementMatrixDto matrix, string code) => matrix.Offerings.Single(o => o.ComponentCode == code).Mode;

    private sealed class FixedClock(DateOnly today) : ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(today);
    }

    private sealed class H : IAsyncDisposable
    {
        public ZayraDbContext Db { get; } = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Tid { get; } = Guid.NewGuid();
        public Company Facility { get; private set; } = null!;
        public Company Logistics { get; private set; } = null!;
        /// <summary>G[1]…G[5]: Masar's grades, levels 10–50.</summary>
        public Grade[] G { get; } = new Grade[6];
        public EntitlementMatrixService Service { get; private set; } = null!;
        private int _employeeNo;

        public static async Task<H> Create()
        {
            var h = new H();
            h.Facility = new Company { TenantId = h.Tid, LegalNameEn = "Masar Facility Services Co.", DefaultCurrency = "SAR" };
            h.Logistics = new Company { TenantId = h.Tid, LegalNameEn = "Masar Logistics Co.", DefaultCurrency = "SAR" };
            string[] names = ["", "Worker", "Technician", "Supervisor", "Engineer", "Manager"];
            for (var i = 1; i <= 5; i++) h.G[i] = new Grade { TenantId = h.Tid, Code = $"G{i}", Name = names[i], Level = i * 10 };
            h.Db.AddRange(h.Facility, h.Logistics);
            h.Db.AddRange(h.G.Skip(1));
            h.Db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = h.Tid, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
            await h.Db.SaveChangesAsync();
            h.Service = new EntitlementMatrixService(h.Db, new FixedClock(Today));
            return h;
        }

        public ControllerContext Context(Guid[]? companyScope = null, string role = "HR Director")
        {
            var claims = new List<Claim>
            {
                new("tenant_id", Tid.ToString()),
                new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new(ClaimTypes.Role, role),
            };
            if (companyScope != null)
                claims.Add(new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = companyScope })));
            return new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } };
        }

        public EntitlementMatrixController Controller(Guid[]? companyScope = null) => new() { ControllerContext = Context(companyScope) };

        public Task<IActionResult> PublishGroup(DateOnly from, List<MatrixCellInput> cells, bool dryRun = false) =>
            Controller().Publish(Service, new PublishMatrixRequest(null, from, cells), dryRun, default);

        public Task<IActionResult> PublishCompany(Company company, DateOnly from, List<MatrixCellInput> cells) =>
            Controller().Publish(Service, new PublishMatrixRequest(company.Id, from, cells), false, default);

        public Task<IActionResult> SetOffering(Company company, string code, bool offered, DateOnly from) =>
            Controller().SetOffering(Service, new SetOfferingRequest(company.Id, code, offered, from), default);

        public async Task<EntitlementMatrixDto> Read(Guid? companyId, DateOnly asOf) =>
            (EntitlementMatrixDto)((OkObjectResult)await Controller().Matrix(Service, companyId, asOf, default)).Value!;

        public void Employee(Company company, Grade grade, string status = EmployeeStatuses.Active) =>
            Db.Employees.Add(new Employee { TenantId = Tid, CompanyId = company.Id, GradeId = grade.Id, Status = status,
                FullName = $"Employee {++_employeeNo}", EmployeeCode = $"M{_employeeNo}" });

        public User Staff(string roleName)
        {
            var user = new User { TenantId = Tid, Email = $"{Guid.NewGuid():N}@example.test", IsGroupScope = true, IsActive = true };
            var role = new Role { TenantId = Tid, Name = roleName, NormalizedName = roleName.ToUpperInvariant(), IsActive = true };
            Db.AddRange(user, role, new UserRole { UserId = user.Id, RoleId = role.Id });
            return user;
        }

        public void PayScale(Grade grade, string code, string name, string calc, decimal amount = 0, decimal percentage = 0, string frequency = "Monthly",
            string type = "Earning") =>
            Db.GradePayScaleComponents.Add(new GradePayScaleComponent { TenantId = Tid, GradeId = grade.Id, ComponentCode = code, ComponentName = name,
                ComponentType = type, CalculationType = calc, Amount = amount, Percentage = percentage, Frequency = frequency });

        // ── cell builders ──
        public static MatrixCellInput Amount(Grade g, string code, decimal amount) =>
            new() { GradeId = g.Id, ComponentCode = code, Eligible = true, ValueType = "Amount", Amount = amount };
        public static MatrixCellInput Pct(Grade g, string code, decimal rate) =>
            new() { GradeId = g.Id, ComponentCode = code, Eligible = true, ValueType = "PercentOfBasic", Rate = rate };
        public static MatrixCellInput InKind(Grade g, string code) => new() { GradeId = g.Id, ComponentCode = code, Eligible = true, ValueType = "InKind" };
        public static MatrixCellInput NotOffered(Grade g, string code) => new() { GradeId = g.Id, ComponentCode = code, Eligible = false };
        public static MatrixCellInput Tier(Grade g, string tier) =>
            new() { GradeId = g.Id, ComponentCode = "MEDICAL", Eligible = true, ValueType = "CoverageTier", CoverageTier = tier, DependantScope = "Family" };
        public static MatrixCellInput Ticket(Grade g, short count, string tierName, string scope = "None", short? maxDependants = null) =>
            new() { GradeId = g.Id, ComponentCode = "AIR_TICKET", Eligible = true, ValueType = "Quantity", Quantity = count, CoverageTier = tierName,
                DependantScope = scope, MaxDependants = maxDependants };

        /// <summary>The Masar Holding group matrix (plan §5), every grade × every non-loan benefit.</summary>
        public List<MatrixCellInput> MasarGrid()
        {
            var cells = new List<MatrixCellInput>
            {
                InKind(G[1], "HOUSING"), Pct(G[2], "HOUSING", 0.25m), Pct(G[3], "HOUSING", 0.25m), Pct(G[4], "HOUSING", 0.25m), Pct(G[5], "HOUSING", 0.30m),
                InKind(G[1], "TRANSPORT"), Pct(G[2], "TRANSPORT", 0.10m), Pct(G[3], "TRANSPORT", 0.10m), Amount(G[4], "TRANSPORT", 1_000m), Amount(G[5], "TRANSPORT", 1_000m),
                Ticket(G[1], 1, "Economy"), Ticket(G[2], 1, "Economy"), Ticket(G[3], 1, "Economy"), Ticket(G[4], 1, "Economy", "Family", 3), Ticket(G[5], 1, "Business", "Family", 4),
                Tier(G[1], "CchiBasic"), Tier(G[2], "CchiBasic"), Tier(G[3], "B"), Tier(G[4], "A"), Tier(G[5], "VIP"),
                NotOffered(G[1], "EDUCATION"), NotOffered(G[2], "EDUCATION"), NotOffered(G[3], "EDUCATION"),
                Amount(G[1], "PER_DIEM", 150m), Amount(G[2], "PER_DIEM", 150m), Amount(G[3], "PER_DIEM", 250m), Amount(G[4], "PER_DIEM", 350m), Amount(G[5], "PER_DIEM", 500m),
            };
            foreach (var (grade, amount, children) in new[] { (G[4], 10_000m, (short)2), (G[5], 15_000m, (short)3) })
            {
                var education = Amount(grade, "EDUCATION", amount);
                education.DependantScope = "Children"; education.MaxDependants = children;
                cells.Add(education);
            }
            foreach (var ticket in cells.Where(c => c.ComponentCode == "AIR_TICKET"))
            {
                ticket.NationalityScope = NationalityScopes.NonSaudi;
                ticket.NationalityBasis = "Home-leave ticket per contract";
            }
            for (var i = 1; i <= 5; i++) cells.Add(NotOffered(G[i], "OTHER_ALLOWANCES"));
            return cells;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
