using System.Data.Common;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Against REAL PostgreSQL, two money races around approved overtime, each choreographed deterministically with a second
/// transaction that commits inside the first one's window.
/// <list type="bullet">
///   <item>Comp-off conversion vs the payroll run: the conversion read the overtime's payroll impact as PendingPayroll; the run then
///   marked it Processed (paid) and committed; the conversion went on to delete the paid impact and grant the day off: cash AND
///   leave for the same work. The second transaction here is the run's own compare-and-swap mark on the impact.</item>
///   <item>Two final approvals racing for the last of a department's overtime budget in Block mode: one must be refused.</item>
/// </list>
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OvertimeMoneyRacePostgresTests
{
    private readonly PostgresFixture _fx;
    public OvertimeMoneyRacePostgresTests(PostgresFixture fx) => _fx = fx;

    private static readonly DateOnly Day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);

    private sealed record World(Guid TenantId, Guid CompanyId, int EmployeeId, Guid PolicyId, Guid DepartmentId);

    // ── comp-off conversion vs the payroll run ───────────────────────────────────────────────────────

    [Fact]
    public async Task ConversionRacingThePayrollRun_NeverGivesCashAndADayOff()
    {
        var w = await SeedAsync();
        var (requestId, impactId) = await ApprovedWithPendingImpactAsync(w, slot: 0);
        var runId = Guid.NewGuid();
        var gate = new AfterFirstRead("FROM overtime_payroll_impacts", async () =>
        {
            // What PayrollRunProcessor does when it consumes the impact, committed in its own transaction.
            await using var db = _fx.CreateDb();
            await db.OvertimePayrollImpacts
                .Where(x => x.TenantId == w.TenantId && x.Id == impactId && x.Status != "Processed")
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed").SetProperty(p => p.PayrollRunId, runId).SetProperty(p => p.ProcessedAtUtc, DateTime.UtcNow));
        });

        ActionResult<OvertimeCompOffConversion> result;
        await using (var db = GatedDb(gate))
            result = await Controller(db, w).CreateCompOffConversion(new CompOffConversionRequest(requestId, 1m), CancellationToken.None);
        gate.Fired.Should().BeTrue("the choreography must actually interleave the run");

        await using var check = _fx.CreateDb();
        var impact = await check.OvertimePayrollImpacts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == impactId);
        impact.Should().NotBeNull("the impact the run paid is the record of that payment; it must survive");
        impact!.Status.Should().Be("Processed");
        (await check.CompOffCredits.AsNoTracking().CountAsync(x => x.TenantId == w.TenantId)).Should().Be(0, "it was paid in cash, so no day off");
        (await check.OvertimeCompOffConversions.AsNoTracking().CountAsync(x => x.TenantId == w.TenantId)).Should().Be(0);
        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task ConversionWithoutARace_StillWithdrawsTheCashAndGrantsTheDay()
    {
        var w = await SeedAsync();
        var (requestId, impactId) = await ApprovedWithPendingImpactAsync(w, slot: 1);
        await using (var db = _fx.CreateDb())
            (await Controller(db, w).CreateCompOffConversion(new CompOffConversionRequest(requestId, 1m), CancellationToken.None))
                .Result.Should().BeOfType<CreatedResult>();

        await using var check = _fx.CreateDb();
        (await check.OvertimePayrollImpacts.AsNoTracking().AnyAsync(x => x.Id == impactId)).Should().BeFalse();
        (await check.CompOffCredits.AsNoTracking().CountAsync(x => x.TenantId == w.TenantId)).Should().Be(1);
    }

    // ── two approvals vs the last of the budget ──────────────────────────────────────────────────────

    [Fact]
    public async Task TwoApprovalsRacingForTheLastOfABlockedBudget_OnlyOneIsApproved()
    {
        var w = await SeedAsync(budgetMode: "Block");
        // Price one approval first, then leave room for exactly one more.
        var first = await PendingHrRequestAsync(w, slot: 2);
        await using (var db = _fx.CreateDb())
            (await Admin(db, w).Approve(first, new OvertimeDecisionRequest(0, "ok"), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        decimal oneApproval;
        await using (var db = _fx.CreateDb())
        {
            oneApproval = (await db.OvertimeCalculations.AsNoTracking().SingleAsync(x => x.OvertimeRequestId == first)).Amount;
            oneApproval.Should().BeGreaterThan(0m);
            await db.OvertimeBudgets.Where(b => b.TenantId == w.TenantId)
                .ExecuteUpdateAsync(x => x.SetProperty(b => b.BudgetAmount, oneApproval * 2.5m));
        }

        var a = await PendingHrRequestAsync(w, slot: 3);
        var b = await PendingHrRequestAsync(w, slot: 4);
        Task<ActionResult>? other = null;
        var gate = new AfterFirstRead("FOR UPDATE", () =>
        {
            other = Task.Run(async () =>
            {
                await using var db = _fx.CreateDb();
                return await Admin(db, w).Approve(b, new OvertimeDecisionRequest(0, "ok"), CancellationToken.None);
            });
            return Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(3)));
        }, onlyTable: "overtime_budgets");

        ActionResult resultA;
        await using (var db = GatedDb(gate))
            resultA = await Admin(db, w).Approve(a, new OvertimeDecisionRequest(0, "ok"), CancellationToken.None);
        gate.Fired.Should().BeTrue();
        var resultB = await other!;

        new[] { resultA, resultB }.Count(r => r is OkObjectResult).Should().Be(1, "the budget had room for one more approval, not two");
        new[] { resultA, resultB }.Count(r => r is ConflictObjectResult).Should().Be(1);
        await using var check = _fx.CreateDb();
        var budget = await check.OvertimeBudgets.AsNoTracking().SingleAsync(x => x.TenantId == w.TenantId);
        budget.ConsumedAmount.Should().Be(oneApproval * 2, "two approvals in all, the first and the race's winner");
        budget.ConsumedAmount.Should().BeLessThanOrEqualTo(budget.BudgetAmount);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────────

    private ZayraDbContext GatedDb(DbCommandInterceptor gate) => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
        .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance,
            Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance, gate)
        .Options);

    private static OvertimeController Controller(ZayraDbContext db, World w) => As(db, w, "HR Manager", "overtime.policy_manage", "overtime.read");
    private static OvertimeController Admin(ZayraDbContext db, World w) => As(db, w, "Admin", "overtime.approve", "overtime.read");

    private static OvertimeController As(ZayraDbContext db, World w, string role, params string[] permissions)
    {
        var claims = new List<Claim> { new("tenant_id", w.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, role) };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new OvertimeController(db, new OrgScope(), new HrmHierarchyService(db, new AuditService(db)))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } },
        };
    }

    private async Task<World> SeedAsync(string? budgetMode = null)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = $"OT money {Guid.NewGuid():N}"[..20], CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"OTM-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
        };
        db.Companies.Add(company);
        var dept = new Department { TenantId = tenantId, CompanyId = company.Id, Code = "OPS", NameEn = "Operations", NameAr = "العمليات" };
        db.Departments.Add(dept);
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, DepartmentId = dept.Id, EmployeeCode = "OTM-1", FullName = "Money Employee",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2), Salary = 12_000m,
        };
        db.Employees.Add(employee);
        var policy = new OvertimePolicy
        {
            TenantId = tenantId, Code = "STD", Name = "Standard", IsActive = true, RoundingRule = "None",
            MinimumMinutes = 30, MaximumMinutesPerDay = 600, MonthlyCapMinutes = 60_000, AllowCompOffConversion = true,
        };
        db.OvertimePolicies.Add(policy);
        db.OvertimeBudgets.Add(new OvertimeBudget { TenantId = tenantId, DepartmentId = dept.Id, Year = Day.Year, Month = Day.Month, BudgetAmount = 1_000_000m, Currency = "SAR" });
        if (budgetMode is not null)
            db.CompanyPayrollRules.Add(new CompanyPayrollRule { TenantId = tenantId, CompanyId = company.Id, RuleKey = PayrollRuleKeys.OvertimeBudgetMode, RuleValue = budgetMode });
        await db.SaveChangesAsync();
        return new World(tenantId, company.Id, employee.Id, policy.Id, dept.Id);
    }

    private async Task<OvertimeRequest> NewRequestAsync(ZayraDbContext db, World w, int slot, string status, int approvedMinutes)
    {
        var start = Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddMinutes(slot * 70);
        var r = new OvertimeRequest
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, EmployeeId = w.EmployeeId, EmployeeName = "Money Employee", OvertimePolicyId = w.PolicyId,
            WorkDate = Day, StartTimeUtc = start, EndTimeUtc = start.AddMinutes(60), RequestedMinutes = 60, ApprovedMinutes = approvedMinutes,
            Reason = "Close", Status = status, CreatedBy = Guid.NewGuid(),
        };
        db.OvertimeRequests.Add(r);
        await db.SaveChangesAsync();
        return r;
    }

    private async Task<(Guid RequestId, Guid ImpactId)> ApprovedWithPendingImpactAsync(World w, int slot)
    {
        await using var db = _fx.CreateDb();
        var r = await NewRequestAsync(db, w, slot, "Approved", 60);
        var impact = new OvertimePayrollImpact
        {
            TenantId = w.TenantId, OvertimeRequestId = r.Id, EmployeeId = w.EmployeeId, Minutes = 60, Amount = 75m, ApprovedMultiplier = 1.5m,
        };
        db.OvertimePayrollImpacts.Add(impact);
        await db.SaveChangesAsync();
        return (r.Id, impact.Id);
    }

    private async Task<Guid> PendingHrRequestAsync(World w, int slot)
    {
        await using var db = _fx.CreateDb();
        return (await NewRequestAsync(db, w, slot, "PendingHR", 0)).Id;
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }

    /// <summary>After the first command whose text contains <paramref name="marker"/> (and the table, if given), runs the other side.</summary>
    private sealed class AfterFirstRead(string marker, Func<Task> between, string? onlyTable = null) : DbCommandInterceptor
    {
        private int _fired;
        public bool Fired => _fired == 1;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(marker, StringComparison.Ordinal)
                && (onlyTable is null || command.CommandText.Contains(onlyTable, StringComparison.Ordinal))
                && Interlocked.Exchange(ref _fired, 1) == 0)
                await between();
            return result;
        }
    }
}
