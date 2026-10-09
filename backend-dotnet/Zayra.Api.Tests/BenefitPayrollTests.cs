using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Benefits;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Source authorization and exactly-once preparation, independent of payroll rendering.</summary>
public class BenefitPayrollTests
{
    private static readonly DateOnly Start = new(2026, 10, 1);
    private static readonly DateOnly End = new(2026, 10, 31);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid ComponentId = Guid.NewGuid();

    [Fact]
    public void MonthlyProration_UsesOnlyInclusiveCoveredDays()
    {
        var row = new BenefitEnrollment { EffectiveFrom = new(2026, 10, 16), EffectiveTo = new(2026, 11, 15) };
        var policy = new BenefitPayrollPolicy("SalaryAllowance", 3100m, Prorate: true);
        Assert.Equal(1600m, BenefitPayroll.CalculateDue(row, policy, Start, End));
        row.EffectiveFrom = new(2026, 9, 1);
        row.EffectiveTo = new(2026, 10, 15);
        Assert.Equal(1500m, BenefitPayroll.CalculateDue(row, policy, Start, End));
        Assert.Equal(3100m, BenefitPayroll.CalculateDue(row, policy with { Prorate = false }, Start, End));
    }

    [Theory]
    [InlineData("Waived")]
    [InlineData("Cancelled")]
    [InlineData("Ended")]
    public void NonActiveEnrollment_IsNeverPayable(string status)
    {
        var row = new BenefitEnrollment { Status = status, EffectiveFrom = Start.AddDays(-1) };
        Assert.Equal(0m, BenefitPayroll.CalculateDue(row, new("SalaryAllowance", 100), Start, End));
    }

    [Theory]
    [InlineData("Coverage")]
    [InlineData("Reimbursement")]
    public void NonRecurringDelivery_DoesNotPayEntitlementAmount(string delivery)
    {
        var row = new BenefitEnrollment { EffectiveFrom = Start };
        Assert.Equal(0m, BenefitPayroll.CalculateDue(row, new(delivery, 5000), Start, End));
    }

    [Fact]
    public void AnnualPayment_IsDueOnlyInConfiguredMonthAndCoverageWindow()
    {
        var row = new BenefitEnrollment { EffectiveFrom = Start.AddMonths(-9), EffectiveTo = End };
        var policy = new BenefitPayrollPolicy("SalaryAllowance", 12000, "Annual", 10);
        Assert.Equal(12000m, BenefitPayroll.CalculateDue(row, policy, Start, End));
        Assert.Equal(0m, BenefitPayroll.CalculateDue(row, policy, Start.AddMonths(-1), Start.AddDays(-1)));
        row.EffectiveFrom = End.AddDays(1);
        Assert.Equal(0m, BenefitPayroll.CalculateDue(row, policy, Start, End));
        row.EffectiveFrom = Start.AddYears(-1);
        row.EffectiveTo = Start.AddDays(-1);
        Assert.Equal(0m, BenefitPayroll.CalculateDue(row, policy, Start, End));
    }

