using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
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
        string payrollImpact = "Full", Guid? companyId = null) => new(
        Name: "Maternity", LeaveTypeId: leaveTypeId, CountryCode: country, CompanyId: companyId, BranchId: null,
        DepartmentName: null, Grade: null, EmploymentType: null, ContractType: null, Gender: "Female",
        AppliesOnProbation: true, AnnualEntitlementDays: days, AccrualMethod: "Yearly",
        CarryForwardMax: 0m, CarryForwardExpiry: 0, EncashmentAllowed: false, EncashmentMaxDays: 0m,
        MinimumDaysPerRequest: 1m, MaximumDaysPerRequest: maxPerRequest, NoticeRequiredDays: 0,
        WeekendsIncluded: true, PublicHolidaysIncluded: true, PayrollImpact: payrollImpact,
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
    public async Task SaudiEmployee_TwelveWeekMaternityRequest_IsNotRefusedByALegacySeventyDayCap()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var (employee, mat) = await SeedLegacySeventyDayMaternityAsync(db, tenantId, "SA");
        var start = Today.AddDays(30);
        // Balance granted by hand, as it must be today (see the report: nothing grants a Yearly policy).
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            LeaveTypeId = mat.Id, LeaveTypeName = mat.NameEn, Year = start.Year, Entitled = 84m,
        });
        if (start.AddDays(83).Year != start.Year)
            db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
            {
                TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
                LeaveTypeId = mat.Id, LeaveTypeName = mat.NameEn, Year = start.Year + 1, Entitled = 84m,
            });
        await db.SaveChangesAsync();

        var submitted = await new LeaveService(db, new ApprovalRouter(db))
            .SubmitRequestAsync(tenantId, TwelveWeeks(employee, mat, start), employee.UserAccountId);

        submitted.TotalDays.Should().Be(84m, "12 weeks counted on the calendar is 84 days");
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
