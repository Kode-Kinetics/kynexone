using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The deductions statement's pure rules (R3) and the Art. 92 consent rule on loan requests. The cap is decided by
/// WageDeductionClassification (#177) — these tests prove the statement reuses it rather than forking it.
/// </summary>
public class DeductionStatementTests
{
    private static readonly Dictionary<Guid, DebtFacts> NoDebts = new();

    private static PayrollSlip Slip(decimal gross, decimal deductions = 0m) =>
        new() { Id = Guid.NewGuid(), RunId = Guid.NewGuid(), EmployeeId = 7, GrossSalary = gross, Deductions = deductions, EmployeeCode = "E7" };

    private static PayrollDeduction Line(string code, decimal amount, string source, bool employer = false, string? gl = null) =>
        new() { ComponentCode = code, ComponentName = code, Amount = amount, Source = source, IsEmployerContribution = employer, GlDriverKey = gl, EmployeeId = 7 };

    private static DeductionStatement Build(decimal gross, params PayrollDeduction[] lines) =>
        DeductionStatementBuilder.Build(Slip(gross), 2026, 6, lines, [], [], NoDebts);

    [Fact]
    public void Absence_IsShownButNotCounted_AndGosiIsNotCounted()
    {
        var s = Build(10_000m,
            Line("LOP_DEDUCTION", 2_000m, "Attendance"),
            Line("LEAVE", 1_000m, "Leave"),
            Line("GOSI-ANN-EE", 975m, "Statutory"),
            Line("GOSI-ANN-ER", 1_175m, "Statutory", employer: true));
        Assert.Equal(0m, s.DebtTotal);
        Assert.Equal(3, s.Lines.Count); // the employer's GOSI share is not taken from pay, so it is not on the statement
        Assert.All(s.Lines.Where(l => l.Category == DeductionCategories.Absence), l =>
        {
            Assert.False(l.CountsTowardCap);
            Assert.Equal(DeductionLegalBasis.Absence, l.LegalBasisKey);
        });
        Assert.False(s.Lines.Single(l => l.Category == DeductionCategories.Statutory).CountsTowardCap);
        Assert.Empty(s.Flags);
        Assert.Equal(CapStatuses.Within, DeductionStatementBuilder.CapStatus(s));
    }

    [Fact]
    public void UnknownNegativeAdjustment_IsCounted_AndFlagged_FailClosed()
    {
        var s = Build(10_000m, Line("ADJ_MISC_RECOVERY", 300m, "Adjustment"));
        var line = Assert.Single(s.Lines);
        Assert.True(line.CountsTowardCap);
        Assert.Equal(DeductionCategories.PenaltyOrAdjustment, line.Category);
        Assert.Equal(DeductionLegalBasis.Unclassified, line.LegalBasisKey);
        Assert.Equal(300m, s.DebtTotal);
        Assert.Contains(ReleaseABlockReasons.DeductionUnclassifiedCounted, s.Flags);
    }

    [Theory]
    [InlineData("ADJ_PENALTY", DeductionCategories.PenaltyOrAdjustment, true)]
    [InlineData("ADJ_COURT_ORDER", DeductionCategories.CourtOrder, true)]
    [InlineData("ADJ_PAYROLL_CORRECTION", DeductionCategories.PenaltyOrAdjustment, false)]
    public void KnownAdjustmentTypes_AreClassified_WithoutTheUnknownFlag(string code, string category, bool counted)
    {
        var s = Build(10_000m, Line(code, 300m, "Adjustment"));
        var line = Assert.Single(s.Lines);
        Assert.Equal(category, line.Category);
        Assert.Equal(counted, line.CountsTowardCap);
        Assert.Equal(WageDeductionClassification.IsDebtType("Adjustment", null, code), line.CountsTowardCap);
        Assert.DoesNotContain(ReleaseABlockReasons.DeductionUnclassifiedCounted, s.Flags);
    }

