using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Setup;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// KSA statutory special leave — Labour Law Arts. 113, 114, 151, 160 as amended by Royal Decree M/44
/// (in force 2025-02-19). Maternity was drafted at 70 days (the old "10 weeks"); the law has given
/// 12 weeks since the amendment. These tests pin each statutory figure, the floor that stops a company
/// configuring below it, and the request path that stops a below-floor cap refusing a lawful request.
/// </summary>
public class KsaStatutorySpecialLeaveTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    // ── The statutory figures, as seeded ────────────────────────────────────────────────────

    /// <summary>The value of a seeded platform rule on a date, resolved the way the reader does:
    /// latest EffectiveFrom on or before the date, EffectiveTo exclusive.</summary>
    private static string? Seeded(string key, DateOnly on)
    {
        var at = on.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return StatutoryRuleSeeder.BuildRules()
            .Where(r => r.CountryCode == CountryCodes.Saudi && r.Jurisdiction == Jurisdictions.KsaMainland
                        && r.RuleKey == key && r.EffectiveFrom <= at && (r.EffectiveTo == null || r.EffectiveTo > at))
            .OrderByDescending(r => r.EffectiveFrom)
            .Select(r => r.RuleValue)
            .FirstOrDefault();
    }

    [Theory]
    // Art. 151 as amended — 12 weeks.
    [InlineData("leave.maternity_days", "84")]
    // Art. 113 — marriage 5, death of spouse/ascendant/descendant 5, sibling 3 (2025), birth 3.
    [InlineData("leave.marriage_days", "5")]
    [InlineData("leave.bereavement_days", "5")]
    [InlineData("leave.bereavement_sibling_days", "3")]
    [InlineData("leave.paternity_days", "3")]
    // Art. 114 — Hajj 10 to 15 days, after two years' service.
    [InlineData("leave.hajj_min_days", "10")]
    [InlineData("leave.hajj_max_days", "15")]
    [InlineData("leave.hajj_min_service_years", "2")]
    // Art. 160 — iddah: four months and ten days (130 calendar days); 15 days for a non-Muslim widow.
    [InlineData("leave.iddah_muslim_days", "130")]
    [InlineData("leave.iddah_non_muslim_days", "15")]
    // [COUNSEL] product default bounding the event date of bereavement, birth and marriage leave.
    [InlineData("leave.event_date_max_lead_days", "30")]
    [InlineData("leave.event_date_max_lead_days.paternity", "7")]
    // Art. 117 — unchanged by the 2025 package: 30 full, 60 at three quarters, 30 unpaid.
    [InlineData("leave.sick_band1_days", "30")]
    [InlineData("leave.sick_band1_pay_rate", "1.0")]
    [InlineData("leave.sick_band2_days", "60")]
    [InlineData("leave.sick_band2_pay_rate", "0.75")]
    [InlineData("leave.sick_band3_days", "30")]
    [InlineData("leave.sick_band3_pay_rate", "0.0")]
    public void SeededStatutoryFigure_IsTheLaw(string key, string expected)
        => Seeded(key, Today).Should().Be(expected);

    [Fact]
    public void Maternity_IsTenWeeksBeforeTheAmendment_AndTwelveFromIt()
    {
        var dayBefore = new DateOnly(2025, 2, 18);
        var inForce = new DateOnly(2025, 2, 19);

        Seeded("leave.maternity_days", dayBefore).Should().Be("70", "a leave that began before M/44 is judged on the old law");
        Seeded("leave.maternity_days", inForce).Should().Be("84");
        Seeded("leave.bereavement_sibling_days", dayBefore).Should().BeNull("there was no sibling entitlement before 2025");
        Seeded("leave.bereavement_sibling_days", inForce).Should().Be("3");

        KsaSpecialLeaveDefaults.FloorDays(KsaStatutoryLeaveKind.Maternity, dayBefore).Should().Be(70m);
        KsaSpecialLeaveDefaults.FloorDays(KsaStatutoryLeaveKind.Maternity, inForce).Should().Be(84m);
        KsaSpecialLeaveDefaults.FloorDays(KsaStatutoryLeaveKind.BereavementSibling, dayBefore).Should().BeNull();
    }

    [Fact]
    public void CompiledFallback_AgreesWithEverySeededRow()
    {
        // Two copies of one legal fact must never become two different legal answers.
        foreach (var kind in Enum.GetValues<KsaStatutoryLeaveKind>())
        {
            var seeded = Seeded(KsaSpecialLeaveRuleKeys.For(kind), Today);
            seeded.Should().NotBeNull($"{kind} must have a seeded platform rule");
            decimal.Parse(seeded!, System.Globalization.CultureInfo.InvariantCulture)
                .Should().Be(KsaSpecialLeaveDefaults.FloorDays(kind, Today)!.Value, kind.ToString());
        }
    }

    [Theory]
    [InlineData("MAT", "Maternity Leave", "Parental", KsaStatutoryLeaveKind.Maternity)]
    [InlineData("ML2", "Maternity", "", KsaStatutoryLeaveKind.Maternity)]
    [InlineData("X1", "Birth leave", "Maternity", KsaStatutoryLeaveKind.Maternity)]
    [InlineData("PAT", "Paternity Leave", "Parental", KsaStatutoryLeaveKind.Paternity)]
    [InlineData("MARRIAGE", "Marriage Leave", "Marriage", KsaStatutoryLeaveKind.Marriage)]
    [InlineData("BRV", "Bereavement Leave", "", KsaStatutoryLeaveKind.Bereavement)]
    [InlineData("BRV2", "Death of a brother or sister", "", KsaStatutoryLeaveKind.BereavementSibling)]
    [InlineData("HAJJ", "Hajj Leave", "Religious", KsaStatutoryLeaveKind.Hajj)]
    [InlineData("IDDAH", "Iddah Leave", "Bereavement", KsaStatutoryLeaveKind.IddahMuslim)]
    [InlineData("IDDAH2", "Iddah leave (non-Muslim widow)", "", KsaStatutoryLeaveKind.IddahNonMuslim)]
    public void Classify_RecognisesTheStatutoryLeaves(string code, string name, string category, KsaStatutoryLeaveKind expected)
        => KsaStatutorySpecialLeave.Classify(code, name, category).Should().Be(expected);

    [Theory]
    [InlineData("MATEXT", "Maternity extension (unpaid)", "Maternity")]   // Art. 151's own unpaid month
    [InlineData("ANNUAL", "Annual Leave", "Annual")]
    [InlineData("SICK", "Sick Leave", "Medical")]
    [InlineData("MATERIAL", "Material handling course", "Training")]
    public void Classify_LeavesOtherLeaveAlone(string code, string name, string category)
        => KsaStatutorySpecialLeave.Classify(code, name, category).Should().BeNull();

    // ── The floor a company cannot configure below ─────────────────────────────────────────

    private static T WithTenant<T>(T controller, Guid tenantId) where T : ControllerBase
    {
        var identity = new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("tenant_id", tenantId.ToString())], "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) },
        };
        return controller;
    }

    private static LeavePoliciesController PoliciesController(ZayraDbContext db, Guid tenantId)
        => WithTenant(new LeavePoliciesController(db, new LeaveService(db, new ApprovalRouter(db))), tenantId);

    private static CreateLeavePolicyRequest Policy(
        Guid leaveTypeId, decimal days, string? country = "SA", decimal maxPerRequest = 0m,
        string payrollImpact = "Full", Guid? companyId = null, bool calendar = true) => new(
        Name: "Maternity", LeaveTypeId: leaveTypeId, CountryCode: country, CompanyId: companyId, BranchId: null,
        DepartmentName: null, Grade: null, EmploymentType: null, ContractType: null, Gender: "Female",
        AppliesOnProbation: true, AnnualEntitlementDays: days, AccrualMethod: "Yearly",
        CarryForwardMax: 0m, CarryForwardExpiry: 0, EncashmentAllowed: false, EncashmentMaxDays: 0m,
        MinimumDaysPerRequest: 1m, MaximumDaysPerRequest: maxPerRequest, NoticeRequiredDays: 0,
        WeekendsIncluded: calendar, PublicHolidaysIncluded: calendar, PayrollImpact: payrollImpact,
        ApprovalWorkflowId: null, Status: "Active");

    private static async Task<LeaveType> AddMaternityTypeAsync(ZayraDbContext db, Guid tenantId, int maxConsecutive = 0)
    {
        var t = new LeaveType
        {
            TenantId = tenantId, Code = "MAT", NameEn = "Maternity Leave", Category = "Parental",
            IsPaid = true, IsActive = true, MaxConsecutiveDays = maxConsecutive,
        };
        db.LeaveTypes.Add(t);
        await db.SaveChangesAsync();
        return t;
    }

    private static void AssertFloorRefusal(IActionResult result)
    {
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value).Should().Be("statutory_leave_floor");
        ((string)bad.Value.GetType().GetProperty("message")!.GetValue(bad.Value)!).Should().Contain("Art. 151");
    }

    [Fact]
    public async Task SaudiMaternityPolicy_BelowTwelveWeeks_IsRefused_AndNothingIsStored()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var mat = await AddMaternityTypeAsync(db, tenantId);
        var controller = PoliciesController(db, tenantId);

        AssertFloorRefusal(await controller.Create(Policy(mat.Id, days: 70m), default));
        AssertFloorRefusal(await controller.Create(Policy(mat.Id, days: 84m, maxPerRequest: 70m), default));
        AssertFloorRefusal(await controller.Create(Policy(mat.Id, days: 84m, payrollImpact: "Unpaid"), default));
        (await db.LeavePolicies.AnyAsync()).Should().BeFalse("a refused statutory floor is not stored");

        (await controller.Create(Policy(mat.Id, days: 84m), default)).Should().BeOfType<CreatedResult>();
        (await controller.Create(Policy(mat.Id, days: 98m, maxPerRequest: 98m), default))
            .Should().BeOfType<CreatedResult>("an employer may grant more than the law");
    }

    [Fact]
    public async Task SaudiMaternityPolicy_CannotBeEditedDownBelowTwelveWeeks()
    {
        var dbName = Guid.NewGuid().ToString();
        ZayraDbContext Open() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(dbName).Options);
        var tenantId = Guid.NewGuid();
        Guid id;
        await using (var db = Open())
        {
            var mat = await AddMaternityTypeAsync(db, tenantId);
            var created = (CreatedResult)await PoliciesController(db, tenantId).Create(Policy(mat.Id, days: 84m), default);
            id = ((LeavePolicy)created.Value!).Id;
        }

        await using (var db = Open())
        {
            var lowered = await PoliciesController(db, tenantId).Update(id, new UpdateLeavePolicyRequest(
                Name: null, CountryCode: null, CompanyId: null, BranchId: null, DepartmentName: null, Grade: null,
                EmploymentType: null, ContractType: null, Gender: null, AppliesOnProbation: null,
                AnnualEntitlementDays: 70m, AccrualMethod: null, CarryForwardMax: null, CarryForwardExpiry: null,
                EncashmentAllowed: null, EncashmentMaxDays: null, MinimumDaysPerRequest: null,
                MaximumDaysPerRequest: null, NoticeRequiredDays: null, WeekendsIncluded: null,
                PublicHolidaysIncluded: null, PayrollImpact: null, ApprovalWorkflowId: null, Status: null), default);
            AssertFloorRefusal(lowered);
        }

        await using (var db = Open())
            (await db.LeavePolicies.SingleAsync(p => p.Id == id)).AnnualEntitlementDays.Should().Be(84m);
    }

    [Fact]
    public async Task CountryNeutralMaternityPolicy_InATenantWithASaudiCompany_IsFloored()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Companies.Add(new Company { TenantId = tenantId, LegalNameEn = "KSA Co", CountryCode = "SA" });
        var mat = await AddMaternityTypeAsync(db, tenantId);

        AssertFloorRefusal(await PoliciesController(db, tenantId).Create(Policy(mat.Id, days: 70m, country: null), default));
    }

    [Fact]
    public async Task NonSaudiMaternityPolicy_IsNotHeldToTheSaudiFloor()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var uae = new Company { TenantId = tenantId, LegalNameEn = "UAE Co", CountryCode = "AE" };
        db.Companies.Add(uae);
        var mat = await AddMaternityTypeAsync(db, tenantId);
        var controller = PoliciesController(db, tenantId);

        (await controller.Create(Policy(mat.Id, days: 60m, country: "AE"), default)).Should().BeOfType<CreatedResult>();
        (await controller.Create(Policy(mat.Id, days: 60m, country: null, companyId: uae.Id), default))
            .Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task TheFloorIsReadFromThePlatformStatutoryRule()
    {
        // The seeded row is the source of truth; the compiled 84 is only the unseeded fallback.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.StatutoryRules.Add(new StatutoryRule
        {
            Id = Guid.NewGuid(), TenantId = null, CountryCode = CountryCodes.Saudi, Jurisdiction = Jurisdictions.KsaMainland,
            RuleKey = KsaSpecialLeaveRuleKeys.MaternityDays, RuleValue = "98", DataType = "decimal",
            EffectiveFrom = new DateTime(2025, 2, 19, 0, 0, 0, DateTimeKind.Utc), Description = "test",
        });
        var mat = await AddMaternityTypeAsync(db, tenantId);

        AssertFloorRefusal(await PoliciesController(db, tenantId).Create(Policy(mat.Id, days: 84m), default));
    }

    [Fact]
    public async Task SaudiMaternityPolicy_OnWorkingDays_BelowEightyFour_IsRefusedWithTheFix()
    {
        // 70 working days cannot be judged against the law's 12 weeks; the refusal spells out the fix.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var mat = await AddMaternityTypeAsync(db, tenantId);

        var refused = await PoliciesController(db, tenantId).Create(Policy(mat.Id, days: 70m, calendar: false), default);

        AssertFloorRefusal(refused);
        var message = (string)((BadRequestObjectResult)refused).Value!.GetType().GetProperty("message")!
            .GetValue(((BadRequestObjectResult)refused).Value)!;
        message.Should().Contain("84 calendar days by law").And.Contain("tick Count Weekends and Count Public Holidays");
    }

    [Fact]
    public async Task SaudiMaternityPolicy_OnWorkingDays_AtEightyFourOrMore_IsSaved_WithAReviewPrompt()
    {
        // At or above the figure a working-day policy grants at least the statute in its own unit, so
        // HR is not stranded: it saves, and a StatutoryReviewNeeded row asks for calendar counting.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var mat = await AddMaternityTypeAsync(db, tenantId);

        var saved = await PoliciesController(db, tenantId).Create(Policy(mat.Id, days: 84m, calendar: false), default);

        saved.Should().BeOfType<CreatedResult>();
        var id = ((LeavePolicy)((CreatedResult)saved).Value!).Id.ToString();
        (await db.LeaveAuditLogs.SingleAsync(a => a.EntityId == id && a.Action == "StatutoryReviewNeeded"))
            .Reason.Should().Contain("switch it to calendar days");
    }

    [Fact]
    public async Task ReSavingAWorkingDayMaternityPolicy_DoesNotDuplicateTheOpenReviewItem_AndFixingItClosesIt()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var mat = await AddMaternityTypeAsync(db, tenantId);
        var controller = PoliciesController(db, tenantId);
        var id = ((LeavePolicy)((CreatedResult)await controller.Create(Policy(mat.Id, days: 84m, calendar: false), default)).Value!).Id;
        UpdateLeavePolicyRequest Update(decimal? days = null, bool? calendar = null) => new(
            Name: null, CountryCode: null, CompanyId: null, BranchId: null, DepartmentName: null, Grade: null,
            EmploymentType: null, ContractType: null, Gender: null, AppliesOnProbation: null,
            AnnualEntitlementDays: days, AccrualMethod: null, CarryForwardMax: null, CarryForwardExpiry: null,
            EncashmentAllowed: null, EncashmentMaxDays: null, MinimumDaysPerRequest: null,
            MaximumDaysPerRequest: null, NoticeRequiredDays: null, WeekendsIncluded: calendar,
            PublicHolidaysIncluded: calendar, PayrollImpact: null, ApprovalWorkflowId: null, Status: null);

        await controller.Update(id, Update(days: 90m), default);
        await controller.Update(id, Update(days: 95m), default);
        (await db.LeaveAuditLogs.CountAsync(a => a.EntityId == id.ToString() && a.Action == "StatutoryReviewNeeded")).Should().Be(1);

        await controller.Update(id, Update(calendar: true), default);
        (await db.LeaveAuditLogs.CountAsync(a => a.EntityId == id.ToString() && a.Action == "StatutoryReviewResolved")).Should().Be(1);
    }

    [Fact]
    public async Task StatutoryEntitlements_AreResolvedForAWholePage_SaudiStaffOnly()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ksa = new Company { TenantId = tenantId, LegalNameEn = "KSA Co", CountryCode = "SA" };
        var uae = new Company { TenantId = tenantId, LegalNameEn = "UAE Co", CountryCode = "AE" };
        db.Companies.AddRange(ksa, uae);
        var saudi = new Employee { TenantId = tenantId, EmployeeCode = "S1", FullName = "S", Status = "Active", CompanyId = ksa.Id };
        var emirati = new Employee { TenantId = tenantId, EmployeeCode = "U1", FullName = "U", Status = "Active", CompanyId = uae.Id };
        db.Employees.AddRange(saudi, emirati);
        var mat = await AddMaternityTypeAsync(db, tenantId);
        var annual = new LeaveType { TenantId = tenantId, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsPaid = true };
        db.LeaveTypes.Add(annual);
        await db.SaveChangesAsync();

        var figures = await new LeaveService(db, new ApprovalRouter(db)).GetKsaStatutoryEntitlementsAsync(tenantId,
            new[] { (saudi.Id, mat.Id), (saudi.Id, annual.Id), (emirati.Id, mat.Id) });

        figures.Should().ContainSingle().Which.Should().Be(new KeyValuePair<(int, Guid), decimal>((saudi.Id, mat.Id), 84m));
    }

    [Fact]
    public async Task HajjWaiver_IsAudited_OnCreateAndOnEveryToggle()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var hajj = new LeaveType { TenantId = tenantId, Code = "HAJJ", NameEn = "Hajj Leave", Category = "Religious", IsPaid = true, IsActive = true };
        db.LeaveTypes.Add(hajj);
        await db.SaveChangesAsync();
        var controller = PoliciesController(db, tenantId);

        var created = (CreatedResult)await controller.Create(
            Policy(hajj.Id, days: 10m) with { AllowsHajjBeyondStatutoryEligibility = true }, default);
        var id = ((LeavePolicy)created.Value!).Id;
        await controller.Update(id, new UpdateLeavePolicyRequest(
            Name: null, CountryCode: null, CompanyId: null, BranchId: null, DepartmentName: null, Grade: null,
            EmploymentType: null, ContractType: null, Gender: null, AppliesOnProbation: null,
            AnnualEntitlementDays: null, AccrualMethod: null, CarryForwardMax: null, CarryForwardExpiry: null,
            EncashmentAllowed: null, EncashmentMaxDays: null, MinimumDaysPerRequest: null,
            MaximumDaysPerRequest: null, NoticeRequiredDays: null, WeekendsIncluded: null,
            PublicHolidaysIncluded: null, PayrollImpact: null, ApprovalWorkflowId: null, Status: null,
            AllowsHajjBeyondStatutoryEligibility: false), default);

        var toggles = await db.LeaveAuditLogs.Where(a => a.EntityId == id.ToString() && a.Action == "HajjEligibilityWaiverChanged")
            .OrderBy(a => a.CreatedAtUtc).ToListAsync();
        toggles.Select(a => (a.OldValue, a.NewValue)).Should().Equal(
            ("allows_hajj_beyond_statutory_eligibility=false", "allows_hajj_beyond_statutory_eligibility=true"),
            ("allows_hajj_beyond_statutory_eligibility=true", "allows_hajj_beyond_statutory_eligibility=false"));
    }

    [Fact]
    public async Task MixedTenant_NeutralMaternityPolicy_IsNotForcedToTheSaudiFigure_ButNeedsASaudiPolicy()
    {
        // A no-country, no-company policy in a tenant with Saudi AND UAE companies governs both. The
        // Saudi 84 must not be forced onto the UAE staff; the admin is told to add a Saudi policy.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Companies.Add(new Company { TenantId = tenantId, LegalNameEn = "KSA Co", CountryCode = "SA" });
        db.Companies.Add(new Company { TenantId = tenantId, LegalNameEn = "UAE Co", CountryCode = "AE" });
        var mat = await AddMaternityTypeAsync(db, tenantId);
        var controller = PoliciesController(db, tenantId);

        var refused = await controller.Create(Policy(mat.Id, days: 60m, country: null, calendar: false), default);
        AssertFloorRefusal(refused);
        ((string)((BadRequestObjectResult)refused).Value!.GetType().GetProperty("message")!
                .GetValue(((BadRequestObjectResult)refused).Value)!)
            .Should().Contain("create a separate Saudi policy (country SA)");

        (await controller.Create(Policy(mat.Id, days: 84m, country: "SA"), default)).Should().BeOfType<CreatedResult>();
        (await controller.Create(Policy(mat.Id, days: 60m, country: null, calendar: false), default))
            .Should().BeOfType<CreatedResult>("Saudi employees now resolve the Saudi policy, which outranks this one");
    }

    // ── Setup apply: a reviewed draft edited below the floor is refused whole ──────────────

    private sealed class NoPreview : ISetupAssistantService
    {
        public Task<SetupPreviewResult> GenerateAsync(SetupRequester requester, CompanyProfile profile, CancellationToken ct)
            => Task.FromResult(new SetupPreviewResult(SetupDraft.Empty(), [], "test"));
    }

    private static SetupAssistantController SetupController(ZayraDbContext db, Guid tenantId)
    {
        var claims = new[]
        {
            new System.Security.Claims.Claim("tenant_id", tenantId.ToString()),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new System.Security.Claims.Claim("permission", "organization.setup.apply"),
            new System.Security.Claims.Claim("permission", "leave.policy_manage"),
        };
        return new SetupAssistantController(db, new NoPreview(), new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "Test")),
                },
            },
        };
    }

    private static DraftLeavePolicy DraftPolicy(string code, decimal days, bool calendar = true)
        => new($"{code} Policy", code, days, "Yearly", false, 0m, 1m, days, 0, calendar, calendar, true, "Full");

    [Fact]
    public async Task SetupApply_RefusesADraftBelowTheSaudiFloor_AndWritesNothing()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var draft = SetupDraft.Empty() with
        {
            LeaveTypes = [new DraftLeaveType("MAT", "Maternity Leave", "Parental", true, 70, true, "#ec4899")],
            LeavePolicies = [DraftPolicy("MAT", 70m)],
        };

        var result = await SetupController(db, tenantId).Apply(new ApplySetupRequest(draft, "SA", "SAR"), default);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value).Should().Be("statutory_leave_floor");
        (await db.LeaveTypes.AnyAsync()).Should().BeFalse("nothing is applied when the draft is refused");
        (await db.LeavePolicies.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SetupApply_RefusesAnUnpaidMaternityType_EvenWithNoPolicy()
    {
        await using var db = CreateDb();
        var draft = SetupDraft.Empty() with
        {
            LeaveTypes = [new DraftLeaveType("MAT", "Maternity Leave", "Parental", false, 84, true, "#ec4899")],
        };

        var result = await SetupController(db, Guid.NewGuid()).Apply(new ApplySetupRequest(draft, "SA", "SAR"), default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SetupApply_ResolvesThePolicysLeaveTypeFromTheDatabase_WhenTheDraftDoesNotCarryIt()
    {
        // Apply attaches a policy to the tenant's EXISTING type of that code; the guard must too, or a
        // draft holding only a policy would slip past it.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        await AddMaternityTypeAsync(db, tenantId);
        var draft = SetupDraft.Empty() with { LeavePolicies = [DraftPolicy("MAT", 70m, calendar: false)] };

        var result = await SetupController(db, tenantId).Apply(new ApplySetupRequest(draft, "SA", "SAR"), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.LeavePolicies.AnyAsync()).Should().BeFalse();
    }

    // ── The request path: a below-floor cap must not refuse a lawful request ────────────────

    private static async Task<(Employee Employee, LeaveType Maternity)> SeedLegacySeventyDayMaternityAsync(
        ZayraDbContext db, Guid tenantId, string companyCountry)
    {
        var company = new Company { TenantId = tenantId, LegalNameEn = "Co", CountryCode = companyCountry };
        db.Companies.Add(company);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"MAT-{Guid.NewGuid():N}", FullName = "New Mother", EnglishName = "New Mother",
            Status = "Active", CompanyId = company.Id, JoiningDate = DateTime.UtcNow.AddYears(-3),
            UserAccountId = Guid.NewGuid(), Gender = "Female",
        };
        db.Employees.Add(employee);
        // The shape a tenant that applied the old setup draft holds: 70 on the type and on the policy.
        // Written directly — the controller would now refuse it — exactly as a legacy row exists.
        var mat = new LeaveType
        {
            TenantId = tenantId, Code = "MAT", NameEn = "Maternity Leave", Category = "Parental",
            IsPaid = true, IsActive = true, MaxConsecutiveDays = 70,
        };
        db.LeaveTypes.Add(mat);
        db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = tenantId, Name = "Maternity Leave Policy", LeaveTypeId = mat.Id, CountryCode = string.Empty,
            AnnualEntitlementDays = 70m, MaximumDaysPerRequest = 70m, MinimumDaysPerRequest = 1m,
            WeekendsIncluded = true, PublicHolidaysIncluded = true, AppliesOnProbation = true,
            AccrualMethod = "Yearly", Status = "Active",
        });
        await db.SaveChangesAsync();
        await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, tenantId);
        return (employee, mat);
    }

    private static LeaveRequest TwelveWeeks(Employee e, LeaveType t, DateOnly start) => new()
    {
        EmployeeId = e.Id, EmployeeName = e.FullName, LeaveTypeId = t.Id,
        StartDate = start, EndDate = start.AddDays(83), DayType = "Full", Reason = "Childbirth",
    };

    [Fact]
    public async Task SaudiEmployee_TwelveWeekMaternityRequest_NeedsNoBalanceRow_AndIsNotRefusedByALegacyCap()
    {
        // Two defects at once: the legacy 70-day caps, and the balance check — nothing ever grants a
        // "Yearly" policy a balance, so every statutory event leave was refused "Insufficient leave
        // balance". The statutory entitlement in force is the grant now.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var (employee, mat) = await SeedLegacySeventyDayMaternityAsync(db, tenantId, "SA");
        (await db.EmployeeLeaveBalances.AnyAsync()).Should().BeFalse("the point is that no balance was granted");

        var submitted = await new LeaveService(db, new ApprovalRouter(db))
            .SubmitRequestAsync(tenantId, TwelveWeeks(employee, mat, Today.AddDays(30)), employee.UserAccountId);

        submitted.TotalDays.Should().Be(84m, "12 weeks counted on the calendar is 84 days");
        (await db.EmployeeLeaveBalances.Where(b => b.LeaveTypeId == mat.Id).SumAsync(b => b.Pending)).Should().Be(84m);
    }

    [Fact]
    public async Task SaudiStatutoryEventLeave_IsCappedAtTheStatutoryEntitlementPerEvent()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var (employee, _) = await SeedLegacySeventyDayMaternityAsync(db, tenantId, "SA");
        var marriage = new LeaveType
        {
            TenantId = tenantId, Code = "MARRIAGE", NameEn = "Marriage Leave", Category = "Marriage", IsPaid = true, IsActive = true,
        };
        db.LeaveTypes.Add(marriage);
        db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = tenantId, Name = "Marriage", LeaveTypeId = marriage.Id, AnnualEntitlementDays = 5m,
            WeekendsIncluded = true, PublicHolidaysIncluded = true, AppliesOnProbation = true, Status = "Active",
        });
        await db.SaveChangesAsync();
        var service = new LeaveService(db, new ApprovalRouter(db));
        LeaveRequest Days(int n, int offset) => new()
        {
            EmployeeId = employee.Id, EmployeeName = employee.FullName, LeaveTypeId = marriage.Id,
            StartDate = Today.AddDays(offset), EndDate = Today.AddDays(offset + n - 1), DayType = "Full", Reason = "Wedding",
        };

        var tooLong = () => service.SubmitRequestAsync(tenantId, Days(6, 10), employee.UserAccountId);
        (await tooLong.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("5 day(s) per event");

        (await service.SubmitRequestAsync(tenantId, Days(5, 20), employee.UserAccountId)).TotalDays.Should().Be(5m);
    }

    [Fact]
    public async Task MaternityInProgressOnTheAmendmentDate_GetsTwelveWeeks_OneThatEndedBeforeItGetsTen()
    {
        // Royal Decree M/44 took effect 19 Feb 2025. A leave running across that date is given the more
        // favourable 84 days [COUNSEL]; one wholly before it is judged on the old 70.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var (employee, mat) = await SeedLegacySeventyDayMaternityAsync(db, tenantId, "SA");
        var service = new LeaveService(db, new ApprovalRouter(db));

        var spanning = await service.SubmitRequestAsync(
            tenantId, TwelveWeeks(employee, mat, new DateOnly(2025, 1, 20)), employee.UserAccountId);
        spanning.TotalDays.Should().Be(84m);

        var before = () => service.SubmitRequestAsync(
            tenantId, TwelveWeeks(employee, mat, new DateOnly(2024, 9, 1)), employee.UserAccountId);
        (await before.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("maximum of 70");
    }

    [Fact]
    public async Task NonSaudiEmployee_KeepsTheConfiguredCap()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var (employee, mat) = await SeedLegacySeventyDayMaternityAsync(db, tenantId, "AE");

        var act = () => new LeaveService(db, new ApprovalRouter(db))
            .SubmitRequestAsync(tenantId, TwelveWeeks(employee, mat, Today.AddDays(30)), employee.UserAccountId);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("maximum of 70");
    }
}
