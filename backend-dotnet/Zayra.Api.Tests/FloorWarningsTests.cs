using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Compliance;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Admin;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Owner decision 2026-10-25: every statutory value is a DEFAULT; a value below the legal minimum is allowed with a
/// warning the admin must acknowledge (audited). The wire contract is the hire path's: 409
/// <c>warnings_require_acknowledgement</c> and <c>acknowledgedWarnings: [{ code, reason }]</c>.
/// </summary>
public class FloorWarningsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ── the gate (pure) ───────────────────────────────────────────────────────────────────

    private static FloorWarning W(string code, decimal? configured = 10m) => new(code, "f", "m", false, 21m, configured, "Labor Law Art. 109", "ar");

    [Fact]
    public void Gate_OpenWarning_WithoutAnAnswer_IsUnacknowledged()
    {
        var o = FloorWarningGate.Evaluate([W("a")], null);
        o.Unacknowledged.Should().ContainSingle().Which.Code.Should().Be("a");
        o.Clear.Should().BeFalse();
    }

    [Fact]
    public void Gate_AnswerWithAReasonOfTenToFiveHundredCharacters_IsConsumed()
    {
        var o = FloorWarningGate.Evaluate([W("a")], [new FloorAcknowledgement("a", "ten chars!!")]);
        o.Clear.Should().BeTrue();
        o.Consumed.Should().ContainSingle().Which.Ack.Code.Should().Be("a");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too short")]
    public void Gate_AnswerWithoutARealReason_IsFlagged_NotAccepted(string? reason)
    {
        var o = FloorWarningGate.Evaluate([W("a")], [new FloorAcknowledgement("a", reason)]);
        o.ReasonInvalid.Should().BeTrue();
        o.Consumed.Should().BeEmpty();
    }

    [Fact]
    public void Gate_ReasonOverFiveHundredCharacters_IsFlagged()
        => FloorWarningGate.Evaluate([W("a")], [new FloorAcknowledgement("a", new string('x', 501))]).ReasonInvalid.Should().BeTrue();

    [Fact]
    public void Gate_AnswerForADifferentCode_LeavesTheWarningOpen()
        => FloorWarningGate.Evaluate([W("a")], [new FloorAcknowledgement("b", "a perfectly good reason")]).Unacknowledged.Should().HaveCount(1);

    [Fact]
    public void Gate_StoredAcknowledgement_AtTheSameValue_NeedsNoNewAnswer_ButAChangedValueDoes()
    {
        var stored = new List<StoredFloorAck> { new("a", "f", 10m, 21m, "c", "why", "Zed", null, DateTime.UtcNow) };
        FloorWarningGate.Evaluate([W("a", 10m)], null, w => FloorAckStore.Covering(stored, w) is not null).Clear.Should().BeTrue();
        FloorWarningGate.Evaluate([W("a", 12m)], null, w => FloorAckStore.Covering(stored, w) is not null).Clear.Should().BeFalse();
    }

    [Fact]
    public void Store_Merge_KeepsLiveAcknowledgements_DropsResolvedOnes_AndRecordsWhoAndWhen()
    {
        var old = new List<StoredFloorAck>
        {
            new("keep", "f", 10m, 21m, "c", "r1", "Old", null, DateTime.UtcNow.AddDays(-3)),
            new("gone", "f", 5m, 21m, "c", "r2", "Old", null, DateTime.UtcNow.AddDays(-3)),
        };
        var live = new[] { W("keep", 10m), W("fresh", 7m) };
        var by = Guid.NewGuid();
        var merged = FloorAckStore.Merge(old, live, [(live[1], new FloorAcknowledgement("fresh", " because we decided "))], "Zed", by, new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc));

        merged.Select(m => m.Code).Should().Equal("keep", "fresh");
        merged[0].AcknowledgedBy.Should().Be("Old");
        merged[1].Should().Match<StoredFloorAck>(m => m.AcknowledgedBy == "Zed" && m.AcknowledgedById == by && m.Reason == "because we decided" && m.StatutoryValue == 21m);
    }

    // ── leave: annual and sick floors, every accrual method ──────────────────────────────

    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static LeavePoliciesController Controller(ZayraDbContext db, Guid tenantId)
    {
        var c = new LeavePoliciesController(db, new LeaveService(db, new ApprovalRouter(db)));
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.Name, "Hana HR"), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Test")),
            },
        };
        return c;
    }

    private static async Task<(Guid Tenant, Company Company)> TenantWithSaudiCompanyAsync(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "KSA Co", CountryCode = "SA" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return (tenant, company);
    }

    private static async Task<LeaveType> TypeAsync(ZayraDbContext db, Guid tenant, string code, string name, string category)
    {
        var t = new LeaveType { TenantId = tenant, Code = code, NameEn = name, NameAr = name, Category = category, IsPaid = true, IsActive = true };
        db.LeaveTypes.Add(t);
        await db.SaveChangesAsync();
        return t;
    }

    private static CreateLeavePolicyRequest Req(Guid typeId, decimal days, string accrual = "Yearly", int minService = 0, IReadOnlyCollection<FloorAcknowledgement>? acks = null) => new(
        Name: "Policy", LeaveTypeId: typeId, CountryCode: "SA", CompanyId: null, BranchId: null, DepartmentName: null, Grade: null,
        EmploymentType: null, ContractType: null, Gender: null, AppliesOnProbation: true, AnnualEntitlementDays: days, AccrualMethod: accrual,
        CarryForwardMax: 0m, CarryForwardExpiry: 0, EncashmentAllowed: false, EncashmentMaxDays: 0m, MinimumDaysPerRequest: 1m,
        MaximumDaysPerRequest: 0m, NoticeRequiredDays: 0, WeekendsIncluded: true, PublicHolidaysIncluded: true, PayrollImpact: "Full",
        ApprovalWorkflowId: null, Status: "Active", MinimumServiceMonths: minService, AcknowledgedWarnings: acks);

    private static JsonElement Body(IActionResult r, int status)
    {
        var o = r.Should().BeOfType<ObjectResult>().Subject;
        o.StatusCode.Should().Be(status);
        return JsonSerializer.SerializeToElement(o.Value, Web);
    }

    [Theory]
    [InlineData("Yearly")]
    [InlineData("Monthly")]
    [InlineData("Quarterly")]
    [InlineData("None")]
    public async Task AnnualLeave_BelowTwentyOneDays_NeedsAcknowledgement_ForEveryAccrualMethod(string accrual)
    {
        await using var db = Db();
        var (tenant, _) = await TenantWithSaudiCompanyAsync(db);
        var annual = await TypeAsync(db, tenant, "ANNUAL", "Annual Leave", "Annual");
        var c = Controller(db, tenant);

        var w = Body(await c.Create(Req(annual.Id, 15m, accrual), default), 409).GetProperty("warnings")[0];
        w.GetProperty("code").GetString().Should().Be("leave_annual_below_minimum");
        w.GetProperty("statutoryValue").GetDecimal().Should().Be(21m);
        w.GetProperty("citation").GetString().Should().Contain("Art. 109");
        (await db.LeavePolicies.AnyAsync()).Should().BeFalse();

        (await c.Create(Req(annual.Id, 21m, accrual), default)).Should().BeOfType<CreatedResult>("21 is the Saudi default and warns nothing");
    }

    [Fact]
    public async Task AnnualLeave_BelowTwentyOne_IsSavedWithAnAcknowledgement_AndTheAccrualHonoursIt()
    {
        await using var db = Db();
        var (tenant, _) = await TenantWithSaudiCompanyAsync(db);
        var annual = await TypeAsync(db, tenant, "ANNUAL", "Annual Leave", "Annual");
        var created = await Controller(db, tenant).Create(Req(annual.Id, 15m, "Monthly",
            acks: [new FloorAcknowledgement("leave_annual_below_minimum", "Part-time contractors agreed 15 days")]), default);

        var policy = (LeavePolicy)created.Should().BeOfType<CreatedResult>().Subject.Value!;
        KsaLeaveFloorChecks.IsAcknowledgedBelowMinimum(policy, KsaLeaveFloorChecks.Annual).Should().BeTrue();
        policy.AnnualEntitlementDays = 16m;
        KsaLeaveFloorChecks.IsAcknowledgedBelowMinimum(policy, KsaLeaveFloorChecks.Annual).Should().BeFalse("a different value is not what was acknowledged");
    }

    [Fact]
    public async Task SickLeave_BelowTheArt117Scale_AndAServiceRequirement_AreWarnings()
    {
        await using var db = Db();
        var (tenant, _) = await TenantWithSaudiCompanyAsync(db);
        var sick = await TypeAsync(db, tenant, "SICK", "Sick Leave", "Sick");
        var c = Controller(db, tenant);

        var warnings = Body(await c.Create(Req(sick.Id, 90m, minService: 3), default), 409).GetProperty("warnings").EnumerateArray().ToList();
        warnings.Select(x => x.GetProperty("code").GetString()).Should().BeEquivalentTo("leave_sick_below_minimum", "leave_sick_service_requirement");
        warnings.First(x => x.GetProperty("code").GetString() == "leave_sick_below_minimum").GetProperty("statutoryValue").GetDecimal().Should().Be(120m);
        (await db.LeavePolicies.AnyAsync()).Should().BeFalse();

        (await c.Create(Req(sick.Id, 120m), default)).Should().BeOfType<CreatedResult>();
    }

    [Theory]
    [InlineData("SA")]
    [InlineData("SAU")]
    [InlineData("KSA")]
    public async Task EverySpellingOfSaudiArabiaIsHeldToTheFloors_IncludingTheScreensKsa(string country)
    {
        await using var db = Db();
        var (tenant, _) = await TenantWithSaudiCompanyAsync(db);
        var annual = await TypeAsync(db, tenant, "ANNUAL", "Annual Leave", "Annual");
        Body(await Controller(db, tenant).Create(Req(annual.Id, 15m) with { CountryCode = country }, default), 409);
    }

    [Fact]
    public async Task NonSaudiPolicies_AreNeverWarned()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid();
        db.Companies.Add(new Company { TenantId = tenant, LegalNameEn = "UAE Co", CountryCode = "AE" });
        var annual = await TypeAsync(db, tenant, "ANNUAL", "Annual Leave", "Annual");
        (await Controller(db, tenant).Create(Req(annual.Id, 10m) with { CountryCode = "AE" }, default)).Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task FloorCheck_ReportsEveryApplicableFloor_BelowOrNot_WithoutSaving()
    {
        await using var db = Db();
        var (tenant, _) = await TenantWithSaudiCompanyAsync(db);
        var annual = await TypeAsync(db, tenant, "ANNUAL", "Annual Leave", "Annual");

        var ok = await Controller(db, tenant).FloorCheck(new LeaveFloorCheckRequest(annual.Id, "SA", null, 15m, 0m, "Full", true, true), default);

        var check = JsonSerializer.SerializeToElement(((OkObjectResult)ok).Value, Web).GetProperty("checks")[0];
        check.GetProperty("below").GetBoolean().Should().BeTrue();
        check.GetProperty("statutoryValue").GetDecimal().Should().Be(21m);
        check.GetProperty("messageAr").GetString().Should().NotBeNullOrEmpty();
        (await db.LeavePolicies.AnyAsync()).Should().BeFalse();
    }

    // ── EOSB: the configuration writer ───────────────────────────────────────────────────

    private static SetupSettingsController Settings(ZayraDbContext db, Guid tenant)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Name, "Sami Super"),
            new(ClaimTypes.Role, "Admin"), new("permission", "security.manage"), new("is_group_scope", "true"),
        };
        return new SetupSettingsController(db, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "t")) } },
        };
    }

    private static GCCSettingRequest Gcc(decimal r1, decimal r2, IReadOnlyCollection<FloorAcknowledgement>? acks = null, string country = "SA") => new(
        country, false, null, null, false, true, r1, r2, 0, "Sun-Thu", "Fri,Sat", true, 60, false, 60, false, false, 2, acks);

    [Fact]
    public async Task Eosb_RateBelowArt84_NeedsAcknowledgement_AndIsStoredWithoutOneNowhere()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid();
        var c = Settings(db, tenant);

        var body = Body(await c.UpsertGCCSetting(Gcc(10m, 30m), default), 409);
        var w = body.GetProperty("warnings").EnumerateArray().Single();
        w.GetProperty("code").GetString().Should().Be("eosb_tier1_below_minimum");
        w.GetProperty("statutoryValue").GetDecimal().Should().Be(15m);
        w.GetProperty("citation").GetString().Should().Contain("Art. 84");
        (await db.GCCComplianceSettings.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Eosb_RateBelowArt84_IsSavedWithAnAcknowledgement_AndAudited()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid();
        var c = Settings(db, tenant);

        var saved = await c.UpsertGCCSetting(Gcc(10m, 30m, [new FloorAcknowledgement("eosb_tier1_below_minimum", "Group policy for casual staff")]), default);

        var row = (GCCComplianceSetting)saved.Should().BeOfType<OkObjectResult>().Subject.Value!;
        row.EosbYears1To5Rate.Should().Be(10m);
        KsaEosbFloorChecks.IsAcknowledged(row.EosbBelowFloorAcksJson, KsaEosbFloorChecks.Tier1, 10m).Should().BeTrue();
        KsaEosbFloorChecks.IsAcknowledged(row.EosbBelowFloorAcksJson, KsaEosbFloorChecks.Tier1, 11m).Should().BeFalse();
        var audit = await db.AdminAuditLogs.SingleAsync(a => a.Action == "BelowStatutoryMinimumAcknowledged");
        audit.NewValuesJson.Should().Contain("Group policy for casual staff").And.Contain("Art. 84").And.Contain("Sami Super");
        audit.PerformedBy.Should().NotBeNull();
    }

    [Fact]
    public async Task Eosb_AnotherPanelResendingAnUnchangedLegacyBelowFloorRate_IsNotBlocked_ButAChangeIs()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid();
        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tenant, CountryCode = "SA", EosbEnabled = true, EosbYears1To5Rate = 10m, EosbYearsAbove5Rate = 30m });
        await db.SaveChangesAsync();
        var c = Settings(db, tenant);

        (await c.UpsertGCCSetting(Gcc(10m, 30m), default)).Should().BeOfType<OkObjectResult>("the WPS panel re-sends the stored rate untouched");
        Body(await c.UpsertGCCSetting(Gcc(9m, 30m), default), 409);
    }

    [Fact]
    public async Task Eosb_RatesAtOrAboveTheFloor_AndNonSaudiCountries_RaiseNoWarning()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid();
        var c = Settings(db, tenant);
        (await c.UpsertGCCSetting(Gcc(15m, 30m), default)).Should().BeOfType<OkObjectResult>();
        (await c.UpsertGCCSetting(Gcc(21m, 45m), default)).Should().BeOfType<OkObjectResult>();
        (await c.UpsertGCCSetting(Gcc(5m, 5m, country: "AE"), default)).Should().BeOfType<OkObjectResult>();
    }

    // ── the "Rules below the legal minimum" list ─────────────────────────────────────────

    [Fact]
    public async Task BelowMinimumList_ShowsAcknowledgedAndUnacknowledgedRules_AcrossLeaveAndEosb_WithWhoAndWhen()
    {
        await using var db = Db();
        var (tenant, company) = await TenantWithSaudiCompanyAsync(db);
        var annual = await TypeAsync(db, tenant, "ANNUAL", "Annual Leave", "Annual");
        var sick = await TypeAsync(db, tenant, "SICK", "Sick Leave", "Sick");
        // one acknowledged through the API, one legacy row written straight to the table (never acknowledged)
        await Controller(db, tenant).Create(Req(annual.Id, 15m, acks: [new FloorAcknowledgement("leave_annual_below_minimum", "Casual staff agreed 15 days")]) with { CompanyId = company.Id }, default);
        db.LeavePolicies.Add(new LeavePolicy { TenantId = tenant, Name = "Legacy sick", LeaveTypeId = sick.Id, CountryCode = "SA", CompanyId = company.Id, AnnualEntitlementDays = 60m, Status = "Active", WeekendsIncluded = true, PublicHolidaysIncluded = true });
        await db.SaveChangesAsync();
        await Settings(db, tenant).UpsertGCCSetting(Gcc(10m, 30m, [new FloorAcknowledgement("eosb_tier1_below_minimum", "Group policy for casual staff")]), default);

        var list = new FloorComplianceController(new Zayra.Api.Infrastructure.Compliance.BelowMinimumReportService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tenant_id", tenant.ToString()), new Claim("is_group_scope", "true")], "t")) } },
        };
        var items = JsonSerializer.SerializeToElement(((OkObjectResult)await list.BelowMinimum(default)).Value, Web).GetProperty("items").EnumerateArray().ToList();

        var annualItem = items.Single(i => i.GetProperty("code").GetString() == "leave_annual_below_minimum");
        annualItem.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
        annualItem.GetProperty("acknowledgedBy").GetString().Should().Be("Hana HR");
        annualItem.GetProperty("acknowledgedAtUtc").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        annualItem.GetProperty("configured").GetDecimal().Should().Be(15m);
        annualItem.GetProperty("statutory").GetDecimal().Should().Be(21m);
        annualItem.GetProperty("citation").GetString().Should().Contain("Art. 109");
        annualItem.GetProperty("companyName").GetString().Should().Be("KSA Co");

        items.Single(i => i.GetProperty("code").GetString() == "leave_sick_below_minimum").GetProperty("acknowledged").GetBoolean().Should().BeFalse("nothing is hidden");
        items.Single(i => i.GetProperty("code").GetString() == "eosb_tier1_below_minimum").GetProperty("acknowledgedBy").GetString().Should().Be("Sami Super");
    }
}