    [Theory]
    [InlineData(4_999, CapStatuses.Near, false)]   // 49.99% of the wage
    [InlineData(5_000, CapStatuses.Near, false)]   // exactly half is allowed
    [InlineData(5_001, CapStatuses.Over, true)]    // 50.01%
    [InlineData(3_999, CapStatuses.Within, false)] // below 80% of the limit
    public void CapHeadroom_AtTheHalfWageBoundary(int debt, string status, bool over)
    {
        var s = Build(10_000m, Line("ADJ_PENALTY", debt, "Adjustment"), Line("GOSI-ANN-EE", 975m, "Statutory"));
        Assert.Equal(5_000m, s.CapLimit);
        Assert.Equal(5_000m - debt, s.Headroom);
        Assert.Equal(status, DeductionStatementBuilder.CapStatus(s));
        Assert.Equal(over, s.Flags.Contains(ReleaseABlockReasons.DeductionsOverHalfWage));
        Assert.Equal(WageDeductionClassification.ExceedsHalfWage(debt, 10_000m), over);
        Assert.Equal(Math.Round(debt / 100m, 2), DeductionStatementBuilder.Percent(s.DebtTotal, s.WageDue));
    }

    [Fact]
    public void DebtAgainstNoWage_IsOverTheLimit()
    {
        var s = Build(0m, Line("ADJ_PENALTY", 10m, "Adjustment"));
        Assert.Contains(ReleaseABlockReasons.DeductionsOverHalfWage, s.Flags);
    }