    [Fact]
    public void SourceIdentity_FollowsEntitlementChainAndPaymentPeriod()
    {
        var chain = Guid.NewGuid();
        var monthly = BenefitPayroll.SourceId(chain, "Monthly", Start, "SalaryAllowance");
        Assert.Equal(monthly, BenefitPayroll.SourceId(chain, "Monthly", Start.AddDays(15), "SalaryAllowance"));
        Assert.NotEqual(monthly, BenefitPayroll.SourceId(chain, "Monthly", Start.AddMonths(1), "SalaryAllowance"));
        Assert.NotEqual(monthly, BenefitPayroll.SourceId(Guid.NewGuid(), "Monthly", Start, "SalaryAllowance"));
        Assert.NotEqual(monthly, BenefitPayroll.SourceId(chain, "Monthly", Start, "PayrollDeduction"));
        Assert.Equal(BenefitPayroll.SourceId(chain, "Annual", Start, "SalaryAllowance"),
            BenefitPayroll.SourceId(chain, "Annual", Start.AddMonths(1), "SalaryAllowance"));
        Assert.NotEqual(BenefitPayroll.SourceId(chain, "Annual", Start, "SalaryAllowance"),
            BenefitPayroll.SourceId(chain, "Annual", Start.AddYears(1), "SalaryAllowance"));
        Assert.Equal(BenefitPayroll.SourceId(chain, "OneTime", Start, "SalaryAllowance"),
            BenefitPayroll.SourceId(chain, "OneTime", Start.AddYears(5), "SalaryAllowance"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    public void SupplementalEngine_EmitsBenefitWithholdingWithoutRecurringSalaryOrDeductions(int tax)
    {
        var selector = typeof(PayrollController).GetMethod("IsSupplementalPayComponent", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(selector);
        var definitions = PayComponentCatalog.SystemComponentSeeds(Guid.NewGuid())
            .Where(component => Assert.IsType<bool>(selector.Invoke(null, [component]))).ToList();
        var result = PayComponentEngine.Compute(definitions, new PayComponentContext
        {
            Basic = 9000, Gross = 9000, FixedDeduction = 400, LoanEmi = 200,
            TaxDeduction = tax, IncomeTaxRate = 10,
            AdjustmentEarningLines = [new("BENEFIT_SCHOOL", "Approved school receipt", 500, "Adjustment", false)],
        });
        Assert.Equal(500m, Assert.Single(result.Earnings).Amount);
        Assert.DoesNotContain(result.Earnings, line => line.Code == "BASIC");
        Assert.DoesNotContain(result.Deductions, line => line.Code is "FIXED_DEDUCTION" or "LOAN_EMI");
        if (tax == 0) Assert.Empty(result.Deductions);
        else
        {
            var withholding = Assert.Single(result.Deductions);
            Assert.Equal("INCOME_TAX", withholding.Code);
            Assert.Equal((decimal)tax, withholding.Amount);
            Assert.Equal("Tax", withholding.Source);
        }
        Assert.Equal(500m - tax, result.Earnings.Sum(x => x.Amount) - result.Deductions.Sum(x => x.Amount));
    }

    [Fact]
    public async Task Staging_UsesFrozenAmountCurrencyAndTaxComponent_NotLiveCatalogue()
    {
        await using var db = Db();
        var f = await Seed(db);
        f.Plan.Currency = "EUR";
        f.Plan.IsActive = false;
        f.Plan.PaymentPolicyJson = Snapshot(f.Plan.Id, new("SalaryAllowance", 9000), currency: "EUR", taxable: false);
        f.Plan.PolicyVersion = 99;
        await db.SaveChangesAsync();
        var adjustment = Assert.Single(await Stage(db, f));
        var witness = Assert.IsType<BenefitPayrollWitness>(BenefitPayroll.Read(adjustment));
        Assert.Equal(3100m, adjustment.Amount);
        Assert.Equal("USD", witness.Currency);
        Assert.True(witness.Component.IsTaxable);
        Assert.Equal("BEN_ALLOWANCE", adjustment.AdjustmentType);
        Assert.Equal(f.Enrollment.PaymentPolicySnapshotJson, witness.PolicySnapshot);
        Assert.Equal("Approved", adjustment.Status);
        Assert.Single(db.AuditLogs.Where(x => x.Action == "benefits.payroll.prepared"));
        Assert.Empty(db.PayrollEarnings);
        Assert.Empty(db.PayrollDeductions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyOrCoverageEnrollment_DoesNotCreateFinancialRows(bool legacy)
    {
        await using var db = Db();
        var f = await Seed(db, delivery: "Coverage", configure: row =>
        {
            if (legacy) row.PaymentPolicySnapshotJson = "{}";
        });
        Assert.Empty(await Stage(db, f));
        Assert.Empty(db.PayrollAdjustments);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task SupplementalRun_DoesNotPayRecurringBenefit()
    {
        await using var db = Db();
        var f = await Seed(db);
        Assert.Empty(await Stage(db, f, recurring: false));
        Assert.Empty(db.PayrollAdjustments);
    }

    [Theory]
    [InlineData("Waived", 0)]
    [InlineData("Cancelled", 0)]
    [InlineData("Active", 1)]
    [InlineData("Active", -1)]
    public async Task InactiveOrOutOfPeriodEnrollment_IsExcluded(string status, int dateCase)
    {
        await using var db = Db();
        var f = await Seed(db);
        f.Enrollment.Status = status;
        if (dateCase == 1) f.Enrollment.EffectiveFrom = End.AddDays(1);
        if (dateCase == -1) f.Enrollment.EffectiveTo = Start.AddDays(-1);
        await db.SaveChangesAsync();
        Assert.Empty(await Stage(db, f));
        Assert.Empty(db.PayrollAdjustments);
    }

    [Fact]
    public async Task StageSameRunTwice_ReusesExactSourceAndAudit()
    {
        await using var db = Db();
        var f = await Seed(db);
        var first = Assert.Single(await Stage(db, f));
        var second = Assert.Single(await Stage(db, f));
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.SourceSnapshotJson, second.SourceSnapshotJson);
        Assert.Single(db.PayrollAdjustments);
        Assert.Single(db.AuditLogs);
    }

    [Theory]
    [InlineData("Processed")]
    [InlineData("Approved")]
    [InlineData("Locked")]
    [InlineData("Paid")]
    public async Task ConsumedSourceOnLiveRun_CannotPayAgain(string status)
    {
        await using var db = Db();
        var f = await Seed(db);
        var first = Assert.Single(await Stage(db, f));
        first.Status = "Processed";
        f.Run.Status = status;
        var next = Run(f, Start);
        db.PayrollRuns.Add(next);
        await db.SaveChangesAsync();
        Assert.Empty(await Stage(db, f, next));
        Assert.Single(db.PayrollAdjustments);
        Assert.Equal(f.Run.Id, first.PayrollRunId);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Ready")]
    [InlineData("Processed")]
    public async Task UnresolvedReservationOnAnotherRun_FailsWithoutMovingSource(string status)
    {
        await using var db = Db();
        var f = await Seed(db);
        var first = Assert.Single(await Stage(db, f));
        f.Run.Status = status;
        var next = Run(f, Start);
        db.PayrollRuns.Add(next);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BenefitPaymentException>(() => Stage(db, f, next));
        Assert.Equal(f.Run.Id, first.PayrollRunId);
        Assert.Single(db.PayrollAdjustments);
    }

    [Fact]
    public async Task VoidedSource_IsReboundOnceWithoutLosingOriginalWitness()
    {
        await using var db = Db();
        var f = await Seed(db);
        var first = Assert.Single(await Stage(db, f));
        var witness = first.SourceSnapshotJson;
        var id = first.Id;
        f.Run.Status = "Voided";
        var replacement = Run(f, Start);
        replacement.RunType = PayrollRunTypes.Replacement;
        replacement.ParentRunId = f.Run.Id;
        db.PayrollRuns.Add(replacement);
        await db.SaveChangesAsync();
        var rebound = Assert.Single(await Stage(db, f, replacement));
        Assert.Equal(id, rebound.Id);
        Assert.Equal(replacement.Id, rebound.PayrollRunId);
        Assert.Equal(witness, rebound.SourceSnapshotJson);
        Assert.Equal(3100m, rebound.Amount);
        Assert.Single(db.PayrollAdjustments);
        var audit = Assert.Single(db.AuditLogs.Where(x => x.Action == "benefits.payroll.rebound"));
        Assert.Contains(f.Run.Id.ToString(), audit.Metadata);
        Assert.Contains(replacement.Id.ToString(), audit.Metadata);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedSourceCannotBeRepricedOnRetryOrAfterVoid(bool voided)
    {
        await using var db = Db();
        var f = await Seed(db);
        var first = Assert.Single(await Stage(db, f));
        f.Enrollment.EffectiveTo = Start.AddDays(-1);
        var changed = Enrollment(f, new("SalaryAllowance", 5000));
        changed.OriginalEnrollmentId = f.Enrollment.Id;
        changed.EffectiveFrom = Start;
        db.BenefitEnrollments.Add(changed);
        var next = f.Run;
        if (voided)
        {
            f.Run.Status = "Voided";
            next = Run(f, Start);
            db.PayrollRuns.Add(next);
        }
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BenefitPaymentException>(() => Stage(db, f, next));
        Assert.Equal(3100m, first.Amount);
        Assert.Equal(f.Run.Id, first.PayrollRunId);
    }

    [Theory]
    [InlineData(false, 6200)]
    [InlineData(true, 4700)]
    public async Task MidMonthSuccessor_PaysOneChainAmount(bool prorate, int expected)
    {
        await using var db = Db();
        var f = await Seed(db, prorate: prorate);
        f.Enrollment.EffectiveTo = new(2026, 10, 15);
        var successor = Enrollment(f, new("SalaryAllowance", 6200, Prorate: prorate));
        successor.OriginalEnrollmentId = f.Enrollment.Id;
        successor.EffectiveFrom = new(2026, 10, 16);
        db.BenefitEnrollments.Add(successor);
        await db.SaveChangesAsync();
        var row = Assert.Single(await Stage(db, f));
        Assert.Equal((decimal)expected, row.Amount);
        Assert.Equal(BenefitPayroll.SourceId(f.Enrollment.Id, "Monthly", Start, "SalaryAllowance"), row.SourceId);
        Assert.Single(db.PayrollAdjustments);
    }

    [Theory]
    [InlineData("direction")]
    [InlineData("tax")]
    [InlineData("currency")]
    public async Task MidMonthSuccessorCannotReinterpretEarlierDaysFinancialPolicy(string mismatch)
    {
        await using var db = Db();
        var f = await Seed(db, prorate: true);
        f.Enrollment.EffectiveTo = new(2026, 10, 15);
        var successor = Enrollment(f, new(mismatch == "direction" ? "PayrollDeduction" : "SalaryAllowance", 3100, Prorate: true));
        successor.OriginalEnrollmentId = f.Enrollment.Id;
        successor.EffectiveFrom = new(2026, 10, 16);
        if (mismatch != "direction") successor.PaymentPolicySnapshotJson = Snapshot(f.Plan.Id,
            new("SalaryAllowance", 3100, Prorate: true), mismatch == "currency" ? "EUR" : "USD", taxable: mismatch != "tax");
        db.BenefitEnrollments.Add(successor);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BenefitPaymentException>(() => Stage(db, f));
        Assert.Empty(db.PayrollAdjustments);
    }

    [Fact]
    public async Task ThreeDatedVersions_RoundOnceAndDoNotExceedAnExactMonthlyCap()
    {
        await using var db = Db();
        var f = await Seed(db, configure: row =>
        {
            row.EffectiveFrom = Start;
            row.EffectiveTo = Start;
            row.MaximumBenefitAmount = 100;
            row.LimitPeriod = "Monthly";
            row.PaymentPolicySnapshotJson = Snapshot(row.BenefitPlanId, new("SalaryAllowance", 100, Prorate: true));
        });
        var second = Enrollment(f, new("SalaryAllowance", 100, Prorate: true));
        second.OriginalEnrollmentId = f.Enrollment.Id;
        second.EffectiveFrom = Start.AddDays(1);
        second.EffectiveTo = Start.AddDays(1);
        var third = Enrollment(f, new("SalaryAllowance", 100, Prorate: true));
        third.OriginalEnrollmentId = f.Enrollment.Id;
        third.EffectiveFrom = Start.AddDays(2);
        third.EffectiveTo = End;
        third.MaximumBenefitAmount = 100;
        third.LimitPeriod = "Monthly";
        db.AddRange(second, third);
        await db.SaveChangesAsync();
        Assert.Equal(100m, Assert.Single(await Stage(db, f)).Amount);
    }

    [Theory]
    [InlineData("Coverage", false)]
    [InlineData("SalaryAllowance", true)]
    [InlineData("PayrollDeduction", true)]
    [InlineData("Reimbursement", true)]
    public async Task CanonicalAuthority_NeverExecutesLegacyPaymentTerms(string delivery, bool needsReconciliation)
    {
        await using var db = Db();
        var f = await Seed(db, delivery: delivery);
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = f.Tenant, FeatureKey = "release_a", IsEnabled = true });
        await db.SaveChangesAsync();
        if (needsReconciliation) await Assert.ThrowsAsync<BenefitPaymentException>(() => Stage(db, f));
        else Assert.Empty(await Stage(db, f));
        Assert.Empty(db.PayrollAdjustments);
    }

    [Fact]
    public async Task ExplicitPayrollDeduction_IsNegativeWithFrozenDeductionComponent()
    {
        await using var db = Db();
        var f = await Seed(db, delivery: "PayrollDeduction");
        var row = Assert.Single(await Stage(db, f));
        Assert.Equal(-3100m, row.Amount);
        Assert.Equal("Deduction", BenefitPayroll.Read(row)!.Component.ComponentType);
    }

    [Fact]
    public async Task TenantCompanyAndSelectedEmployeeBoundariesExcludeOtherEntitlements()
    {
        await using var db = Db();
        var f = await Seed(db);
        var otherTenant = Enrollment(f, new("SalaryAllowance", 1000));
        otherTenant.TenantId = Guid.NewGuid();
        var otherCompany = Enrollment(f, new("SalaryAllowance", 2000));
        otherCompany.CompanyId = Guid.NewGuid();
        var otherEmployee = Enrollment(f, new("SalaryAllowance", 4000));
        otherEmployee.EmployeeId = f.Employee.Id + 1000;
        db.AddRange(otherTenant, otherCompany, otherEmployee);
        await db.SaveChangesAsync();
        Assert.Equal(3100m, Assert.Single(await Stage(db, f)).Amount);
        Assert.Single(db.PayrollAdjustments);
    }

    [Theory]
    [InlineData("currency")]
    [InlineData("plan")]
    [InlineData("component")]
    public async Task InvalidFrozenPaymentAuthority_FailsClosed(string mismatch)
    {
        await using var db = Db();
        var f = await Seed(db, configure: row => row.PaymentPolicySnapshotJson = Snapshot(
            mismatch == "plan" ? Guid.NewGuid() : row.BenefitPlanId, new("SalaryAllowance", 3100),
            mismatch == "currency" ? "EUR" : "USD", componentType: mismatch == "component" ? "Deduction" : "Earning"));
        await Assert.ThrowsAsync<BenefitPaymentException>(() => Stage(db, f));
        Assert.Empty(db.PayrollAdjustments);
    }

    [Fact]
    public async Task AnnualLimit_IncludesPriorPreparedBenefitAndBlocksOverflow()
    {
        await using var db = Db();
        var f = await Seed(db);
        f.Enrollment.MaximumBenefitAmount = 5000;
        f.Enrollment.LimitPeriod = "Annual";
        await db.SaveChangesAsync();
        var first = Assert.Single(await Stage(db, f));
        first.Status = "Processed";
        f.Run.Status = "Paid";
        var november = Run(f, Start.AddMonths(1));
        db.PayrollRuns.Add(november);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BenefitPaymentException>(() => Stage(db, f, november));
        Assert.Single(db.PayrollAdjustments);
    }

    [Theory]
    [InlineData("PerEnrollment", false)]
    [InlineData("Lifetime", true)]
    public async Task NewIndependentEnrollment_ResetsOnlyPerEnrollmentLimit(string limitPeriod, bool blocked)
    {
        await using var db = Db();
        var f = await Seed(db, configure: row =>
        {
            row.MaximumBenefitAmount = 4000;
            row.LimitPeriod = limitPeriod;
            row.EffectiveTo = End;
        });
        var prior = Assert.Single(await Stage(db, f));
        prior.Status = "Processed";
        f.Run.Status = "Paid";
        var newEnrollment = Enrollment(f, new("SalaryAllowance", 3100));
        newEnrollment.EffectiveFrom = End.AddDays(1);
        newEnrollment.MaximumBenefitAmount = 4000;
        newEnrollment.LimitPeriod = limitPeriod;
        var november = Run(f, End.AddDays(1));
        db.AddRange(newEnrollment, november);
        await db.SaveChangesAsync();
        if (blocked) await Assert.ThrowsAsync<BenefitPaymentException>(() => Stage(db, f, november));
        else Assert.Equal(3100m, Assert.Single(await Stage(db, f, november)).Amount);
    }

    [Fact]
    public async Task AgreedPolicySnapshotCannotBeOverwrittenOnExistingEnrollment()
    {
        await using var db = Db();
        var f = await Seed(db);
        f.Enrollment.PaymentPolicySnapshotJson = Snapshot(f.Plan.Id, new("SalaryAllowance", 9999));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("source")]
    [InlineData("delete")]
    public async Task PreparedPayrollAuthorityCannotBeEditedOrDeleted(string edit)
    {
        await using var db = Db();
        var f = await Seed(db);
        var row = Assert.Single(await Stage(db, f));
        if (edit == "amount") row.Amount = 9999;
        if (edit == "source") row.SourceType = "Manual";
        if (edit == "delete") db.PayrollAdjustments.Remove(row);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task GovernedEditCannotAffectAnAlreadyPreparedPayrollPeriod()
    {
        await using var db = Db();
        var f = await Seed(db);
        await Stage(db, f);
        await Assert.ThrowsAsync<BenefitPaymentException>(() => BenefitPayroll.EnsureMutableAsync(db, f.Tenant,
            f.Enrollment.Id, End, default));
        await BenefitPayroll.EnsureMutableAsync(db, f.Tenant, f.Enrollment.Id, End.AddDays(1), default);
        f.Run.Status = "Voided";
        await db.SaveChangesAsync();
        await BenefitPayroll.EnsureMutableAsync(db, f.Tenant, f.Enrollment.Id, Start, default);
    }

    [Fact]
    public async Task OneTimePayment_IsNotRepeatedInFollowingMonth()
    {
        await using var db = Db();
        var f = await Seed(db, frequency: "OneTime");
        var first = Assert.Single(await Stage(db, f));
        first.Status = "Processed";
        f.Run.Status = "Paid";
        var november = Run(f, Start.AddMonths(1));
        db.PayrollRuns.Add(november);
        await db.SaveChangesAsync();
        Assert.Empty(await Stage(db, f, november));
        Assert.Single(db.PayrollAdjustments);
    }

    [Theory]
    [InlineData("Pending", 0)]
    [InlineData("Rejected", 0)]
    [InlineData("Cancelled", 0)]
    [InlineData("Approved", 1)]
    public async Task ClaimPayment_RequiresFinalApprovalAndCanUseSupplementalRun(string status, int count)
    {
        await using var db = Db();
        var f = await Seed(db, delivery: "Reimbursement");
        var claim = Claim(f, status);
        db.ApprovalRequests.Add(claim);
        await db.SaveChangesAsync();
        var staged = await Stage(db, f, recurring: false);
        Assert.Equal(count, staged.Count);
        if (count == 1)
        {
            Assert.Equal(500m, staged[0].Amount);
            Assert.Equal(claim.Id, staged[0].SourceId);
            Assert.Equal(BenefitPayroll.ClaimSource, staged[0].SourceType);
            Assert.Equal("Approved", claim.Status); // Payroll preparation does not rewrite approval authority.
        }
    }

    [Fact]
    public async Task ClaimApprovedAfterRunPeriod_IsNotPaidRetroactively()
    {
        await using var db = Db();
        var f = await Seed(db, delivery: "Reimbursement");
        var claim = Claim(f, "Approved");
        claim.CompletedAtUtc = End.AddDays(1).ToDateTime(TimeOnly.MinValue);
        db.ApprovalRequests.Add(claim);
        await db.SaveChangesAsync();
        Assert.Empty(await Stage(db, f, recurring: false));
    }

    [Fact]
    public async Task ApprovedClaimCannotBePaidAgainByAnotherLiveRun()
    {
        await using var db = Db();
        var f = await Seed(db, delivery: "Reimbursement");
        db.ApprovalRequests.Add(Claim(f, "Approved"));
        await db.SaveChangesAsync();
        var first = Assert.Single(await Stage(db, f, recurring: false));
        first.Status = "Processed";
        f.Run.Status = "Paid";
        var november = Run(f, Start.AddMonths(1));
        db.PayrollRuns.Add(november);
        await db.SaveChangesAsync();
        Assert.Empty(await Stage(db, f, november, recurring: false));
        Assert.Single(db.PayrollAdjustments);
    }

    private static Task<List<PayrollAdjustment>> Stage(ZayraDbContext db, Fixture f, PayrollRun? run = null, bool recurring = true)
    {
        var target = run ?? f.Run;
        var start = new DateOnly(target.Year, target.Month, 1);
        return BenefitPayroll.StageAsync(db, target, f.Company.Id, "USD", [f.Employee], recurring, start,
            start.AddMonths(1).AddDays(-1), default);
    }

    private static string Snapshot(Guid planId, BenefitPayrollPolicy policy, string currency = "USD", bool taxable = true,
        string? componentType = null) => JsonSerializer.Serialize(new BenefitPayrollPolicyEnvelope(1, planId, currency, policy,
            new(ComponentId, "BEN_ALLOWANCE", "Company benefit", componentType ?? (policy.Delivery == "PayrollDeduction" ? "Deduction" : "Earning"), taxable)), Json);

    private static BenefitEnrollment Enrollment(Fixture f, BenefitPayrollPolicy policy) => new()
    {
        TenantId = f.Tenant, CompanyId = f.Company.Id, EmployeeId = f.Employee.Id, EmployeeName = f.Employee.FullName,
        BenefitPlanId = f.Plan.Id, AssignmentSource = "GradeDefault", EffectiveFrom = Start.AddMonths(-1),
        MaximumBenefitAmount = 50000, LimitPeriod = "Annual", PaymentPolicySnapshotJson = Snapshot(f.Plan.Id, policy),
    };

    private static PayrollRun Run(Fixture f, DateOnly start) => new()
    {
        TenantId = f.Tenant, CompanyId = f.Company.Id, Year = start.Year, Month = start.Month, Status = "Draft",
    };

    private static ApprovalRequest Claim(Fixture f, string status)
    {
        var approval = new ApprovalRequest
        {
            TenantId = f.Tenant, CompanyId = f.Company.Id, EntityName = BenefitClaims.EntityName,
            RequestedForEmployeeId = f.Employee.Id, Status = status, CompletedAtUtc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc),
        };
        approval.EntityId = approval.Id.ToString();
        var p = new BenefitClaimProposal(1, approval.Id, f.Tenant, f.Company.Id, null, f.Employee.Id, f.Employee.FullName,
            f.Enrollment.Id, f.Enrollment.Id, f.Plan.Id, f.Plan.Name, "USD", 500, new(2026, 10, 10), "SYNTHETIC-INVOICE", "School receipt",
            "Annual", 50000, new(2026, 1, 1), new(2026, 12, 31), f.Enrollment.PaymentPolicySnapshotJson,
            f.Enrollment.UpdatedAtUtc, [], "synthetic-test-route");
        approval.Payload = JsonSerializer.Serialize(p, Json);
        approval.PayloadSha256 = AdditionalBenefitGrants.Digest(approval.Payload);
        return approval;
    }

    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static async Task<Fixture> Seed(ZayraDbContext db, bool prorate = false, string delivery = "SalaryAllowance",
        string frequency = "Monthly", Action<BenefitEnrollment>? configure = null)
    {
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Synthetic benefit payroll", CountryCode = "US", IsActive = true };
        var employee = new Employee { TenantId = tenant, CompanyId = company.Id, EmployeeCode = "BP1", FullName = "Synthetic Benefit Worker",
            JoiningDate = Start.AddYears(-1).ToDateTime(TimeOnly.MinValue), Status = "Active" };
        var plan = new BenefitPlan { TenantId = tenant, CompanyId = company.Id, Code = "BP", Name = "Company benefit", Currency = "USD", EffectiveFrom = Start.AddYears(-1) };
        db.AddRange(company, employee, plan);
        await db.SaveChangesAsync();
        var run = new PayrollRun { TenantId = tenant, CompanyId = company.Id, Year = Start.Year, Month = Start.Month, Status = "Draft" };
        var provisional = new Fixture(tenant, company, employee, plan, new(), run);
        var enrollment = Enrollment(provisional, new(delivery, 3100, frequency, Prorate: prorate));
        configure?.Invoke(enrollment);
        var f = provisional with { Enrollment = enrollment };
        db.AddRange(enrollment, run);
        await db.SaveChangesAsync();
        return f;
    }
    private sealed record Fixture(Guid Tenant, Company Company, Employee Employee, BenefitPlan Plan, BenefitEnrollment Enrollment, PayrollRun Run);
}
