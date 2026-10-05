using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class LoanPolicyEligibilityTests
{
    [Fact]
    public async Task CompanyPolicyZeroAmountAddsNoCapButTypeLimitsAreFrozenAndEnforced()
    {
        await using var h = await Fixture.Create();
        h.Type.MaxAmount = 500; h.Type.MaxInstallments = 6; h.Type.MinServiceMonths = 12;
        var policy = h.Policy(0); policy.MaxInstallments = 24; policy.MinServiceMonths = 0;
        h.Db.Add(policy); await h.Db.SaveChangesAsync();
        var eligible = await h.Assess(400);
        Assert.True(eligible.Eligible); Assert.Equal(500, eligible.MaxAvailableAmount);
        var snapshot = JsonSerializer.Deserialize<LoanPolicy>(eligible.PolicySnapshotJson)!;
        Assert.Equal(500, snapshot.MaxAmount); Assert.Equal(6, snapshot.MaxInstallments); Assert.Equal(12, snapshot.MinServiceMonths);
        Assert.Contains("AmountLimit", (await h.Assess(600)).Codes);
        var tooMany = await new LoanEligibilityService(h.Db).EvaluateAsync(h.Tid, h.Employee, h.Type, 400, 12, "BankTransfer");
        Assert.Contains("Installments", tooMany.Codes);
        // Historical terms remain stable, whereas a new application sees the tightened type.
        h.Type.MaxAmount = 100; h.Type.MaxInstallments = 2; h.Type.MinServiceMonths = 36;
        await h.Db.SaveChangesAsync();
        var frozen = await new LoanEligibilityService(h.Db).EvaluateAsync(h.Tid, h.Employee, h.Type, 400, 4, "BankTransfer", policySnapshotJson: eligible.PolicySnapshotJson);
        Assert.True(frozen.Eligible); Assert.Equal(500, frozen.MaxAvailableAmount);
        var fresh = await h.Assess(400);
        Assert.Contains("AmountLimit", fresh.Codes); Assert.Contains("Installments", fresh.Codes); Assert.Contains("MinService", fresh.Codes);
        Assert.Equal(0, (await h.Db.LoanPolicies.AsNoTracking().SingleAsync()).MaxAmount); // only snapshot, never the published source, is combined
    }

    [Fact]
    public async Task PublishingPolicyVersionsRetiresOnlyPreviousHeadAndRetainsHistoricalTerms()
    {
        await using var h = await Fixture.Create();
        var request = new LoanPolicyRequest(h.Employee.CompanyId!.Value, h.Type.Id, "Personal policy", MaxAmount: 500,
            AllowedEmploymentStatuses: ["Active"], AllowedRepaymentMethods: ["BankTransfer"]);
        var controller = h.Controller("HR Manager");
        Assert.IsType<OkObjectResult>(await controller.CreateLoanPolicy(request, default));
        Assert.IsType<OkObjectResult>(await controller.CreateLoanPolicy(request with { MaxAmount = 1000 }, default));
        var versions = await h.Db.Set<LoanPolicy>().OrderBy(x => x.Version).ToListAsync();
        Assert.Equal(2, versions.Count); Assert.False(versions[0].IsActive); Assert.True(versions[1].IsActive);
        Assert.Equal(500, versions[0].MaxAmount); Assert.Equal(1000, versions[1].MaxAmount);
        Assert.Equal(2, (await h.Assess(750)).PolicyVersion);
    }

    [Fact]
    public async Task CompanyPolicyWins_AndSavedPolicyDoesNotChangeWhenNewVersionPublished()
    {
        await using var h = await Fixture.Create();
        h.Db.Add(new LoanPolicy { TenantId = h.Tid, LoanTypeId = h.Type.Id, MaxAmount = 50, MaxConcurrentLoans = 10 });
        var own = h.Policy(500); h.Db.Add(own); await h.Db.SaveChangesAsync();
        var original = await h.Assess(400);
        Assert.True(original.Eligible); Assert.Equal(own.Id, original.PolicyId);
        var next = h.Policy(100); next.Version = 2; h.Db.Add(next); await h.Db.SaveChangesAsync();
        Assert.Contains("AmountLimit", (await h.Assess(400)).Codes);
        var saved = await new LoanEligibilityService(h.Db).EvaluateAsync(h.Tid, h.Employee, h.Type, 400, 4, "BankTransfer", policySnapshotJson: original.PolicySnapshotJson);
        Assert.True(saved.Eligible); Assert.Equal(1, saved.PolicyVersion);
    }

    [Fact]
    public async Task PendingAndApprovedCommitmentsReserveExposure_AndCurrentLoanIsExcluded()
    {
        await using var h = await Fixture.Create();
        var policy = h.Policy(1000); policy.MaxTotalOutstanding = 500; policy.MaxConcurrentLoans = 3;
        var pending = h.Loan("Pending", 200); var approved = h.Loan("Approved", 100);
        h.Db.AddRange(policy, pending, approved); await h.Db.SaveChangesAsync();
        var result = await h.Assess(250);
        Assert.Equal(300, result.CommittedAmount); Assert.Equal(200, result.MaxAvailableAmount); Assert.Contains("AmountLimit", result.Codes);
        var excluded = await new LoanEligibilityService(h.Db).EvaluateAsync(h.Tid, h.Employee, h.Type, 200, 4, "BankTransfer", pending.Id);
        Assert.True(excluded.Eligible); Assert.Equal(100, excluded.CommittedAmount);
    }

    [Fact]
    public async Task SalaryLimitsFailClosedForMissingOrDifferentCurrencySalary()
    {
        await using var h = await Fixture.Create();
        var policy = h.Policy(10000); policy.MaxMultiplierOfSalary = 2; h.Db.Add(policy); await h.Db.SaveChangesAsync();
        Assert.Contains("SalaryCurrency", (await h.Assess(100)).Codes);
        var salary = new EmployeeSalaryStructure { TenantId = h.Tid, EmployeeId = h.Employee.Id, BasicSalary = 1000, Currency = "USD", EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)) };
        h.Db.Add(salary); await h.Db.SaveChangesAsync();
        Assert.Contains("SalaryCurrency", (await h.Assess(100)).Codes);
        salary.Currency = "SAR"; await h.Db.SaveChangesAsync();
        Assert.True((await h.Assess(100)).Eligible); Assert.Equal(2000, (await h.Assess(100)).MaxAvailableAmount);
    }

    [Fact]
    public async Task UnlikeCurrencyCommitmentsFailClosedAndAreNotAddedToCurrentCurrencyTotals()
    {
        await using var h = await Fixture.Create();
        var previous = h.Loan("Active", 200); previous.OutstandingBalance = 200; previous.Currency = "USD";
        var current = h.Loan("Pending", 100); current.Currency = "SAR";
        h.Db.AddRange(previous, current); await h.Db.SaveChangesAsync();
        var result = await h.Assess(50);
        Assert.False(result.Eligible); Assert.Contains("CommitmentCurrency", result.Codes); Assert.Equal(100, result.CommittedAmount);
    }

    [Fact]
    public async Task StatusNoticeProbationServiceAndFrequencyAreEnforced()
    {
        await using var h = await Fixture.Create();
        var p = h.Policy(500); p.MinServiceMonths = 12; p.RequireProbationCompleted = true;
        p.AllowedRepaymentFrequenciesJson = "[\"Weekly\"]";
        h.Employee.JoiningDate = DateTime.UtcNow.AddMonths(-1); h.Employee.Status = "Offboarded";
        h.Db.AddRange(p, new EmployeeOffboarding { TenantId = h.Tid, EmployeeId = h.Employee.Id }); await h.Db.SaveChangesAsync();
        var result = await h.Assess(100);
        foreach (var code in new[] { "EmploymentStatus", "Notice", "Probation", "MinService", "RepaymentFrequency" }) Assert.Contains(code, result.Codes);
    }

    [Fact]
    public async Task OverdueSchedulesAndRecentlyClosedLoansBlockFurtherApplications()
    {
        await using var h = await Fixture.Create();
        var p = h.Policy(1000); p.CooldownMonthsAfterRepayment = 3;
        var active = h.Loan("Active", 100); active.OutstandingBalance = 100;
        var closed = h.Loan("Closed", 100); closed.UpdatedAtUtc = DateTime.UtcNow;
        h.Db.AddRange(p, active, closed, new LoanInstallment { TenantId = h.Tid, LoanId = active.Id, DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), AmountDue = 25 });
        await h.Db.SaveChangesAsync();
        var result = await h.Assess(100); Assert.Contains("Overdue", result.Codes); Assert.Contains("Cooldown", result.Codes);
    }

    [Fact]
    public async Task NewRequestRequiresHrEvenWhenTypePreviouslyAutoApproved_AndFinanceCannotDecide()
    {
        await using var h = await Fixture.Create();
        h.Type.RequiresApproval = false; await h.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await h.Controller("Employee").CreateLoan(new(h.Employee.PublicId, "", h.Type.Id, 100, 4, null, h.Employee.Id), default));
        var loan = await h.Db.EmployeeLoans.SingleAsync(); var step = await h.Db.LoanApprovals.SingleAsync();
        Assert.Equal("Pending", loan.Status); Assert.Equal("HR Manager", step.ApproverRole);
        Assert.NotEqual("{}", loan.PolicySnapshotJson);
        Assert.IsType<ForbidResult>(await h.Controller("Finance").DecideApproval(loan.Id, step.Id, new("Approved", null, null, null, null), default));
        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").DecideApproval(loan.Id, step.Id, new("Approved", null, null, null, null), default));
        Assert.Equal("Approved", loan.Status); Assert.Equal(0, loan.OutstandingBalance);
    }

    [Fact]
    public async Task ThresholdAddsHrDirector_AndSoftExceptionOptInDoesNotApproveLoan()
    {
        await using var h = await Fixture.Create();
        var p = h.Policy(50); p.AllowExceptions = true; p.AdditionalApprovalThreshold = 90;
        h.Db.Add(p); await h.Db.SaveChangesAsync();
        var req = new CreateLoanRequest(h.Employee.PublicId, "", h.Type.Id, 100, 4, null, h.Employee.Id);
        Assert.IsType<BadRequestObjectResult>(await h.Controller("Employee").CreateLoan(req, default));
        Assert.IsType<OkObjectResult>(await h.Controller("Employee").CreateLoan(req with { RequestPolicyException = true }, default));
        var loan = await h.Db.EmployeeLoans.SingleAsync(); var steps = await h.Db.LoanApprovals.OrderBy(x => x.StepOrder).ToListAsync();
        Assert.Equal(new[] { "HR Manager", "HR Director" }, steps.Select(x => x.ApproverRole));
        Assert.IsType<BadRequestObjectResult>(await h.Controller("HR Manager").DecideApproval(loan.Id, steps[0].Id, new("Approved", null, null, null, null), default));
        Assert.Equal("Pending", loan.Status);
    }

    [Fact]
    public async Task LaterHrApproverCannotEnlargeTermsAlreadyApprovedByFirstReviewer()
    {
        await using var h = await Fixture.Create();
        var p = h.Policy(1000); p.AdditionalApprovalThreshold = 90;
        h.Db.Add(p); await h.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await h.Controller("Employee").CreateLoan(new(h.Employee.PublicId, "", h.Type.Id, 100, 4, null, h.Employee.Id), default));
        var loan = await h.Db.EmployeeLoans.SingleAsync(); var steps = await h.Db.LoanApprovals.OrderBy(x => x.StepOrder).ToListAsync();
        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").DecideApproval(loan.Id, steps[0].Id, new("Approved", null, 80, 4, null), default));
        Assert.Equal("Pending", loan.Status); Assert.Equal(80, loan.ApprovedAmount);
        Assert.IsType<BadRequestObjectResult>(await h.Controller("HR Director").DecideApproval(loan.Id, steps[1].Id, new("Approved", null, 100, 4, null), default));
        Assert.IsType<OkObjectResult>(await h.Controller("HR Director").DecideApproval(loan.Id, steps[1].Id, new("Approved", null, null, null, null), default));
        Assert.Equal("Approved", loan.Status); Assert.Equal(80, loan.ApprovedAmount);
    }

    [Fact]
    public async Task InactiveStatusCannotBeBypassedWithExceptionRequest()
    {
        await using var h = await Fixture.Create();
        var p = h.Policy(50); p.AllowExceptions = true; h.Employee.Status = "Terminated";
        h.Db.Add(p); await h.Db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await h.Controller("Employee").CreateLoan(new(h.Employee.PublicId, "", h.Type.Id, 100, 4, null, h.Employee.Id, RequestPolicyException: true), default));
        Assert.Empty(await h.Db.EmployeeLoans.ToListAsync());
    }

    [Fact]
    public async Task RoleAndEmployeeScopeWithoutReadPermission_CannotReadLoanBookEvenWithMineFilter()
    {
        await using var h = await Fixture.Create();
        var loan = h.Loan("Active", 100);
        h.Db.Add(loan);
        await h.Db.SaveChangesAsync();
        var controller = h.Controller("Manager");

        foreach (var result in new[]
        {
            await controller.ListLoans(null, null, mine: true),
            await controller.GetLoan(loan.Id, default),
            await controller.GetInstallments(loan.Id, default),
            await controller.ListLoanChanges(loan.Id, default)
        })
        {
            var denied = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, denied.StatusCode);
            Assert.Equal("loans_read_forbidden", JsonSerializer.SerializeToElement(denied.Value).GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task SelfPermission_ForcesOwnerFilterAndRejectsAnotherVisibleEmployeesLoan()
    {
        await using var h = await Fixture.Create();
        var otherEmployee = new Employee
        {
            TenantId = h.Tid, CompanyId = h.Employee.CompanyId, UserAccountId = Guid.NewGuid(),
            FullName = "Another borrower", EmployeeCode = "P2", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        h.Db.Add(otherEmployee);
        await h.Db.SaveChangesAsync();
        var own = h.Loan("Active", 100);
        var other = h.Loan("Active", 200);
        other.EmployeeIntId = otherEmployee.Id;
        other.EmployeeId = otherEmployee.PublicId;
        h.Db.AddRange(own, other);
        await h.Db.SaveChangesAsync();
        var controller = h.Controller("Employee");
        ((ClaimsIdentity)controller.User.Identity!).AddClaim(new Claim("permission", "loans.self"));

        var listed = Assert.IsType<OkObjectResult>(await controller.ListLoans(null, null, mine: false));
        var json = JsonSerializer.SerializeToElement(listed.Value);
        Assert.Equal(1, json.GetProperty("total").GetInt32());
        Assert.Equal(own.Id, json.GetProperty("items")[0].GetProperty("Id").GetGuid());
        Assert.IsType<OkObjectResult>(await controller.GetLoan(own.Id, default));
        Assert.IsType<ForbidResult>(await controller.GetLoan(other.Id, default));
    }

    [Fact]
    public async Task SeededLoanRoles_HaveUniqueLevelsAndRepairEmployeeBroadReadGrant()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "Loan roles", Slug = $"loan-roles-{tid:N}" });
        await db.SaveChangesAsync();
        var seeder = new AuthSeeder(db);
        await seeder.EnsureTenantRolesAsync(tid);
        var employee = await db.Roles.Include(x => x.RolePermissions).SingleAsync(x => x.TenantId == tid && x.Name == "Employee");
        var broadRead = await db.Permissions.SingleAsync(x => x.Key == "loans.read");
        employee.RolePermissions.Add(new RolePermission { RoleId = employee.Id, PermissionId = broadRead.Id });
        await db.SaveChangesAsync();

        await seeder.EnsureTenantRolesAsync(tid);
        db.ChangeTracker.Clear();
        var roles = await db.Roles.Where(x => x.TenantId == tid).Include(x => x.RolePermissions).ThenInclude(x => x.Permission).ToListAsync();
        Assert.Equal(roles.Count, roles.Select(x => x.AuthorityLevel).Distinct().Count());
        Assert.Equal(7, roles.Single(x => x.Name == "Finance").AuthorityLevel);
        var employeeRole = roles.Single(x => x.Name == "Employee");
        var employeeKeys = employeeRole.RolePermissions.Select(x => x.Permission!.Key).ToArray();
        Assert.Contains("loans.self", employeeKeys);
        Assert.DoesNotContain("loans.read", employeeKeys);
    }

    [Theory]
    [InlineData("loans.read")]
    [InlineData("loans.write")]
    public async Task LoanChanges_AuthorizedReaderCanReadOnlyRequestedLoanChanges(string permission)
    {
        await using var h = await Fixture.Create();
        var loan = h.Loan("Active", 100);
        var visibleChange = new LoanChangeRequest
        {
            TenantId = h.Tid, CompanyId = loan.CompanyId, LoanId = loan.Id,
            ChangeType = "Reschedule", Reason = "Requested revised schedule"
        };
        h.Db.AddRange(loan, visibleChange, new LoanChangeRequest
        {
            TenantId = h.Tid, CompanyId = loan.CompanyId, LoanId = Guid.NewGuid(),
            ChangeType = "Reschedule", Reason = "Another loan's schedule"
        });
        await h.Db.SaveChangesAsync();
        var controller = h.Controller("Manager");
        ((ClaimsIdentity)controller.User.Identity!).AddClaim(new Claim("permission", permission));

        var result = Assert.IsType<OkObjectResult>(await controller.ListLoanChanges(loan.Id, default));
        var changes = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal(1, changes.GetArrayLength());
        Assert.Equal(visibleChange.Id, changes[0].GetProperty("Id").GetGuid());
    }

    [Fact]
    public async Task TransferredEmployeeSeesOwnHistoricalDebtButNotAnotherEmployeesLoan()
    {
        await using var h = await Fixture.Create();
        var oldCompany = new Company { TenantId = h.Tid, LegalNameEn = "Prior employer", DefaultCurrency = "SAR" };
        var userId = Guid.NewGuid(); h.Employee.UserAccountId = userId;
        var own = h.Loan("Active", 250); own.CompanyId = oldCompany.Id; own.OutstandingBalance = 200; own.TotalRepaid = 50;
        var other = h.Loan("Active", 300); other.CompanyId = oldCompany.Id; other.EmployeeIntId = 999;
        var ownReceipt = new LoanRepayment { TenantId = h.Tid, CompanyId = oldCompany.Id, LoanId = own.Id, Amount = 50,
            Reference = "OWNER-HISTORY", PaymentMethod = "BankTransfer", PaidDate = DateOnly.FromDateTime(DateTime.UtcNow) };
        var otherReceipt = new LoanRepayment { TenantId = h.Tid, CompanyId = oldCompany.Id, LoanId = other.Id, Amount = 60,
            Reference = "OTHER-HISTORY", PaymentMethod = "BankTransfer", PaidDate = DateOnly.FromDateTime(DateTime.UtcNow) };
        h.Db.AddRange(oldCompany, own, other, ownReceipt, otherReceipt,
            new LoanInstallment { TenantId = h.Tid, LoanId = own.Id, InstallmentNumber = 1, AmountDue = 250, AmountPaid = 50,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)) });
        await h.Db.SaveChangesAsync();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim("tenant_id", h.Tid.ToString()), new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("permission", "loans.self"),
            new Claim(ClaimTypes.Role, "Employee"), new Claim("entity_access", JsonSerializer.Serialize(new { c = h.Employee.CompanyId, r = "Employee" })) }, "test")) };
        await using var scopedDb = new ZayraDbContext(h.Options, new HttpContextAccessor { HttpContext = http });
        var controller = new LoansController(scopedDb, new Scope()) { ControllerContext = new() { HttpContext = http } };
        // loans.self is owner-only even when the caller explicitly sends mine=false.
        var listed = Assert.IsType<OkObjectResult>(await controller.ListLoans(null, null, mine: false));
        var listJson = JsonSerializer.SerializeToElement(listed.Value);
        Assert.Equal(1, listJson.GetProperty("total").GetInt32());
        Assert.Empty(await scopedDb.LoanRepayments.ToListAsync()); // proves the ambient old-company filter is active
        var detail = Assert.IsType<OkObjectResult>(await controller.GetLoan(own.Id, default));
        var receipts = JsonSerializer.SerializeToElement(detail.Value).GetProperty("repayments");
        Assert.Equal(1, receipts.GetArrayLength()); Assert.Equal("OWNER-HISTORY", receipts[0].GetProperty("Reference").GetString());
        Assert.Equal(50, receipts[0].GetProperty("Amount").GetDecimal());
        Assert.IsType<OkObjectResult>(await controller.GetInstallments(own.Id, default));
        Assert.IsType<NotFoundResult>(await controller.GetLoan(other.Id, default));
        Assert.IsType<NotFoundResult>(await controller.GetInstallments(other.Id, default));
        var result = await new LoanEligibilityService(scopedDb).EvaluateAsync(h.Tid, h.Employee, h.Type, 100, 4, "BankTransfer");
        Assert.Equal(200, result.CommittedAmount);
    }

    [Fact]
    public async Task TeamManagerCannotCreateOthersLoansOrReadSalaryBearingAuditPayloads()
    {
        await using var h = await Fixture.Create();
        var manager = h.Controller("Manager");
        ((ClaimsIdentity)manager.User.Identity!).AddClaim(new Claim("permission", "loans.read"));
        Assert.IsType<ForbidResult>(await manager.CreateLoan(new(h.Employee.PublicId, "", h.Type.Id, 100, 4, null, h.Employee.Id), default));
        var loan = h.Loan("Active", 100);
        var log = new LoanAuditLog { TenantId = h.Tid, LoanId = loan.Id, Action = "LifecycleReviewed", NewValuesJson = "{\"Salary\":12345}" };
        h.Db.AddRange(loan, log); await h.Db.SaveChangesAsync();
        var result = Assert.IsType<OkObjectResult>(await manager.GetLoan(loan.Id, default));
        var json = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal("", json.GetProperty("auditLogs")[0].GetProperty("NewValuesJson").GetString());
        Assert.Contains("12345", (await h.Db.LoanAuditLogs.AsNoTracking().SingleAsync()).NewValuesJson);
        Assert.IsType<ForbidResult>(await manager.GetLoanEligibility(h.Type.Id, 100, 4, employeeIntId: h.Employee.Id));
    }

    [Theory]
    [InlineData("Finance")]
    [InlineData("HR Manager")]
    public async Task OriginalCompanyStaffRetainTransferredDebtButNotOtherDepartmentLoans(string role)
    {
        await using var h = await Fixture.Create();
        var originalCompany = new Company { TenantId = h.Tid, LegalNameEn = "Original lender", DefaultCurrency = "SAR" };
        var sameCompanyOtherDepartment = new Employee { TenantId = h.Tid, CompanyId = originalCompany.Id,
            FullName = "Restricted department", EmployeeCode = "RESTRICTED", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) };
        h.Db.AddRange(originalCompany, sameCompanyOtherDepartment); await h.Db.SaveChangesAsync();
        var transferred = h.Loan("Active", 250); transferred.CompanyId = originalCompany.Id; transferred.TotalRepaid = 50; transferred.OutstandingBalance = 200;
        var restricted = h.Loan("Active", 300); restricted.CompanyId = originalCompany.Id;
        restricted.EmployeeIntId = sameCompanyOtherDepartment.Id; restricted.EmployeeId = sameCompanyOtherDepartment.PublicId;
        h.Db.AddRange(transferred, restricted, new LoanRepayment { TenantId = h.Tid, CompanyId = originalCompany.Id,
            LoanId = transferred.Id, Amount = 50, Reference = "ORIGINAL-LENDER-RECEIPT", PaymentMethod = "Cash", PaidDate = DateOnly.FromDateTime(DateTime.UtcNow) });
        await h.Db.SaveChangesAsync();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim("tenant_id", h.Tid.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("permission", "loans.read"),
            new Claim(ClaimTypes.Role, role), new Claim("entity_access", JsonSerializer.Serialize(new { c = originalCompany.Id, r = role })) }, "test")) };
        await using var db = new ZayraDbContext(h.Options, new HttpContextAccessor { HttpContext = http });
        var controller = new LoansController(db, new EmptyEmployeeScope()) { ControllerContext = new() { HttpContext = http } };
        Assert.False(await db.Employees.AnyAsync(x => x.Id == h.Employee.Id)); // current employee belongs to another company
        var list = Assert.IsType<OkObjectResult>(await controller.ListLoans(null, null));
        var listJson = JsonSerializer.SerializeToElement(list.Value);
        Assert.Equal(1, listJson.GetProperty("total").GetInt32());
        Assert.Equal(transferred.Id, listJson.GetProperty("items")[0].GetProperty("Id").GetGuid());
        var detail = Assert.IsType<OkObjectResult>(await controller.GetLoan(transferred.Id, default));
        Assert.Equal(50, JsonSerializer.SerializeToElement(detail.Value).GetProperty("repayments")[0].GetProperty("Amount").GetDecimal());
        Assert.IsType<ForbidResult>(await controller.GetLoan(restricted.Id, default));
        var report = Assert.IsType<OkObjectResult>(await controller.AuditReport(null, null));
        Assert.Equal(1, JsonSerializer.SerializeToElement(report.Value).GetProperty("TotalLoans").GetInt32());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public DbContextOptions<ZayraDbContext> Options { get; } = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public ZayraDbContext Db { get; }
        public Fixture() => Db = new(Options);
        public Guid Tid { get; } = Guid.NewGuid();
        public Guid EmployeeUserId { get; } = Guid.NewGuid();
        public Employee Employee { get; private set; } = null!;
        public LoanType Type { get; private set; } = null!;
        public static async Task<Fixture> Create()
        {
            var h = new Fixture(); var c = new Company { TenantId = h.Tid, LegalNameEn = "Policy test", DefaultCurrency = "SAR" };
            h.Employee = new() { TenantId = h.Tid, CompanyId = c.Id, UserAccountId = h.EmployeeUserId, FullName = "Policy borrower", EmployeeCode = "P1", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2) };
            h.Type = new() { TenantId = h.Tid, NameEn = "Personal", Code = "P", MaxAmount = 10000, MaxInstallments = 12 };
            h.Db.AddRange(c, h.Employee, h.Type); await h.Db.SaveChangesAsync(); return h;
        }
        public LoanPolicy Policy(decimal maximum) => new() { TenantId = Tid, CompanyId = Employee.CompanyId, LoanTypeId = Type.Id, MaxAmount = maximum, MaxConcurrentLoans = 10 };
        public EmployeeLoan Loan(string status, decimal amount) => new() { TenantId = Tid, CompanyId = Employee.CompanyId, EmployeeIntId = Employee.Id, EmployeeId = Employee.PublicId, LoanTypeId = Type.Id, Status = status, RequestedAmount = amount, ApprovedAmount = amount, RequestedInstallments = 4 };
        public Task<LoanEligibilityAssessment> Assess(decimal amount) => new LoanEligibilityService(Db).EvaluateAsync(Tid, Employee, Type, amount, 4, "BankTransfer");
        public LoansController Controller(string role) => new(Db, new Scope()) { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tenant_id", Tid.ToString()), new Claim(ClaimTypes.NameIdentifier, (role == "Employee" ? EmployeeUserId : Guid.NewGuid()).ToString()), new Claim(ClaimTypes.Role, role) }, "test")) } } };
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    private sealed class Scope : IDataScopeService
    { public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization }); }
    private sealed class EmptyEmployeeScope : IDataScopeService
    { public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(new DataScope { Level = DataScopeLevel.Department, AllowedEmployeeIds = [] }); }
}