    [Fact]
    public void AggregateLoanLine_SplitsPerLoan_FromWitnesses()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var debts = new Dictionary<Guid, DebtFacts>
        {
            [a] = new(a, "LN-1", "Personal", 12, 400m, ConsentOnFile: false, IsAdvance: false),
            [b] = new(b, "LN-2", "Housing", 10, 1_500m, ConsentOnFile: true, IsAdvance: false),
        };
        var s = DeductionStatementBuilder.Build(Slip(10_000m), 2026, 6, [Line("LOAN_EMI", 1_900m, "Loan")],
            [new DebtTake(a, 400m, 2_800m), new DebtTake(b, 1_500m, 15_000m)], [], debts);
        Assert.Equal(1_900m, s.Lines.Sum(l => l.Amount));
        var la = s.Lines.Single(l => l.LoanId == a);
        Assert.Equal(2_400m, la.BalanceAfter);
        Assert.Equal(6, la.InstalmentsRemaining); // "6 of 12 left"
        Assert.Equal(4m, la.PercentOfWage);
        var lb = s.Lines.Single(l => l.LoanId == b);
        Assert.Equal(15m, lb.PercentOfWage);
        Assert.True(lb.ConsentOnFile);
        Assert.DoesNotContain(ReleaseABlockReasons.LoanInstalmentOver10PctNoConsent, s.Flags); // 15% WITH consent is lawful
    }

    [Fact]
    public void LoanAbove10PercentWithoutConsent_IsFlagged()
    {
        var a = Guid.NewGuid();
        var debts = new Dictionary<Guid, DebtFacts> { [a] = new(a, "LN-1", "Personal", 6, 1_100m, false, false) };
        var s = DeductionStatementBuilder.Build(Slip(10_000m), 2026, 6, [Line("LOAN_EMI", 1_100m, "Loan")],
            [new DebtTake(a, 1_100m, 6_600m)], [], debts);
        Assert.Contains(ReleaseABlockReasons.LoanInstalmentOver10PctNoConsent, s.Flags);
    }

    [Fact]
    public void SplitThatDoesNotAddUp_StaysOneTotal_AndAssumesNoBalance()
    {
        var a = Guid.NewGuid();
        var s = DeductionStatementBuilder.Build(Slip(10_000m), 2026, 6, [Line("LOAN_EMI", 1_000m, "Loan")],
            [new DebtTake(a, 400m, 4_800m)], [], NoDebts);
        var line = Assert.Single(s.Lines);
        Assert.Null(line.LoanId);
        Assert.Null(line.BalanceAfter);
        Assert.Equal(1_000m, line.Amount);
        Assert.True(line.CountsTowardCap);
        Assert.Contains(ReleaseABlockReasons.DeductionSplitUnreconciled, s.Flags);
    }

    [Fact]
    public void EveryLegalBasisKey_IsTranslated_AndNeverACode()
    {
        // The frontend dictionary is the other half of this contract (unit/releaseA.deductions.spec.ts).
        Assert.Equal(DeductionLegalBasis.All.Length, DeductionLegalBasis.All.Distinct().Count());
        Assert.All(DeductionLegalBasis.All, k => Assert.Matches("^[A-Z].*\\.$", k));
        foreach (var code in new[] { ReleaseABlockReasons.DeductionUnclassifiedCounted, ReleaseABlockReasons.DeductionSplitUnreconciled })
            Assert.Equal(code, ReleaseABlockReasons.Get(code).Code);
    }

    // ── Art. 92: loan instalment above 10% of the wage needs written consent (Release A tenants only) ────────────

    [Fact]
    public void Art92_TenPercentExactlyIsAllowed_AboveNeedsConsent_UnknownWageFailsClosed()
    {
        Assert.False(LoanArt92Check.Evaluate(1_000m, 10_000m, true).RequiresConsent);
        Assert.True(LoanArt92Check.Evaluate(1_000.01m, 10_000m, true).RequiresConsent);
        Assert.True(LoanArt92Check.Evaluate(100m, null, true).RequiresConsent);
        Assert.False(LoanArt92Check.Evaluate(5_000m, 10_000m, false).RequiresConsent); // repaid outside payroll
        Assert.Equal(20m, LoanArt92Check.Evaluate(2_000m, 10_000m, true).Pct);
    }

    [Fact]
    public async Task LoanAbove10Percent_WithoutConsent_IsRefused_WithConsent_IsAccepted()
    {
        await using var h = await LoanHarness.Create(releaseA: true);
        var request = new CreateLoanRequest(h.Employee.PublicId, "", h.Type.Id, 12_000m, 6, null, h.Employee.Id, "PayrollDeduction");

        var refused = Assert.IsType<BadRequestObjectResult>(await h.Hr().CreateLoan(request, default));
        var body = JsonSerializer.SerializeToElement(refused.Value);
        Assert.Equal(ReleaseABlockReasons.LoanInstalmentOver10PctNoConsent, body.GetProperty("error").GetString());
        Assert.Equal(20m, body.GetProperty("art92").GetProperty("pct").GetDecimal());
        Assert.Empty(h.Db.EmployeeLoans);

        // A consent on a colleague's file, or a document of another type, is not this employee's consent.
        var wrongType = h.Document(h.Employee.Id, "Passport");
        var colleagues = h.Document(h.Colleague.Id, RestrictedEmployeeDocumentTypes.LoanDeductionConsent);
        await h.Db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await h.Hr().CreateLoan(request with { ConsentDocumentId = wrongType.Id }, default));
        Assert.IsType<BadRequestObjectResult>(await h.Hr().CreateLoan(request with { ConsentDocumentId = colleagues.Id }, default));

        var consent = h.Document(h.Employee.Id, " loandeductionconsent ");
        await h.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await h.Hr().CreateLoan(request with { ConsentDocumentId = consent.Id }, default));
        var loan = await h.Db.EmployeeLoans.SingleAsync();
        Assert.Equal(consent.Id, loan.ConsentDocumentId);
        Assert.Equal(10_000m, loan.CapBaseWage);
    }

    [Fact]
    public async Task LoanAtOrBelow10Percent_NeedsNoConsent()
    {
        await using var h = await LoanHarness.Create(releaseA: true);
        var request = new CreateLoanRequest(h.Employee.PublicId, "", h.Type.Id, 6_000m, 6, null, h.Employee.Id, "PayrollDeduction");
        Assert.IsType<OkObjectResult>(await h.Hr().CreateLoan(request, default));
        Assert.Null((await h.Db.EmployeeLoans.SingleAsync()).ConsentDocumentId);
    }

    [Fact]
    public async Task WithoutReleaseA_TheLoanRequestBehavesAsBefore()
    {
        await using var h = await LoanHarness.Create(releaseA: false);
        var request = new CreateLoanRequest(h.Employee.PublicId, "", h.Type.Id, 12_000m, 6, null, h.Employee.Id, "PayrollDeduction");
        Assert.IsType<OkObjectResult>(await h.Hr().CreateLoan(request, default));
        var loan = await h.Db.EmployeeLoans.SingleAsync();
        Assert.Null(loan.ConsentDocumentId);
        Assert.Null(loan.CapBaseWage);
    }

    [Fact]
    public async Task Approval_ReChecksTheConsent_OnTheApprovedTerms()
    {
        await using var h = await LoanHarness.Create(releaseA: true);
        // Requested at 10% (no consent needed); the approver shortens it to 3 instalments = 20%.
        Assert.IsType<OkObjectResult>(await h.Hr().CreateLoan(
            new CreateLoanRequest(h.Employee.PublicId, "", h.Type.Id, 6_000m, 6, null, h.Employee.Id, "PayrollDeduction"), default));
        var loan = await h.Db.EmployeeLoans.SingleAsync();
        var step = await h.Db.LoanApprovals.SingleAsync();
        var refused = Assert.IsType<BadRequestObjectResult>(await h.Hr().DecideApproval(loan.Id, step.Id,
            new ApprovalDecisionRequest("Approved", null, 6_000m, 3, null), default));
        Assert.Equal(ReleaseABlockReasons.LoanInstalmentOver10PctNoConsent,
            JsonSerializer.SerializeToElement(refused.Value).GetProperty("error").GetString());
        Assert.Equal("Pending", loan.Status);
    }

    private sealed class LoanHarness : IAsyncDisposable
    {
        public ZayraDbContext Db { get; } = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Tid { get; } = Guid.NewGuid();
        public Employee Employee { get; private set; } = null!;
        public Employee Colleague { get; private set; } = null!;
        public LoanType Type { get; private set; } = null!;
        private IServiceProvider _services = null!;

        public static async Task<LoanHarness> Create(bool releaseA)
        {
            var h = new LoanHarness();
            var c = new Company { TenantId = h.Tid, LegalNameEn = "Masar Facility Services", DefaultCurrency = "SAR" };
            h.Employee = new() { TenantId = h.Tid, CompanyId = c.Id, UserAccountId = Guid.NewGuid(), FullName = "Mohammed Abdelrahman", EmployeeCode = "MS-1", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3) };
            h.Colleague = new() { TenantId = h.Tid, CompanyId = c.Id, UserAccountId = Guid.NewGuid(), FullName = "Omar Haddad", EmployeeCode = "MS-2", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3) };
            h.Type = new() { TenantId = h.Tid, NameEn = "Personal", Code = "P", MaxAmount = 50_000, MaxInstallments = 24 };
            h.Db.AddRange(c, h.Employee, h.Colleague, h.Type);
            await h.Db.SaveChangesAsync();
            h.Db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = h.Tid, EmployeeId = h.Employee.Id, BasicSalary = 7_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
                Currency = "SAR", EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
            });
            h.Db.Add(new LoanPolicy
            {
                TenantId = h.Tid, CompanyId = c.Id, LoanTypeId = h.Type.Id, MaxAmount = 50_000, MaxInstallments = 24, MaxConcurrentLoans = 5,
                AllowedRepaymentMethodsJson = "[\"BankTransfer\",\"PayrollDeduction\"]",
            });
            if (releaseA) h.Db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = h.Tid, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
            await h.Db.SaveChangesAsync();
            h._services = new ServiceCollection()
                .AddSingleton<ITenantModuleService>(new TenantModuleService(h.Db, new MemoryCache(new MemoryCacheOptions())))
                .BuildServiceProvider();
            return h;
        }

        public EmployeeDocument Document(int employeeId, string type)
        {
            var d = new EmployeeDocument { TenantId = Tid, CompanyId = Employee.CompanyId, EmployeeId = employeeId, DocumentType = type, FileName = "consent.pdf" };
            Db.EmployeeDocuments.Add(d);
            return d;
        }

        public LoansController Hr() => new(Db, new OrgScope())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = _services,
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", Tid.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Role, "HR Manager"), new Claim("permission", "loans.write"),
                    }, "test")),
                },
            },
        };

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
