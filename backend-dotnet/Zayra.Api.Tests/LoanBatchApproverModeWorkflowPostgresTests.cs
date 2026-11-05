using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The batch-approver setting end to end on real PostgreSQL with the production provider options and interceptors:
/// an Admin publishes the policy through the endpoint, the batch response says whether self-approval is open, the
/// creator self-approves with a reason (advisory locks and the policy join run on Postgres), the batch is paid through
/// to an active loan with its journal and schedule, and an HR Manager can tighten the setting but not reopen it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class LoanBatchApproverModeWorkflowPostgresTests(PostgresFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed record Seed(Guid Tid, Guid CompanyId, Guid TypeId, Employee Employee, Guid Maker, Guid Checker, Guid Admin);

    [Fact]
    public async Task SingleOperator_CreatorSelfApprovesWithReason_ThenPaysThroughToAnActiveLoan()
    {
        var s = await SeedAsync();
        await PublishAsync(s, s.Admin, "Admin", "SingleOperator", 500m, expectOk: true);
        var loanId = await ApprovedLoanAsync(s, 100m);

        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await As(db, s, s.Maker).CreatePaymentBatch(new(new[] { loanId }), Ct));
        Guid batchId;
        await using (var db = fixture.CreateDb())
            batchId = (await db.LoanDisbursementBatches.AsNoTracking().SingleAsync(x => x.TenantId == s.Tid)).Id;

        Assert.True(await SelfApprovalAllowedAsync(s, batchId), "the GET response must open self-approval under a SingleOperator policy");

        await using (var db = fixture.CreateDb())
            Assert.IsType<BadRequestObjectResult>(await As(db, s, s.Maker).ApprovePaymentBatch(batchId, Ct, new ApproveLoanBatchRequest("short")));
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await As(db, s, s.Maker).ApprovePaymentBatch(batchId, Ct, new ApproveLoanBatchRequest("Sole finance officer this month")));

        await using (var db = fixture.CreateDb())
        {
            var batch = await db.LoanDisbursementBatches.AsNoTracking().SingleAsync(x => x.Id == batchId);
            Assert.Equal("Approved", batch.Status);
            Assert.Equal(s.Maker, batch.ApprovedBy);
            var self = await db.LoanAuditLogs.AsNoTracking().SingleAsync(x => x.TenantId == s.Tid && x.Action == "PaymentBatchSelfApproved");
            Assert.Equal(loanId, self.LoanId);
            Assert.Contains("Sole finance officer this month", self.NewValuesJson);
        }
        Assert.False(await SelfApprovalAllowedAsync(s, batchId), "only a draft offers self-approval");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await As(db, s, s.Maker).ConfirmPaymentBatchPaid(batchId, new($"SELF-{Guid.NewGuid():N}"[..20], today), Ct));

        await using (var db = fixture.CreateDb())
        {
            var loan = await db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == loanId);
            Assert.Equal("Active", loan.Status);
            Assert.Equal(100m, loan.OutstandingBalance);
            Assert.Equal(3, await db.LoanInstallments.CountAsync(x => x.LoanId == loanId));
            Assert.NotEmpty(await db.FinanceGlEntries.Where(x => x.TenantId == s.Tid && x.SourceEntityId == loanId).ToListAsync());
        }
    }

    [Fact]
    public async Task CapAndRetightening_AreEnforcedOnPostgres_AndHrCannotReopen()
    {
        var s = await SeedAsync();
        await PublishAsync(s, s.Admin, "Admin", "SingleOperator", 50m, expectOk: true);
        var loanId = await ApprovedLoanAsync(s, 100m);
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await As(db, s, s.Maker).CreatePaymentBatch(new(new[] { loanId }), Ct));
        Guid batchId;
        await using (var db = fixture.CreateDb())
            batchId = (await db.LoanDisbursementBatches.AsNoTracking().SingleAsync(x => x.TenantId == s.Tid)).Id;

        Assert.False(await SelfApprovalAllowedAsync(s, batchId), "a batch above the cap is not offered for self-approval");
        await using (var db = fixture.CreateDb())
            Assert.IsType<BadRequestObjectResult>(await As(db, s, s.Maker).ApprovePaymentBatch(batchId, Ct, new ApproveLoanBatchRequest("Above the cap but trying")));

        // HR may tighten back to strict, but may not raise the cap or reopen single-operator.
        await PublishAsync(s, s.Checker, "HR Manager", "SingleOperator", 1000m, expectOk: false);
        await PublishAsync(s, s.Checker, "HR Manager", "SeparateApprover", 0m, expectOk: true);
        await PublishAsync(s, s.Checker, "HR Manager", "SingleOperator", 0m, expectOk: false);
        await using (var db = fixture.CreateDb())
            Assert.Equal("SeparateApprover", (await db.LoanPolicies.AsNoTracking().SingleAsync(x => x.TenantId == s.Tid && x.IsActive)).BatchApproverMode);

        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await As(db, s, s.Checker).ApprovePaymentBatch(batchId, Ct));
    }

    [Fact]
    public async Task DatabaseRefusesAnInvalidSettingEvenWhenTheApiIsBypassed()
    {
        var s = await SeedAsync();
        await PublishAsync(s, s.Admin, "Admin", "SeparateApprover", 0m, expectOk: true);
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
                 {
                     "UPDATE loan_policies SET batch_approver_mode = 'Anyone' WHERE tenant_id = @t",
                     "UPDATE loan_policies SET self_approval_max_amount = 10 WHERE tenant_id = @t",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("t", s.Tid);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal("23514", ex.SqlState);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<Seed> SeedAsync()
    {
        await using var db = fixture.CreateDb();
        var tid = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tid, LegalNameEn = "Approver Mode Co", CountryCode = "SAU", DefaultCurrency = "SAR", Jurisdiction = "KSA-mainland", IsActive = true };
        var employee = new Employee
        {
            TenantId = tid, CompanyId = company.Id, EmployeeCode = $"AM-{Guid.NewGuid():N}"[..12], FullName = "Mode Borrower", EnglishName = "Mode Borrower",
            Status = EmployeeStatuses.Active, BankName = "Mode Bank", BankIban = "SA4420000001234567891234", JoiningDate = DateTime.UtcNow.AddYears(-2),
            UserAccountId = Guid.NewGuid(),
        };
        var type = new LoanType { TenantId = tid, Code = $"AM{Guid.NewGuid():N}"[..10], NameEn = "Mode", MaxAmount = 10_000m, MaxInstallments = 12, RequiresApproval = true, IsActive = true };
        db.AddRange(company, employee, type);
        await db.SaveChangesAsync();
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tid, EmployeeId = employee.Id, Iban = "SA4420000001234567891234", BankName = "Mode Bank", SalaryCurrency = "SAR" });
        await db.SaveChangesAsync();
        return new Seed(tid, company.Id, type.Id, employee, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    }

    private async Task PublishAsync(Seed s, Guid user, string role, string mode, decimal cap, bool expectOk)
    {
        await using var db = fixture.CreateDb();
        var result = await As(db, s, user, role).CreateLoanPolicy(new LoanPolicyRequest(s.CompanyId, s.TypeId, "Mode policy", MaxAmount: 5000m,
            AllowedEmploymentStatuses: ["Active"], AllowedRepaymentMethods: ["BankTransfer"], BatchApproverMode: mode, SelfApprovalMaxAmount: cap), Ct);
        if (expectOk) Assert.IsType<OkObjectResult>(result);
        else Assert.IsNotType<OkObjectResult>(result);
    }

    private async Task<Guid> ApprovedLoanAsync(Seed s, decimal amount)
    {
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await As(db, s, s.Maker).CreateLoan(new CreateLoanRequest(s.Employee.PublicId, s.Employee.FullName, s.TypeId, amount, 3, null, s.Employee.Id), Ct));
        await using var read = fixture.CreateDb();
        var loan = await read.EmployeeLoans.AsNoTracking().SingleAsync(x => x.TenantId == s.Tid);
        var step = await read.LoanApprovals.AsNoTracking().SingleAsync(x => x.LoanId == loan.Id);
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await As(db, s, s.Checker).DecideApproval(loan.Id, step.Id, new ApprovalDecisionRequest("Approved", "ok", null, null, null), Ct));
        return loan.Id;
    }

    private async Task<bool> SelfApprovalAllowedAsync(Seed s, Guid batchId)
    {
        await using var db = fixture.CreateDb();
        var ok = Assert.IsType<OkObjectResult>(await As(db, s, s.Maker).GetPaymentBatch(batchId, Ct));
        return JsonSerializer.SerializeToElement(ok.Value).GetProperty("SelfApprovalAllowed").GetBoolean();
    }

    private static LoansController As(ZayraDbContext db, Seed s, Guid user, params string[] roles) => new(db, new Scope())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("tenant_id", s.Tid.ToString()), new Claim(ClaimTypes.NameIdentifier, user.ToString()),
                            new Claim("permission", "loans.read"), new Claim(ClaimTypes.Name, "Tester") }
                        .Concat((roles.Length == 0 ? new[] { "Finance", "HR Manager" } : roles).Select(r => new Claim(ClaimTypes.Role, r))), "test")),
            },
        },
    };

    private sealed class Scope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
