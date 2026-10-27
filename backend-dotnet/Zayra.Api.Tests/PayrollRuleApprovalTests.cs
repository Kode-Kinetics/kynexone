using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// HR proposes, Finance approves. A change that lowers pay or goes below a statutory minimum waits for a DIFFERENT person
/// holding payroll.approve; a change that cannot lower pay applies directly. Changes start on a chosen date and never
/// reprice a period that began earlier.
/// </summary>
public class PayrollRuleApprovalTests
{
    private const string Group = "{\"v\":2,\"m\":\"group\",\"c\":[]}";
    private static readonly Guid Maker = Guid.NewGuid();
    private static readonly Guid Checker = Guid.NewGuid();

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CompanyPayrollRulesController As(ZayraDbContext db, Guid tenant, Guid user) => new(new Zayra.Api.Infrastructure.Payroll.CompanyPayrollRulesService(db, new AuditService(db)))
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            [new(ClaimTypes.NameIdentifier, user.ToString()), new("tenant_id", tenant.ToString()),
                new("entity_scope", Group), new("entity_scope_strict", "true")], "Test")) } },
    };

    private static async Task<(Guid Tenant, Guid Company)> SeedAsync(ZayraDbContext db, bool withApprover)
    {
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "A", CountryCode = "SAU", DefaultCurrency = "SAR" };
        db.Companies.Add(company);
        if (withApprover)
        {
            var permission = new Permission { Key = "payroll.approve", Module = "Payroll" };
            var role = new Role { TenantId = tenant, Name = "Finance Approver", NormalizedName = "FINANCE APPROVER" };
            db.Permissions.Add(permission);
            db.Roles.Add(role);
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
            db.Users.Add(new User { Id = Checker, TenantId = tenant, Email = "f@x.test", NormalizedEmail = "F@X.TEST", FullName = "Finance", IsGroupScope = true });
            db.UserRoles.Add(new UserRole { UserId = Checker, RoleId = role.Id });
        }
        await db.SaveChangesAsync();
        return (tenant, company.Id);
    }

    private static CompanyPayrollRulesController.SaveRulesRequest Lowering(Guid company, DateOnly? from = null) => new(company,
        [new PayrollRuleChange(PayrollRuleKeys.AbsenceDeductionBase, "Gross")], false, "Agreed with finance", from);

    // ── Which changes need a second person ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PayrollRuleKeys.AbsenceDeductionBase, null, "Gross", true)]       // a bigger absence deduction
    [InlineData(PayrollRuleKeys.AbsenceDeductionBase, "Gross", "Basic", false)]   // a smaller one
    [InlineData(PayrollRuleKeys.LeaveCashOutOnExit, null, "false", true)]
    [InlineData(PayrollRuleKeys.LeaveCashOutOnExit, "false", "true", false)]
    [InlineData(PayrollRuleKeys.LeaveCashOutBasis, null, "Basic", true)]          // payout on basic only, from the full package
    [InlineData(PayrollRuleKeys.LeaveCashOutBasis, "Basic", "Gross", false)]
    [InlineData(PayrollRuleKeys.LeaveCashOutBasis, null, "Gross", false)]         // same as the default
    [InlineData(PayrollRuleKeys.RestDayMultiplier, "1.5", "2", false)]            // a raise
    [InlineData(PayrollRuleKeys.RestDayMultiplier, "2", "1.5", true)]
    [InlineData(PayrollRuleKeys.HolidayMultiplier, null, "2", true)]              // platform value behind it is not known here
    [InlineData(PayrollRuleKeys.RestDayWorkMode, null, "CompensatoryDay", true)]
    [InlineData(PayrollRuleKeys.RestDayWorkMode, "CompensatoryDay", "PaidPremium", false)]
    [InlineData(PayrollRuleKeys.ProrationBasis, null, "CalendarActual", true)]
    [InlineData(PayrollRuleKeys.MaxProbationDays, null, "60", false)]             // moves no pay
    public void LowersPay_ClassifiesEachChange(string key, string? from, string? to, bool lowers)
        => CompanyPayrollRules.LowersPay(key, from, to).Should().Be(lowers);

    // ── Maker-checker ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APayLoweringChange_WithNoOtherApprover_IsRefusedUpFront_InBothLanguages()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, withApprover: false);

        var result = await As(db, tenant, Maker).Save(Lowering(company), default);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value!.ToString().Should().Contain("ERR_PAYROLL_RULE_NO_APPROVER").And.Contain("messageAr");
        (await db.PayrollRuleChangeRequests.CountAsync()).Should().Be(0);
        (await db.CompanyPayrollRules.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TheMakerCannotBeTheOnlyApprover()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, withApprover: true);

        // The approver themselves proposes: there is nobody else left to decide.
        (await As(db, tenant, Checker).Save(Lowering(company), default)).Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task APayLoweringChange_WaitsForApproval_ThenAppliesWhenAnotherPersonApproves()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, withApprover: true);

        (await As(db, tenant, Maker).Save(Lowering(company), default)).Should().BeOfType<AcceptedResult>();
        (await db.CompanyPayrollRules.CountAsync()).Should().Be(0, "nothing applies before a second person approves");
        var pending = await db.PayrollRuleChangeRequests.SingleAsync();
        pending.Status.Should().Be("Pending");
        pending.RequestedBy.Should().Be(Maker);

        (await As(db, tenant, Maker).Approve(pending.Id, null, default)).Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(403, "nobody approves their own change");
        (await db.CompanyPayrollRules.CountAsync()).Should().Be(0);

        (await As(db, tenant, Checker).Approve(pending.Id, null, default)).Should().BeOfType<OkObjectResult>();

        (await CompanyPayrollRules.ResolveAsync(db, tenant, company, default)).AbsenceDeductionBase.Should().Be("Gross");
        var decided = await db.PayrollRuleChangeRequests.SingleAsync();
        decided.Status.Should().Be("Approved");
        decided.DecidedBy.Should().Be(Checker);
        var audit = await db.AuditLogs.Where(a => a.Action == "payroll.rules.changed").SingleAsync();
        audit.Metadata.Should().Contain(Maker.ToString()).And.Contain(Checker.ToString());
        (await db.AuditLogs.CountAsync(a => a.Action == "payroll.rules.change_requested")).Should().Be(1);
    }

    [Fact]
    public async Task ARejectedChange_NeverApplies_AndNeedsAReason()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, withApprover: true);
        await As(db, tenant, Maker).Save(Lowering(company), default);
        var id = (await db.PayrollRuleChangeRequests.SingleAsync()).Id;

        (await As(db, tenant, Checker).Reject(id, new(" "), default)).Should().BeOfType<BadRequestObjectResult>();
        (await As(db, tenant, Checker).Reject(id, new("Not agreed with the client"), default)).Should().BeOfType<OkObjectResult>();

        (await db.CompanyPayrollRules.CountAsync()).Should().Be(0);
        (await db.PayrollRuleChangeRequests.SingleAsync()).Status.Should().Be("Rejected");
        (await As(db, tenant, Checker).Approve(id, null, default)).Should().BeOfType<ConflictObjectResult>("a decided change cannot be decided again");
    }

    [Fact]
    public async Task ANonLoweringChange_AppliesDirectly_WithNoApprover()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, withApprover: false);

        var result = await As(db, tenant, Maker).Save(new(company, [new PayrollRuleChange(PayrollRuleKeys.MaxProbationDays, "60")]), default);

        result.Should().BeOfType<OkObjectResult>();
        (await CompanyPayrollRules.ResolveAsync(db, tenant, company, default)).MaxProbationDays.Should().Be(60);
        (await db.PayrollRuleChangeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task BelowTheMinimum_NeedsReason_AndTheAcknowledgementMustEchoTheWarnings()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, withApprover: true);
        var change = new[] { new PayrollRuleChange(PayrollRuleKeys.RestDayMultiplier, "1.25") };
        string[] echoed = ["rest_day_multiplier:BELOW_OVERTIME_FLOOR"];

        (await As(db, tenant, Maker).Save(new(company, [.. change]), default)).Should().BeOfType<ConflictObjectResult>("not acknowledged");
        (await As(db, tenant, Maker).Save(new(company, [.. change], true, null, null, [.. echoed]), default))
            .Should().BeOfType<BadRequestObjectResult>("an acknowledgement needs a reason");
        (await As(db, tenant, Maker).Save(new(company, [.. change], true, "Signed contract terms", null, ["holiday_multiplier:BELOW_OVERTIME_FLOOR"]), default))
            .Should().BeOfType<BadRequestObjectResult>("it acknowledged a different warning than the one raised");
        (await As(db, tenant, Maker).Save(new(company, [.. change], true, "Signed contract terms", null, null), default))
            .Should().BeOfType<BadRequestObjectResult>("a bare acknowledge flag proves nothing was shown");
        (await db.PayrollRuleChangeRequests.CountAsync()).Should().Be(0);

        (await As(db, tenant, Maker).Save(new(company, [.. change], true, "Signed contract terms", null, [.. echoed]), default))
            .Should().BeOfType<AcceptedResult>();
        var pending = await db.PayrollRuleChangeRequests.SingleAsync();
        pending.WarningCodesJson.Should().Contain("BELOW_OVERTIME_FLOOR");

        await As(db, tenant, Checker).Approve(pending.Id, null, default);
        var row = await db.CompanyPayrollRules.SingleAsync();
        row.AcknowledgedBy.Should().Be(Maker);
        row.AcknowledgementNote.Should().Be("Signed contract terms");
        var audit = await db.AuditLogs.Where(a => a.Action == "payroll.rules.changed").SingleAsync();
        audit.Metadata.Should().Contain("rest_day_multiplier:BELOW_OVERTIME_FLOOR").And.Contain("Signed contract terms");
    }

    [Fact]
    public async Task ApproverFromAnotherTenant_IsNotAnApprover()
    {
        await using var db = CreateDb();
        await SeedAsync(db, withApprover: true);          // an approver, but in a different tenant
        var (tenant, company) = await SeedAsync(db, withApprover: false);

        (await As(db, tenant, Maker).Save(Lowering(company), default)).Should().BeOfType<ConflictObjectResult>();
    }

    // ── Effective date ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AChangeStartingLater_DoesNotRepriceAnEarlierPeriod()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, withApprover: false);
        var today = CompanyPayrollRules.Today();
        var nextMonth = today.AddDays(40);
        // Absent-day base moves from Gross back to Basic later (a smaller deduction, so no approval): history is kept.
        await CompanyPayrollRules.SaveAsync(db, tenant, company, Maker, [new PayrollRuleChange(PayrollRuleKeys.AbsenceDeductionBase, "Gross")], false, null, DateTime.UtcNow, default);
        (await As(db, tenant, Maker).Save(new(company, [new PayrollRuleChange(PayrollRuleKeys.AbsenceDeductionBase, "Basic")], false, null, nextMonth), default))
            .Should().BeOfType<OkObjectResult>();

        (await CompanyPayrollRules.ResolveAsync(db, tenant, company, default, today)).AbsenceDeductionBase.Should().Be("Gross");
        (await CompanyPayrollRules.ResolveAsync(db, tenant, company, default, nextMonth.AddDays(-1))).AbsenceDeductionBase.Should().Be("Gross",
            "a period that began before the change keeps the rule it began with");
        (await CompanyPayrollRules.ResolveAsync(db, tenant, company, default, nextMonth)).AbsenceDeductionBase.Should().Be("Basic");
        var shown = (await CompanyPayrollRules.ListAsync(db, tenant, company, default)).Single(r => r.Key == PayrollRuleKeys.AbsenceDeductionBase);
        shown.Value.Should().Be("Gross");
        shown.UpcomingValue.Should().Be("Basic");
        shown.UpcomingFrom.Should().Be(nextMonth);
    }

    [Fact]
    public async Task AChangeCannotStartInThePast()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, withApprover: false);

        var result = await As(db, tenant, Maker).Save(new(company, [new PayrollRuleChange(PayrollRuleKeys.MaxProbationDays, "60")], false, null,
            CompanyPayrollRules.Today().AddDays(-1)), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.CompanyPayrollRules.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ClearingARuleFromADate_RestoresTheDefaultFromThenOn_AndKeepsTheHistory()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, withApprover: false);
        var today = CompanyPayrollRules.Today();
        await CompanyPayrollRules.SaveAsync(db, tenant, company, Maker, [new PayrollRuleChange(PayrollRuleKeys.MaxProbationDays, "60")], false, null, DateTime.UtcNow, default);
        await CompanyPayrollRules.SaveAsync(db, tenant, company, Maker, [new PayrollRuleChange(PayrollRuleKeys.MaxProbationDays, null)], false, null, DateTime.UtcNow, default, today.AddDays(10));

        (await CompanyPayrollRules.ResolveAsync(db, tenant, company, default, today.AddDays(9))).MaxProbationDays.Should().Be(60);
        (await CompanyPayrollRules.ResolveAsync(db, tenant, company, default, today.AddDays(10))).MaxProbationDays.Should().BeNull();
    }
}
