using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Salary-advance money controls, brought to the loan module's standard:
/// <list type="bullet">
/// <item>an installment cannot be marked paid by hand (LoansController.MarkInstallmentPaid refuses the same route);
///   payroll deduction is the collection path, proven in <see cref="AdvancePayrollDeductionTests"/>;</item>
/// <item>a request is permission-gated, self-only for a non-HR/Finance caller, money-shaped, and inside every
///   limit of the active AdvancePolicy before the no-approval branch can disburse;</item>
/// <item>an approval is bounded by the request and the policy, data-scoped, and never decided by the requester
///   or by the employee the advance pays.</item>
/// </list>
/// </summary>
public class AdvanceRepaymentControlTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // ── Mark-paid: refused, nothing moves ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-500)]        // a negative "payment" used to INCREASE the debt
    [InlineData(1_000_000)]   // far above the outstanding balance
    [InlineData(500)]         // an ordinary amount, but the request carries no payment reference
    public async Task MarkInstallmentPaid_IsRefused_ForAnyAmount_AndNothingMoves(int amount)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (advanceId, installmentId) = await SeedActiveAdvanceAsync(db, f);

        var result = await Advances(db, f, Guid.NewGuid(), Roles("Finance"))
            .MarkInstallmentPaid(advanceId, installmentId, new PayAdvanceInstallmentRequest(amount, Today, null), Ct);

        Assert.IsType<ConflictObjectResult>(result);
        await AssertUntouchedAsync(db, advanceId, installmentId);
    }

    [Fact]
    public async Task MarkInstallmentPaid_ByTheBorrower_IsRefused_AndNothingMoves()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (advanceId, installmentId) = await SeedActiveAdvanceAsync(db, f);

        // The borrower also holds the Finance role: still cannot record a repayment of their own advance.
        var result = await Advances(db, f, f.EmployeeUserId, Roles("Finance"))
            .MarkInstallmentPaid(advanceId, installmentId, new PayAdvanceInstallmentRequest(500m, Today, null), Ct);

        Assert.IsType<ConflictObjectResult>(result);
        await AssertUntouchedAsync(db, advanceId, installmentId);
    }

    [Fact]
    public async Task MarkInstallmentPaid_ByAUserScopedToAnotherCompany_IsRefused_AndNothingMoves()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (advanceId, installmentId) = await SeedActiveAdvanceAsync(db, f);

        var result = await Advances(db, f, Guid.NewGuid(), Roles("Finance"), new FixedScope(new[] { f.EmployeeId + 999 }))
            .MarkInstallmentPaid(advanceId, installmentId, new PayAdvanceInstallmentRequest(500m, Today, null), Ct);

        Assert.IsType<ConflictObjectResult>(result);
        await AssertUntouchedAsync(db, advanceId, installmentId);
    }

    [Fact]
    public async Task TwoConcurrentMarkPaidCalls_NeitherRetiresDebt()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (advanceId, installmentId) = await SeedActiveAdvanceAsync(db, f);

        var results = await Task.WhenAll(
            Task.Run(() => Advances(db, f, Guid.NewGuid(), Roles("Finance"))
                .MarkInstallmentPaid(advanceId, installmentId, new PayAdvanceInstallmentRequest(500m, Today, null), Ct)),
            Task.Run(() => Advances(db, f, Guid.NewGuid(), Roles("Admin"))
                .MarkInstallmentPaid(advanceId, installmentId, new PayAdvanceInstallmentRequest(500m, Today, null), Ct)));

        Assert.All(results, r => Assert.IsType<ConflictObjectResult>(r));
        await AssertUntouchedAsync(db, advanceId, installmentId);
    }

    // ── Create ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_IsGatedOnTheSelfServiceOrLoanWritePermission()
    {
        var attr = typeof(AdvancesController).GetMethod(nameof(AdvancesController.Create))!
            .GetCustomAttribute<HasPermissionAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(new[] { "loans.self", "loans.write" }, attr!.Permissions);
    }

    [Fact]
    public async Task Create_ByAPlainEmployee_ForSomeoneElse_IsForbidden()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var colleagueUserId = Guid.NewGuid();

        var result = await Advances(db, f, colleagueUserId, Roles("Employee"), perms: new[] { "loans.self" })
            .Create(Request(f, 1_000m), Ct);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(await db.SalaryAdvances.ToListAsync());
    }

    [Fact]
    public async Task Create_ByTheEmployee_ForThemselves_IsAccepted()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);

        var result = await Advances(db, f, f.EmployeeUserId, Roles("Employee"), perms: new[] { "loans.self" })
            .Create(new CreateAdvanceRequest(Guid.Empty, "", 1_000m, "Installments", 2, null, "rent"), Ct);

        Assert.IsType<OkObjectResult>(result);
        var adv = await db.SalaryAdvances.SingleAsync();
        Assert.Equal(f.EmployeeId, adv.EmployeeIntId);
        Assert.Equal("Pending", adv.Status);
    }

    [Fact]
    public async Task Create_ByAnEmployeeLinkedOnlyThroughTheLoginLink_ForThemselves_IsAccepted()
    {
        // The legacy Employee.UserAccountId column is empty; the login is linked through EmployeeUserAccounts,
        // which is what the employee_id claim carries. Both the "myself" default and an explicit self-target work.
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var employee = await db.Employees.SingleAsync(x => x.Id == f.EmployeeId);
        employee.UserAccountId = null;
        await db.SaveChangesAsync();

        Assert.IsType<OkObjectResult>(await Advances(db, f, f.EmployeeUserId, Roles("Employee"), perms: new[] { "loans.self" })
            .Create(new CreateAdvanceRequest(Guid.Empty, "", 1_000m, "Installments", 2, null, "rent"), Ct));
        Assert.IsType<OkObjectResult>(await Advances(db, f, f.EmployeeUserId, Roles("Employee"), perms: new[] { "loans.self" })
            .Create(new CreateAdvanceRequest(f.EmployeePublicId, "", 500m, "Installments", 1, null, "rent"), Ct));
        Assert.Equal(2, await db.SalaryAdvances.CountAsync(x => x.EmployeeIntId == f.EmployeeId));
    }

    [Theory]
    [InlineData(-1_000)]
    [InlineData(0)]
    public async Task Create_WithANonPositiveAmount_IsRejected(int amount)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);

        var result = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager")).Create(Request(f, amount), Ct);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.SalaryAdvances.ToListAsync());
    }

    [Fact]
    public async Task Create_WithFractionalCents_IsRejected()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);

        var result = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager")).Create(Request(f, 100.005m), Ct);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_AboveThePercentageOfSalaryCap_IsRejected()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);   // wage 10,000 SAR, policy 50% → cap 5,000

        var over = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager")).Create(Request(f, 5_000.01m), Ct);
        var bad = Assert.IsType<BadRequestObjectResult>(over);
        Assert.Contains("50% of monthly salary", bad.Value as string);
        Assert.Empty(await db.SalaryAdvances.ToListAsync());

        var atCap = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager")).Create(Request(f, 5_000m), Ct);
        Assert.IsType<OkObjectResult>(atCap);
    }

    [Fact]
    public async Task Create_WhenTheWageIsUnknown_AndThePolicyCapsBySalary_IsRejected()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db, withSalary: false);

        var result = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager")).Create(Request(f, 100m), Ct);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("salary structure", bad.Value as string);
    }

    [Fact]
    public async Task Create_WithoutApprovalRequired_AndOverTheCap_IsRejected_AndNothingIsDisbursed()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db, requiresApproval: false);

        var result = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager")).Create(Request(f, 9_000m), Ct);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.SalaryAdvances.ToListAsync());
        Assert.Empty(await db.AdvanceInstallments.ToListAsync());
        Assert.Empty(await db.FinanceGlEntries.ToListAsync());
    }

    [Fact]
    public async Task Create_WhenThePolicySaysNoApprovalNeeded_StillWaitsForASecondPerson()
    {
        // Strict separation of duties: every advance has one decider. A policy's RequiresApproval=false used to
        // disburse on create with nobody deciding; it no longer skips approval.
        await using var db = CreateDb();
        var f = await SeedAsync(db, requiresApproval: false);

        var result = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager")).Create(Request(f, 3_000m, installments: 3), Ct);

        Assert.IsType<OkObjectResult>(result);
        var adv = await db.SalaryAdvances.SingleAsync();
        Assert.Equal("Pending", adv.Status);
        Assert.Equal(0m, adv.OutstandingBalance);
        Assert.Empty(await db.AdvanceInstallments.ToListAsync());
        Assert.Empty(await db.FinanceGlEntries.Where(x => x.EventType == "Disbursement").ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]    // policy MaxInstallments = 3
    public async Task Create_WithInstallmentsOutsideThePolicy_IsRejected(int installments)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);

        var result = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager"))
            .Create(Request(f, 1_200m, installments: installments), Ct);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.SalaryAdvances.ToListAsync());
    }

    [Fact]
    public async Task Create_WhenThePolicyForbidsInstallments_AllowsOnlyOne()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db, allowInstallments: false);

        Assert.IsType<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Roles("HR Manager"))
            .Create(Request(f, 1_200m, installments: 2), Ct));
        Assert.IsType<OkObjectResult>(await Advances(db, f, Guid.NewGuid(), Roles("HR Manager"))
            .Create(Request(f, 1_200m, installments: 1), Ct));
    }

    [Fact]
    public async Task Create_BeforeTheMinimumServiceMonths_IsRejected()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db, joinedMonthsAgo: 2);   // policy MinServiceMonths = 6

        var result = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager")).Create(Request(f, 1_000m), Ct);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("service month", bad.Value as string);
        Assert.Empty(await db.SalaryAdvances.ToListAsync());
    }

    [Fact]
    public async Task Create_WithinTheCooldownAfterAPayrollClosedAdvance_IsRejected()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        // Payroll closes a fully deducted advance as "Closed", not "Settled"; the cooldown must see it.
        db.SalaryAdvances.Add(new SalaryAdvance
        {
            TenantId = f.TenantId, CompanyId = f.CompanyId, EmployeeId = f.EmployeePublicId, EmployeeIntId = f.EmployeeId,
            AdvanceNumber = "ADV-OLD", Status = "Closed", ApprovedAmount = 1_000m, TotalRepaid = 1_000m,
            CreatedAtUtc = DateTime.UtcNow.AddYears(-1), UpdatedAtUtc = DateTime.UtcNow.AddDays(-10),
        });
        await db.SaveChangesAsync();

        var result = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager")).Create(Request(f, 1_000m), Ct);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("wait", bad.Value as string);
    }

    // ── Approve ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_AboveTheRequestedAmount_IsRejected_AndNothingIsDisbursed()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var advanceId = await SeedPendingAdvanceAsync(db, f, requested: 2_000m);

        var result = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager"))
            .Approve(advanceId, new AdvanceApproveRequest(2_500m, 2, null), Ct);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("requested amount", bad.Value as string);
        await AssertStillPendingAsync(db, advanceId);
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(0)]
    public async Task Approve_WithANonPositiveAmount_IsRejected(int amount)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var advanceId = await SeedPendingAdvanceAsync(db, f, requested: 2_000m);

        var result = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager"))
            .Approve(advanceId, new AdvanceApproveRequest(amount, 1, null), Ct);

        Assert.IsType<BadRequestObjectResult>(result);
        await AssertStillPendingAsync(db, advanceId);
    }

    [Fact]
    public async Task Approve_AboveTheSalaryCap_IsRejected()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        // Raised before the cap applied (or before the policy was tightened): requested 8,000 against a 5,000 cap.
        var advanceId = await SeedPendingAdvanceAsync(db, f, requested: 8_000m);

        var result = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager"))
            .Approve(advanceId, new AdvanceApproveRequest(8_000m, 2, null), Ct);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("50% of monthly salary", bad.Value as string);
        await AssertStillPendingAsync(db, advanceId);

        // The checker may lower it to the cap.
        Assert.IsType<OkObjectResult>(await Advances(db, f, Guid.NewGuid(), Roles("HR Manager"))
            .Approve(advanceId, new AdvanceApproveRequest(5_000m, 2, null), Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Approve_WithInstallmentsOutsideThePolicy_IsRejected(int installments)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var advanceId = await SeedPendingAdvanceAsync(db, f, requested: 2_000m);

        var result = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager"))
            .Approve(advanceId, new AdvanceApproveRequest(2_000m, installments, null), Ct);

        Assert.IsType<BadRequestObjectResult>(result);
        await AssertStillPendingAsync(db, advanceId);
    }

    [Fact]
    public async Task Approve_ByTheRequester_IsRefused()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var requester = Guid.NewGuid();
        var advanceId = await SeedPendingAdvanceAsync(db, f, requested: 2_000m, createdBy: requester);

        var result = await Advances(db, f, requester, Roles("HR Manager"))
            .Approve(advanceId, new AdvanceApproveRequest(2_000m, 2, null), Ct);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Maker-checker", bad.Value as string);
        await AssertStillPendingAsync(db, advanceId);
    }

    [Fact]
    public async Task Approve_ByTheSubject_ThroughTheDirectLogin_IsRefused()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var advanceId = await SeedPendingAdvanceAsync(db, f, requested: 2_000m);

        var result = await Advances(db, f, f.EmployeeUserId, Roles("HR Manager"))
            .Approve(advanceId, new AdvanceApproveRequest(2_000m, 2, null), Ct);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Segregation of duties", bad.Value as string);
        await AssertStillPendingAsync(db, advanceId);
    }

    [Fact]
    public async Task Approve_ByTheSubject_ThroughALoginLinkRowOnly_IsRefused()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        // A second login linked to the employee only through the login-link table (Employees.UserAccountId
        // points elsewhere): the subject bar must still recognise them.
        var linkedLogin = Guid.NewGuid();
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount
        {
            TenantId = f.TenantId, EmployeeId = f.EmployeeId, UserId = linkedLogin, Status = "Active",
        });
        await db.SaveChangesAsync();
        var advanceId = await SeedPendingAdvanceAsync(db, f, requested: 2_000m);

        var result = await Advances(db, f, linkedLogin, Roles("HR Manager"))
            .Approve(advanceId, new AdvanceApproveRequest(2_000m, 2, null), Ct);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Segregation of duties", bad.Value as string);
        await AssertStillPendingAsync(db, advanceId);
    }

    [Fact]
    public async Task Approve_ByAScopedUser_OutsideTheirScope_IsForbidden()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var advanceId = await SeedPendingAdvanceAsync(db, f, requested: 2_000m);

        var result = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager"), new FixedScope(new[] { f.EmployeeId + 999 }))
            .Approve(advanceId, new AdvanceApproveRequest(2_000m, 2, null), Ct);

        Assert.IsType<ForbidResult>(result);
        await AssertStillPendingAsync(db, advanceId);
    }

    [Fact]
    public async Task Reject_ByAScopedUser_OutsideTheirScope_IsForbidden()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var advanceId = await SeedPendingAdvanceAsync(db, f, requested: 2_000m);

        var result = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager"), new FixedScope(new[] { f.EmployeeId + 999 }))
            .Reject(advanceId, new RejectRequest("no"), Ct);

        Assert.IsType<ForbidResult>(result);
        await AssertStillPendingAsync(db, advanceId);
    }

    [Fact]
    public async Task Approve_ByAnIndependentApprover_WithinPolicy_Disburses()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var advanceId = await SeedPendingAdvanceAsync(db, f, requested: 3_000m);

        var result = await Advances(db, f, Guid.NewGuid(), Roles("HR Manager"), new FixedScope(new[] { f.EmployeeId }))
            .Approve(advanceId, new AdvanceApproveRequest(2_400m, 3, null), Ct);

        Assert.IsType<OkObjectResult>(result);
        var adv = await db.SalaryAdvances.SingleAsync(x => x.Id == advanceId);
        Assert.Equal("Active", adv.Status);
        Assert.Equal(2_400m, adv.OutstandingBalance);
        Assert.Equal(3, await db.AdvanceInstallments.CountAsync(x => x.AdvanceId == advanceId));
        Assert.Single(await db.FinanceGlEntries.Where(x => x.SourceEntityId == advanceId && x.EventType == "Disbursement").ToListAsync());
    }

    // ── Fixture ───────────────────────────────────────────────────────────────────────────────────

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private sealed record Fx(Guid TenantId, Guid CompanyId, int EmployeeId, Guid EmployeePublicId, Guid EmployeeUserId);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>One company (SAR), one employee on a 10,000 SAR monthly wage linked to a login, and an active
    /// policy: 50% of salary, 3 installments, 6 service months, 2 advances a year, 3-month cooldown.</summary>
    private static async Task<Fx> SeedAsync(ZayraDbContext db, bool requiresApproval = true, bool withSalary = true,
        bool allowInstallments = true, int joinedMonthsAgo = 24)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Advance Co", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "ADV-001", FullName = "Huda Saleh", EnglishName = "Huda Saleh",
            Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddMonths(-joinedMonthsAgo), UserAccountId = userId,
        };
        db.AddRange(company, employee);
        db.AdvancePolicies.Add(new AdvancePolicy
        {
            TenantId = tenantId, PolicyName = "Standard", MaxPercentageOfSalary = 50, MaxAdvancesPerYear = 2,
            MinServiceMonths = 6, AllowInstallments = allowInstallments, MaxInstallments = 3, CooldownMonths = 3,
            RequiresApproval = requiresApproval, IsActive = true,
        });
        await db.SaveChangesAsync();
        if (withSalary)
        {
            db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = employee.Id, SalaryStructureId = Guid.NewGuid(),
                BasicSalary = 8_000m, HousingAllowance = 2_000m, Currency = "SAR",
                EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1)), IsActive = true,
            });
            await db.SaveChangesAsync();
        }
        return new Fx(tenantId, company.Id, employee.Id, employee.PublicId, userId);
    }

    private static CreateAdvanceRequest Request(Fx f, decimal amount, int installments = 2) =>
        new(f.EmployeePublicId, "Huda Saleh", amount, "Installments", installments, null, "family", f.EmployeeId);

    private static async Task<Guid> SeedPendingAdvanceAsync(ZayraDbContext db, Fx f, decimal requested, Guid? createdBy = null)
    {
        var adv = new SalaryAdvance
        {
            TenantId = f.TenantId, CompanyId = f.CompanyId, EmployeeId = f.EmployeePublicId, EmployeeIntId = f.EmployeeId,
            EmployeeName = "Huda Saleh", AdvanceNumber = $"ADV-P-{Guid.NewGuid():N}"[..14], RequestedAmount = requested,
            RepaymentType = "Installments", Installments = 2, Status = "Pending", CreatedBy = createdBy ?? Guid.NewGuid(),
        };
        db.SalaryAdvances.Add(adv);
        await db.SaveChangesAsync();
        return adv.Id;
    }

    private static async Task<(Guid AdvanceId, Guid InstallmentId)> SeedActiveAdvanceAsync(ZayraDbContext db, Fx f)
    {
        var adv = new SalaryAdvance
        {
            TenantId = f.TenantId, CompanyId = f.CompanyId, EmployeeId = f.EmployeePublicId, EmployeeIntId = f.EmployeeId,
            EmployeeName = "Huda Saleh", AdvanceNumber = "ADV-A-1", RequestedAmount = 2_000m, ApprovedAmount = 2_000m,
            OutstandingBalance = 2_000m, InstallmentAmount = 1_000m, Installments = 2, RepaymentType = "Installments",
            Status = "Active", CreatedBy = Guid.NewGuid(),
        };
        var inst = new AdvanceInstallment
        {
            TenantId = f.TenantId, AdvanceId = adv.Id, InstallmentNumber = 1, DueDate = Today, AmountDue = 1_000m, Status = "Pending",
        };
        db.AddRange(adv, inst);
        await db.SaveChangesAsync();
        return (adv.Id, inst.Id);
    }

    private static async Task AssertUntouchedAsync(ZayraDbContext db, Guid advanceId, Guid installmentId)
    {
        var adv = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == advanceId);
        Assert.Equal("Active", adv.Status);
        Assert.Equal(2_000m, adv.OutstandingBalance);
        Assert.Equal(0m, adv.TotalRepaid);
        var inst = await db.AdvanceInstallments.AsNoTracking().SingleAsync(x => x.Id == installmentId);
        Assert.Equal("Pending", inst.Status);
        Assert.Equal(0m, inst.AmountPaid);
        Assert.Empty(await db.FinanceGlEntries.Where(x => x.SourceEntityId == advanceId).ToListAsync());
        Assert.Empty(await db.AdvanceAuditLogs.Where(x => x.AdvanceId == advanceId).ToListAsync());
    }

    private static async Task AssertStillPendingAsync(ZayraDbContext db, Guid advanceId)
    {
        var adv = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == advanceId);
        Assert.Equal("Pending", adv.Status);
        Assert.Equal(0m, adv.ApprovedAmount);
        Assert.Empty(await db.AdvanceInstallments.Where(x => x.AdvanceId == advanceId).ToListAsync());
        Assert.Empty(await db.FinanceGlEntries.Where(x => x.SourceEntityId == advanceId).ToListAsync());
        Assert.Empty(await db.AdvanceApprovals.Where(x => x.AdvanceId == advanceId).ToListAsync());
    }

    private static string[] Roles(params string[] roles) => roles;

    private static AdvancesController Advances(ZayraDbContext db, Fx f, Guid userId, string[] roles,
        IDataScopeService? scope = null, string[]? perms = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", f.TenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "Advance Tester"),
        };
        // The token service issues employee_id from the login-to-employee link; the employee's own login carries it.
        if (userId == f.EmployeeUserId) claims.Add(new Claim("employee_id", f.EmployeeId.ToString()));
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange((perms ?? Array.Empty<string>()).Select(p => new Claim("permission", p)));
        return new AdvancesController(db, scope ?? new FixedScope(null))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            },
        };
    }

    private sealed class FixedScope(int[]? allowed) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(allowed is null
                ? new DataScope { Level = DataScopeLevel.Organization }
                : new DataScope { Level = DataScopeLevel.Department, AllowedEmployeeIds = allowed });
    }
}
