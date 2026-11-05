using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A company decides whether a loan payment batch needs a second approver. The default stays strict (the creator cannot
/// approve their own batch). A company may opt into SingleOperator, optionally capped by amount, but only an Admin can
/// move the setting, a self-approval needs a written reason and is audited, and a borrower can never approve a batch that
/// pays their own loan whatever the setting says.
/// </summary>
public class LoanBatchApproverModeTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed record Fx(ZayraDbContext Db, Guid TenantId, Guid BorrowerUser, Guid MakerUser, Guid CheckerUser, Employee Employee, Guid CompanyId, LoanType Type);

    private static async Task<Fx> SeedAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var borrowerUser = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Loan Co", DefaultCurrency = "SAR", CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true };
        var employee = new Employee
        {
            TenantId = tenant, CompanyId = company.Id, EmployeeCode = "LOAN-1", FullName = "Borrower", EnglishName = "Borrower",
            Status = EmployeeStatuses.Active, BankName = "Test Bank", BankIban = "SA4420000001234567891234",
            JoiningDate = DateTime.UtcNow.AddYears(-2), UserAccountId = borrowerUser,
        };
        var type = new LoanType { TenantId = tenant, Code = "PERSONAL", NameEn = "Personal", MaxAmount = 10_000m, MaxInstallments = 12, RequiresApproval = true, IsActive = true };
        db.AddRange(company, employee, type);
        await db.SaveChangesAsync();
        return new Fx(db, tenant, borrowerUser, Guid.NewGuid(), Guid.NewGuid(), employee, company.Id, type);
    }

    private static LoansController As(Fx f, Guid user, params string[] roles) => new(f.Db, new Scope())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("tenant_id", f.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, user.ToString()),
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

    private static async Task<EmployeeLoan> ApprovedLoanAsync(Fx f, decimal amount = 100m)
    {
        Assert.IsType<OkObjectResult>(await As(f, f.MakerUser).CreateLoan(new CreateLoanRequest(f.Employee.PublicId,
            f.Employee.FullName, f.Type.Id, amount, 3, null, f.Employee.Id), Ct));
        var loan = await f.Db.EmployeeLoans.SingleAsync();
        var step = await f.Db.LoanApprovals.SingleAsync(x => x.LoanId == loan.Id);
        Assert.IsType<OkObjectResult>(await As(f, f.CheckerUser).DecideApproval(loan.Id, step.Id,
            new ApprovalDecisionRequest("Approved", "ok", null, null, null), Ct));
        return loan;
    }

    private static async Task<LoanPolicy> PolicyAsync(Fx f, string mode, decimal cap = 0m, bool active = true)
    {
        var policy = new LoanPolicy
        {
            TenantId = f.TenantId, CompanyId = f.CompanyId, LoanTypeId = f.Type.Id, Version = 1, PolicyName = "Approver mode",
            IsActive = active, BatchApproverMode = mode, SelfApprovalMaxAmount = cap,
        };
        f.Db.Add(policy);
        await f.Db.SaveChangesAsync();
        return policy;
    }

    private static async Task<Guid> DraftBatchByMakerAsync(Fx f, EmployeeLoan loan)
    {
        Assert.IsType<OkObjectResult>(await As(f, f.MakerUser).CreatePaymentBatch(new CreateLoanPaymentBatchRequest(new[] { loan.Id }), Ct));
        return (await f.Db.Set<LoanDisbursementBatch>().SingleAsync()).Id;
    }

    private static Task<string> StatusAsync(Fx f, Guid batchId) =>
        f.Db.Set<LoanDisbursementBatch>().AsNoTracking().Where(x => x.Id == batchId).Select(x => x.Status).SingleAsync();

    [Fact]
    public async Task NoPolicy_CreatorStillCannotApproveOwnBatch()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        var batchId = await DraftBatchByMakerAsync(f, await ApprovedLoanAsync(f));
        Assert.IsType<BadRequestObjectResult>(await As(f, f.MakerUser).ApprovePaymentBatch(batchId, Ct, new ApproveLoanBatchRequest("I am the only finance user")));
        Assert.Equal("Draft", await StatusAsync(f, batchId));
    }

    [Fact]
    public async Task SeparateApproverPolicy_CreatorCannotApproveOwnBatch()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f); await PolicyAsync(f, "SeparateApprover");
        var batchId = await DraftBatchByMakerAsync(f, loan);
        Assert.IsType<BadRequestObjectResult>(await As(f, f.MakerUser).ApprovePaymentBatch(batchId, Ct, new ApproveLoanBatchRequest("reason")));
        Assert.Equal("Draft", await StatusAsync(f, batchId));
    }

    [Fact]
    public async Task SingleOperatorPolicy_CreatorMayApproveWithAReason_AndItIsAudited()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f); await PolicyAsync(f, "SingleOperator");
        var batchId = await DraftBatchByMakerAsync(f, loan);
        Assert.IsType<OkObjectResult>(await As(f, f.MakerUser).ApprovePaymentBatch(batchId, Ct, new ApproveLoanBatchRequest("Only one finance officer in this company")));
        Assert.Equal("Approved", await StatusAsync(f, batchId));
        var audit = await f.Db.LoanAuditLogs.AsNoTracking().SingleAsync(x => x.Action == "PaymentBatchSelfApproved");
        Assert.Equal(loan.Id, audit.LoanId);
        Assert.Contains("Only one finance officer", audit.NewValuesJson);
        Assert.Equal(f.MakerUser, audit.PerformedBy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ok")]
    public async Task SingleOperatorPolicy_SelfApprovalWithoutARealReasonIsRefused(string? reason)
    {
        var f = await SeedAsync(); await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f); await PolicyAsync(f, "SingleOperator");
        var batchId = await DraftBatchByMakerAsync(f, loan);
        var request = reason is null ? null : new ApproveLoanBatchRequest(reason);
        Assert.IsType<BadRequestObjectResult>(await As(f, f.MakerUser).ApprovePaymentBatch(batchId, Ct, request));
        Assert.Equal("Draft", await StatusAsync(f, batchId));
    }

    [Fact]
    public async Task SingleOperatorPolicy_ADifferentApproverNeedsNoReasonAndIsNotLoggedAsSelfApproval()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f); await PolicyAsync(f, "SingleOperator");
        var batchId = await DraftBatchByMakerAsync(f, loan);
        Assert.IsType<OkObjectResult>(await As(f, f.CheckerUser).ApprovePaymentBatch(batchId, Ct));
        Assert.Equal("Approved", await StatusAsync(f, batchId));
        Assert.False(await f.Db.LoanAuditLogs.AnyAsync(x => x.Action == "PaymentBatchSelfApproved"));
    }

    [Fact]
    public async Task SingleOperatorWithCap_BatchAboveTheCapNeedsASeparateApprover()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f, 100m); await PolicyAsync(f, "SingleOperator", cap: 50m);
        var batchId = await DraftBatchByMakerAsync(f, loan);
        Assert.IsType<BadRequestObjectResult>(await As(f, f.MakerUser).ApprovePaymentBatch(batchId, Ct, new ApproveLoanBatchRequest("Within my authority")));
        Assert.Equal("Draft", await StatusAsync(f, batchId));
        Assert.IsType<OkObjectResult>(await As(f, f.CheckerUser).ApprovePaymentBatch(batchId, Ct));
    }

    [Fact]
    public async Task SingleOperatorWithCap_BatchAtOrBelowTheCapMayBeSelfApproved()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f, 100m); await PolicyAsync(f, "SingleOperator", cap: 100m);
        var batchId = await DraftBatchByMakerAsync(f, loan);
        Assert.IsType<OkObjectResult>(await As(f, f.MakerUser).ApprovePaymentBatch(batchId, Ct, new ApproveLoanBatchRequest("Within my authority")));
    }

    [Fact]
    public async Task SingleOperatorPolicy_BorrowerStillCannotApproveABatchPayingTheirOwnLoan()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f); await PolicyAsync(f, "SingleOperator");
        var batchId = await DraftBatchByMakerAsync(f, loan);
        Assert.IsType<BadRequestObjectResult>(await As(f, f.BorrowerUser).ApprovePaymentBatch(batchId, Ct, new ApproveLoanBatchRequest("It is my loan but I am allowed")));
        Assert.Equal("Draft", await StatusAsync(f, batchId));
    }

    [Fact]
    public async Task InactivePolicyVersion_DoesNotRelaxTheControl()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f); await PolicyAsync(f, "SingleOperator", active: false);
        var batchId = await DraftBatchByMakerAsync(f, loan);
        Assert.IsType<BadRequestObjectResult>(await As(f, f.MakerUser).ApprovePaymentBatch(batchId, Ct, new ApproveLoanBatchRequest("A retired policy said so")));
    }

    [Fact]
    public async Task OtherCompanysPolicy_DoesNotRelaxTheControl()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f);
        var other = await PolicyAsync(f, "SingleOperator");
        other.CompanyId = Guid.NewGuid(); await f.Db.SaveChangesAsync();
        var batchId = await DraftBatchByMakerAsync(f, loan);
        Assert.IsType<BadRequestObjectResult>(await As(f, f.MakerUser).ApprovePaymentBatch(batchId, Ct, new ApproveLoanBatchRequest("Another company's setting")));
    }

    [Fact]
    public async Task BatchWithoutACompany_NeverMatchesAGroupPolicy()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f);
        var group = await PolicyAsync(f, "SingleOperator");
        group.CompanyId = null; await f.Db.SaveChangesAsync();
        var batchId = await DraftBatchByMakerAsync(f, loan);
        var batch = await f.Db.Set<LoanDisbursementBatch>().SingleAsync(x => x.Id == batchId);
        batch.CompanyId = null; await f.Db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await As(f, f.MakerUser).ApprovePaymentBatch(batchId, Ct, new ApproveLoanBatchRequest("Legacy batch with no company")));
    }

    [Fact]
    public async Task CapIsRefusedWhenTheBatchIsNotInTheCompanyCurrency()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f, 100m); await PolicyAsync(f, "SingleOperator", cap: 5000m);
        var batchId = await DraftBatchByMakerAsync(f, loan);
        var batch = await f.Db.Set<LoanDisbursementBatch>().SingleAsync(x => x.Id == batchId);
        batch.Currency = "USD"; await f.Db.SaveChangesAsync();   // the cap of 5,000 is in SAR
        Assert.IsType<BadRequestObjectResult>(await As(f, f.MakerUser).ApprovePaymentBatch(batchId, Ct, new ApproveLoanBatchRequest("Within a SAR cap, but paid in USD")));
        Assert.Equal("Draft", await StatusAsync(f, batchId));
    }

    [Fact]
    public async Task CreatorOnlySeesTheFlag_AndOthersAreNotChargedTheQuery()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f); await PolicyAsync(f, "SingleOperator");
        var batchId = await DraftBatchByMakerAsync(f, loan);
        static bool Flag(IActionResult r) => System.Text.Json.JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(r).Value).GetProperty("SelfApprovalAllowed").GetBoolean();
        Assert.True(Flag(await As(f, f.MakerUser).GetPaymentBatch(batchId, Ct)));
        Assert.False(Flag(await As(f, f.CheckerUser).GetPaymentBatch(batchId, Ct)));
    }

    [Fact]
    public async Task ChangingTheModeOrCapIsAudited_AndAnUnchangedPublishIsNot()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        Assert.IsType<OkObjectResult>(await As(f, f.MakerUser, "Admin").CreateLoanPolicy(Request(f, "SingleOperator", cap: 2500m), Ct));
        Assert.IsType<OkObjectResult>(await As(f, f.MakerUser, "Admin").CreateLoanPolicy(Request(f, "SingleOperator", cap: 2500m, maxAmount: 900m), Ct));
        Assert.IsType<OkObjectResult>(await As(f, f.MakerUser, "HR Manager").CreateLoanPolicy(Request(f, "SeparateApprover"), Ct));
        var audits = await f.Db.AuditLogs.AsNoTracking().Where(x => x.Action == "loans.policy.batch_approver_changed").OrderBy(x => x.CreatedAtUtc).ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.Contains("SingleOperator", audits[0].Metadata);
        Assert.Contains("2500", audits[0].Metadata);
        Assert.Contains("\"to\":\"SeparateApprover\"", audits[1].Metadata!.Replace(" ", ""));
        Assert.All(audits, a => Assert.Equal(f.CompanyId, a.CompanyId));
    }

    [Fact]
    public async Task OfferingSwitchNeverCopiesAGroupPolicysRelaxedModeIntoACompany()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        f.Db.Add(new LoanPolicy { TenantId = f.TenantId, CompanyId = null, LoanTypeId = f.Type.Id, Version = 1, PolicyName = "Group", IsActive = true,
            BatchApproverMode = "SingleOperator", SelfApprovalMaxAmount = 0, MaxInstallments = 12 });
        await f.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await As(f, f.MakerUser, "HR Manager").SetLoanTypeOffering(new SetLoanOfferingRequest(f.CompanyId, f.Type.Id, false), Ct));
        var stub = await f.Db.Set<LoanPolicy>().AsNoTracking().SingleAsync(x => x.CompanyId == f.CompanyId && x.IsActive);
        Assert.Equal("SeparateApprover", stub.BatchApproverMode);
        Assert.Equal(0m, stub.SelfApprovalMaxAmount);
    }

    // ── who may change the setting ──────────────────────────────────────────────────────────────────────────

    private LoanPolicyRequest Request(Fx f, string mode = "SeparateApprover", decimal cap = 0m, decimal maxAmount = 500m) =>
        new(f.CompanyId, f.Type.Id, "Personal policy", MaxAmount: maxAmount, AllowedEmploymentStatuses: ["Active"],
            AllowedRepaymentMethods: ["BankTransfer"], BatchApproverMode: mode, SelfApprovalMaxAmount: cap);

    [Fact]
    public async Task HrManagerCannotRelaxTheApproverMode()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        Assert.IsType<ForbidResult>(await As(f, f.MakerUser, "HR Manager").CreateLoanPolicy(Request(f, "SingleOperator"), Ct));
        Assert.Empty(await f.Db.Set<LoanPolicy>().ToListAsync());
    }

    [Fact]
    public async Task AdminCanSetTheApproverModeAndCap()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        Assert.IsType<OkObjectResult>(await As(f, f.MakerUser, "Admin").CreateLoanPolicy(Request(f, "SingleOperator", cap: 2500m), Ct));
        var saved = await f.Db.Set<LoanPolicy>().SingleAsync();
        Assert.Equal("SingleOperator", saved.BatchApproverMode);
        Assert.Equal(2500m, saved.SelfApprovalMaxAmount);
    }

    [Fact]
    public async Task HrManagerMayEditOtherTermsWhileKeepingTheCurrentMode()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        Assert.IsType<OkObjectResult>(await As(f, f.MakerUser, "Admin").CreateLoanPolicy(Request(f, "SingleOperator", cap: 2500m), Ct));
        Assert.IsType<OkObjectResult>(await As(f, f.MakerUser, "HR Manager").CreateLoanPolicy(Request(f, "SingleOperator", cap: 2500m, maxAmount: 900m), Ct));
        var active = await f.Db.Set<LoanPolicy>().SingleAsync(x => x.IsActive);
        Assert.Equal("SingleOperator", active.BatchApproverMode);
        Assert.Equal(900m, active.MaxAmount);
    }

    [Fact]
    public async Task HrManagerCannotChangeTheCapEither()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        Assert.IsType<OkObjectResult>(await As(f, f.MakerUser, "Admin").CreateLoanPolicy(Request(f, "SingleOperator", cap: 2500m), Ct));
        Assert.IsType<ForbidResult>(await As(f, f.MakerUser, "HR Manager").CreateLoanPolicy(Request(f, "SingleOperator", cap: 999999m), Ct));
    }

    [Fact]
    public async Task HrManagerMayTightenBackToSeparateApprover()
    {
        var f = await SeedAsync(); await using var db = f.Db;
        Assert.IsType<OkObjectResult>(await As(f, f.MakerUser, "Admin").CreateLoanPolicy(Request(f, "SingleOperator"), Ct));
        Assert.IsType<OkObjectResult>(await As(f, f.MakerUser, "HR Manager").CreateLoanPolicy(Request(f, "SeparateApprover"), Ct));
        Assert.Equal("SeparateApprover", (await f.Db.Set<LoanPolicy>().SingleAsync(x => x.IsActive)).BatchApproverMode);
    }

    [Theory]
    [InlineData("Nobody", 0)]
    [InlineData("", 0)]
    [InlineData("SingleOperator", -1)]
    [InlineData("SingleOperator", 1000000000000)]
    [InlineData("SingleOperator", 10.005)]
    [InlineData("SeparateApprover", 100)]
    public async Task InvalidModeOrCapIsRejected(string mode, double cap)
    {
        var f = await SeedAsync(); await using var db = f.Db;
        Assert.IsType<BadRequestObjectResult>(await As(f, f.MakerUser, "Admin").CreateLoanPolicy(Request(f, mode, (decimal)cap), Ct));
    }
}
