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
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class LoanLifecycleTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("{\"Decision\":{},\"CollectionStatus\":\"OnHold\"}")]
    [InlineData("{\"Decision\":\"Hold\",\"CollectionStatus\":42}")]
    public async Task LegacyConversion_MalformedHoldEvidenceFailsClosed(string json)
    {
        await using var h = await Harness.Create();
        h.Loan.PolicyId = null; h.Loan.PolicySnapshotJson = "{}";
        h.Loan.RepaymentMethod = "PayrollDeduction"; h.Loan.CollectionStatus = "OnHold";
        h.Db.LoanAuditLogs.Add(new LoanAuditLog { TenantId = h.Tid, LoanId = h.Loan.Id,
            Action = "LifecycleReviewed", PerformedBy = Guid.NewGuid(), NewValuesJson = json });
        await h.Db.SaveChangesAsync();
        var request = new LoanChangeRequestInput("CollectionMethod", "Reconcile legacy evidence",
            RepaymentMethod: "BankTransfer", ReconciliationReference: "SHAPE-CHECK", ConfirmNoPayrollCollection: true);
        Assert.IsType<ConflictObjectResult>(await h.Controller("Finance").RequestLoanChange(h.Loan.Id, request, default));
        Assert.Empty(await h.Db.LoanChangeRequests.ToListAsync());
    }

    [Fact]
    public async Task LegacyConversion_RejectsPreviouslyDecidedReconciliationReference()
    {
        await using var h = await Harness.Create();
        h.Db.LoanChangeRequests.Add(new LoanChangeRequest { TenantId = h.Tid, CompanyId = h.Loan.CompanyId,
            LoanId = h.Loan.Id, ChangeType = "CollectionMethod", Status = "Rejected", Reference = "RECON-USED",
            Reason = "Earlier evidence rejected", CreatedBy = Guid.NewGuid() });
        await h.Db.SaveChangesAsync();
        var request = new LoanChangeRequestInput("CollectionMethod", "Retry historical evidence",
            RepaymentMethod: "BankTransfer", ReconciliationReference: " RECON-USED ", ConfirmNoPayrollCollection: true);
        var result = Assert.IsType<ConflictObjectResult>(await h.Controller("Finance").RequestLoanChange(h.Loan.Id, request, default));
        Assert.Contains("already recorded", Assert.IsType<string>(result.Value));
        Assert.Single(await h.Db.LoanChangeRequests.ToListAsync());
    }

    [Fact]
    public async Task LegacyManualCollection_RequiresReconciliationAndIndependentApproval_PreservesMoneyAndHold()
    {
        await using var h = await Harness.Create();
        h.Loan.PolicyId = null; h.Loan.PolicySnapshotJson = "{}";
        h.Loan.RepaymentMethod = "PayrollDeduction"; h.Loan.CollectionStatus = "OnHold";
        h.Db.FinanceGlEntries.Add(new FinanceGlEntry { TenantId = h.Tid, CompanyId = h.Loan.CompanyId,
            SourceModule = "Loan", SourceEntityId = h.Loan.Id, EventType = "Disbursement", Amount = h.Loan.ApprovedAmount,
            Currency = "SAR", EntryDate = h.Loan.DisbursementDate!.Value, DebitAccount = "Original receivable", CreditAccount = "Original bank" });
        h.Db.FinanceGlEntries.Add(new FinanceGlEntry { TenantId = h.Tid, CompanyId = h.Loan.CompanyId,
            SourceModule = "Loan", SourceEntityId = h.Loan.Id, EventType = "Repayment", Amount = h.Loan.TotalRepaid,
            Currency = "SAR", EntryDate = h.Today, DebitAccount = "Original bank", CreditAccount = "Original receivable" });
        await h.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").ReviewLoanLifecycle(h.Loan.Id,
            new LoanLifecycleReviewRequest("Hold", "Hold for legacy collection reconciliation"), default));
        var maker = Guid.NewGuid();
        var request = new LoanChangeRequestInput("CollectionMethod", "Legacy bank receipts reconciled against employee statement",
            RepaymentMethod: "BankTransfer", ReconciliationReference: "RECON-2026-001", ConfirmNoPayrollCollection: true);
        Assert.IsType<OkObjectResult>(await h.Controller("Finance", maker).RequestLoanChange(h.Loan.Id, request, default));
        var change = await h.Db.LoanChangeRequests.SingleAsync();
        var before = (h.Loan.ApprovedAmount, h.Loan.TotalRepaid, h.Loan.OutstandingBalance);
        var decision = new LoanChangeDecisionRequest("Approved", "Bank history independently verified");
        Assert.IsType<BadRequestObjectResult>(await h.Controller("Finance", maker).DecideLoanChange(h.Loan.Id, change.Id, decision, default));
        Assert.IsType<OkObjectResult>(await h.Controller("Finance").DecideLoanChange(h.Loan.Id, change.Id, decision, default));
        Assert.Equal("BankTransfer", h.Loan.RepaymentMethod);
        Assert.Equal("OnHold", h.Loan.CollectionStatus);
        Assert.Equal(before, (h.Loan.ApprovedAmount, h.Loan.TotalRepaid, h.Loan.OutstandingBalance));
        Assert.Equal(2, await h.Db.FinanceGlEntries.CountAsync());
    }

    [Theory]
    [InlineData("missing_receipt")]
    [InlineData("wrong_receivable")]
    [InlineData("wrong_currency")]
    [InlineData("automatic_hold_only")]
    public async Task LegacyConversion_RefusesMissingAccountingOrExplicitHrHold(string defect)
    {
        await using var h = await Harness.Create();
        h.Loan.PolicyId = null; h.Loan.PolicySnapshotJson = "{}";
        h.Loan.RepaymentMethod = "PayrollDeduction"; h.Loan.CollectionStatus = "OnHold";
        h.Db.FinanceGlEntries.Add(new FinanceGlEntry { TenantId = h.Tid, CompanyId = h.Loan.CompanyId,
            SourceModule = "Loan", SourceEntityId = h.Loan.Id, EventType = "Disbursement", Amount = h.Loan.ApprovedAmount,
            Currency = "SAR", EntryDate = h.Loan.DisbursementDate!.Value, DebitAccount = "Original receivable", CreditAccount = "Original bank" });
        if (defect != "missing_receipt")
            h.Db.FinanceGlEntries.Add(new FinanceGlEntry { TenantId = h.Tid, CompanyId = h.Loan.CompanyId,
                SourceModule = "Loan", SourceEntityId = h.Loan.Id, EventType = "Repayment", Amount = h.Loan.TotalRepaid,
                Currency = defect == "wrong_currency" ? "USD" : "SAR", EntryDate = h.Today, DebitAccount = "Original bank",
                CreditAccount = defect == "wrong_receivable" ? "Wrong account" : "Original receivable" });
        await h.Db.SaveChangesAsync();
        if (defect != "automatic_hold_only")
            Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").ReviewLoanLifecycle(h.Loan.Id,
                new LoanLifecycleReviewRequest("Hold", "Hold for legacy collection reconciliation"), default));
        var request = new LoanChangeRequestInput("CollectionMethod", "Reconciled historical statement",
            RepaymentMethod: "BankTransfer", ReconciliationReference: "AUDIT-RECON", ConfirmNoPayrollCollection: true);
        Assert.IsType<ConflictObjectResult>(await h.Controller("Finance").RequestLoanChange(h.Loan.Id, request, default));
        Assert.Equal("PayrollDeduction", h.Loan.RepaymentMethod);
        Assert.Empty(await h.Db.LoanChangeRequests.ToListAsync());
    }

    [Fact]
    public async Task LegacyCollectionConversion_RejectsUnheldLoan_AndAnyPayrollHistory()
    {
        await using var h = await Harness.Create();
        h.Loan.PolicyId = null; h.Loan.PolicySnapshotJson = "{}"; h.Loan.RepaymentMethod = "PayrollDeduction";
        await h.Db.SaveChangesAsync();
        var request = new LoanChangeRequestInput("CollectionMethod", "Reconciled legacy bank history",
            RepaymentMethod: "BankTransfer", ReconciliationReference: "RECON-002", ConfirmNoPayrollCollection: true);
        Assert.IsType<ConflictObjectResult>(await h.Controller("Finance").RequestLoanChange(h.Loan.Id, request, default));
        h.Loan.CollectionStatus = "OnHold";
        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").ReviewLoanLifecycle(h.Loan.Id,
            new LoanLifecycleReviewRequest("Hold", "Hold pending reconciliation"), default));
        h.Db.PayrollRunConsumptions.Add(new PayrollRunConsumption { TenantId = h.Tid, CompanyId = h.Loan.CompanyId,
            PayrollRunId = Guid.NewGuid(), ArtifactType = PayrollConsumptionArtifacts.Loan, ArtifactId = h.Loan.Id, Amount = 10m });
        await h.Db.SaveChangesAsync();
        var blocked = Assert.IsType<ConflictObjectResult>(await h.Controller("Finance").RequestLoanChange(h.Loan.Id, request, default));
        Assert.Contains("Payroll collection evidence", Assert.IsType<string>(blocked.Value));
        Assert.Equal("PayrollDeduction", h.Loan.RepaymentMethod);
        Assert.Empty(await h.Db.LoanChangeRequests.ToListAsync());
    }

    [Fact]
    public async Task LegacyConversion_SeesPriorForeignCompanyPayrollWitness_EvenWhenScopedToLender()
    {
        await using var h = await Harness.Create();
        h.Loan.PolicyId = null; h.Loan.PolicySnapshotJson = "{}"; h.Loan.RepaymentMethod = "PayrollDeduction";
        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").ReviewLoanLifecycle(h.Loan.Id,
            new LoanLifecycleReviewRequest("Hold", "Hold pending reconciliation"), default));
        h.Db.PayrollRunConsumptions.Add(new PayrollRunConsumption { TenantId = h.Tid, CompanyId = Guid.NewGuid(),
            PayrollRunId = Guid.NewGuid(), ArtifactType = PayrollConsumptionArtifacts.Loan, ArtifactId = h.Loan.Id, Amount = 10m });
        await h.Db.SaveChangesAsync();
        var controller = h.Controller("Finance");
        ((ClaimsIdentity)controller.User.Identity!).AddClaim(new Claim("entity_scope", JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { h.Loan.CompanyId } })));
        await using var db = new ZayraDbContext(h.Options, new HttpContextAccessor { HttpContext = controller.HttpContext });
        Assert.Empty(await db.PayrollRunConsumptions.ToListAsync());
        var scoped = new LoansController(db, new Scope()) { ControllerContext = controller.ControllerContext };
        var request = new LoanChangeRequestInput("CollectionMethod", "History reconciliation request",
            RepaymentMethod: "BankTransfer", ReconciliationReference: "FOREIGN-PAYROLL", ConfirmNoPayrollCollection: true);
        var result = Assert.IsType<ConflictObjectResult>(await scoped.RequestLoanChange(h.Loan.Id, request, default));
        Assert.Contains("Payroll collection evidence", Assert.IsType<string>(result.Value));
    }

    [Fact]
    public async Task EligibilitySnapshots_AtCreationAndApproval_UseRestrictedFinancialAssessment()
    {
        await using var h = await Harness.Create(paid: false);
        h.Loan.Status = "Cancelled"; // Existing harness loan must not reserve eligibility exposure.
        h.Employee.BankName = "SECRET-ASSESSMENT-BANK";
        h.Employee.BankIban = "SA4420000001234567891234";
        h.Employee.WpsBankDetails = "SECRET-ASSESSMENT-WPS";
        h.Employee.IdNumber = "SECRET-ASSESSMENT-ID";
        h.Employee.PassportNumber = "SECRET-ASSESSMENT-PASSPORT";
        h.Employee.IqamaNumber = "SECRET-ASSESSMENT-IQAMA";
        h.Employee.MedicalInformation = "SECRET-ASSESSMENT-MEDICAL";
        h.Employee.DisciplinaryRecords = "SECRET-ASSESSMENT-DISCIPLINARY";
        h.Employee.TerminationReason = "SECRET-ASSESSMENT-TERMINATION";
        h.Db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = h.Tid, EmployeeId = h.Employee.Id, BasicSalary = 10_000m,
            Currency = "SAR", EffectiveDate = h.Today.AddYears(-1), IsActive = true
        });
        await h.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").CreateLoan(
            new CreateLoanRequest(h.Employee.PublicId, h.Employee.FullName, h.Loan.LoanTypeId, 100m, 3, null, h.Employee.Id), default));
        var loan = await h.Db.EmployeeLoans.SingleAsync(x => x.Id != h.Loan.Id);
        AssertProjection(loan.EligibilitySnapshotJson);
        var approval = await h.Db.LoanApprovals.SingleAsync(x => x.LoanId == loan.Id);
        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").DecideApproval(loan.Id, approval.Id,
            new ApprovalDecisionRequest("Approved", "Independent review", 90m, 3, h.Today.AddMonths(1)), default));
        await h.Db.Entry(loan).ReloadAsync();
        Assert.Equal("Approved", loan.Status);
        AssertProjection(loan.EligibilitySnapshotJson);

        static void AssertProjection(string json)
        {
            using var document = JsonDocument.Parse(json);
            // An explicit schema, not reflection over the record, catches accidental Employee
            // navigation properties and future sensitive additions to the assessment itself.
            var allowed = new[] { "Eligible", "Reasons", "Codes", "MaxAvailableAmount", "PolicyId", "PolicyVersion",
                "PolicySnapshotJson", "EmploymentSnapshotJson", "MonthlySalary", "CommittedAmount",
                // Slice L1: the explainable-limit fields. Same sensitivity as MonthlySalary (salary-derived caps),
                // no personnel identifiers — the grade block carries grade id/code/name and the cell's figures only.
                "GradeLimit", "Available", "BindingLimit", "Limits",
                // Release A R3: the Art. 92 10% test (instalment, wage, share). Same sensitivity as MonthlySalary.
                "Art92" };
            Assert.Equal(allowed.OrderBy(x => x), document.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
            Assert.True(document.RootElement.GetProperty("Eligible").GetBoolean());
            Assert.Equal(10_000m, document.RootElement.GetProperty("MonthlySalary").GetDecimal());
            Assert.Equal(0m, document.RootElement.GetProperty("CommittedAmount").GetDecimal());
            Assert.Equal("{}", document.RootElement.GetProperty("EmploymentSnapshotJson").GetString());
            Assert.DoesNotContain("SECRET-ASSESSMENT-", json);
            Assert.DoesNotContain("SA4420000001234567891234", json);
            Assert.DoesNotContain("BankIban", json);
            Assert.DoesNotContain("MedicalInformation", json);
        }
    }

    [Fact]
    public async Task LoanFinancialSnapshot_UsesExplicitFieldAllowlist_AndExcludesUnrelatedEmployeeSecrets()
    {
        await using var h = await Harness.Create();
        h.Employee.BankName = "PRIVATE-BANK-NAME";
        h.Employee.BankIban = "SA4420000001234567891234";
        h.Employee.WpsBankDetails = "PRIVATE-WPS-ACCOUNT";
        h.Employee.IdNumber = "PRIVATE-NATIONAL-ID";
        h.Employee.PassportNumber = "PRIVATE-PASSPORT";
        h.Employee.IqamaNumber = "PRIVATE-IQAMA";
        h.Employee.MedicalInformation = "PRIVATE-MEDICAL-DIAGNOSIS";
        h.Employee.DisciplinaryRecords = "PRIVATE-DISCIPLINARY-RECORD";
        h.Employee.TerminationReason = "PRIVATE-TERMINATION-NARRATIVE";
        await h.Db.SaveChangesAsync();
        var service = new LoanLifecycleService(h.Db);
        await service.RefreshAsync(h.Tid, h.Loan, default);
        await h.Db.SaveChangesAsync();
        using (var snapshot = JsonDocument.Parse(h.Loan.EmploymentSnapshotJson))
        {
            // Explicit allowlist, deliberately not generated by reflection from the DTO: adding a
            // sensitive DTO property must fail this test until the projection is reviewed.
            var allowed = new[]
            {
                "EmployeeStatus", "CompanyId", "GradeId", "DesignationId", "Salary", "ContractType",
                "ContractStartDate", "ContractEndDate", "ProbationStartDate", "ProbationEndDate", "ConfirmationDate",
                "JoiningDate", "OnProbation", "ContractExpired", "OffboardingId", "OffboardingStatus", "SeparationType",
                "LastWorkingDay", "UnpaidLeaveIds", "Grade", "Designation", "JobTitle", "SalaryCurrency",
                "HasActiveSeparation", "LatestOffboardingId", "LatestOffboardingStatus"
            };
            Assert.Equal(allowed.OrderBy(x => x), snapshot.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
            Assert.Equal(10_000m, snapshot.RootElement.GetProperty("Salary").GetDecimal());
            Assert.Equal(h.Loan.CompanyId, snapshot.RootElement.GetProperty("CompanyId").GetGuid());
        }
        h.Employee.Salary = 12_345.67m;
        await h.Db.SaveChangesAsync();
        await service.RefreshAsync(h.Tid, h.Loan, default);
        await h.Db.SaveChangesAsync();
        Assert.True(h.Loan.ReviewRequired);
        Assert.Contains("Salary changed", h.Loan.ReviewReason);
        Assert.Equal(12_345.67m, LoanLifecycleService.ReadSnapshot(h.Loan.EmploymentSnapshotJson)!.Salary);
        var storedPayloads = h.Loan.EmploymentSnapshotJson + string.Join("\n", (await h.Db.LoanAuditLogs.ToListAsync()).Select(x => x.OldValuesJson + x.NewValuesJson));
        foreach (var secret in new[] { "PRIVATE-BANK-NAME", "SA4420000001234567891234", "PRIVATE-WPS-ACCOUNT", "PRIVATE-NATIONAL-ID", "PRIVATE-PASSPORT", "PRIVATE-IQAMA", "PRIVATE-MEDICAL-DIAGNOSIS", "PRIVATE-DISCIPLINARY-RECORD", "PRIVATE-TERMINATION-NARRATIVE" })
            Assert.DoesNotContain(secret, storedPayloads);
    }

    [Fact]
    public async Task PromotionAndSalaryChange_RequireReview_WithoutChangingDebtOrSchedule()
    {
        await using var h = await Harness.Create();
        var service = new LoanLifecycleService(h.Db);
        await service.RefreshAsync(h.Tid, h.Loan, default);
        Assert.False(h.Loan.ReviewRequired);
        h.Employee.GradeId = Guid.NewGuid(); h.Employee.DesignationId = Guid.NewGuid(); h.Employee.Salary = 12_000m;
        await h.Db.SaveChangesAsync();
        var before = JsonSerializer.Serialize(await h.Db.LoanInstallments.OrderBy(x => x.InstallmentNumber).ToListAsync());
        await service.RefreshAsync(h.Tid, h.Loan, default);
        await h.Db.SaveChangesAsync();
        Assert.True(h.Loan.ReviewRequired);
        Assert.Contains("Grade or designation", h.Loan.ReviewReason);
        Assert.Contains("Salary changed", h.Loan.ReviewReason);
        Assert.Equal(100m, h.Loan.ApprovedAmount);
        Assert.Equal(90m, h.Loan.OutstandingBalance);
        Assert.Equal(10m, h.Loan.TotalRepaid);
        Assert.Equal(before, JsonSerializer.Serialize(await h.Db.LoanInstallments.OrderBy(x => x.InstallmentNumber).ToListAsync()));
        Assert.Single(await h.Db.LoanAuditLogs.Where(x => x.Action == "EmploymentReviewRequired").ToListAsync());
        await service.RefreshAsync(h.Tid, h.Loan, default);
        await h.Db.SaveChangesAsync();
        Assert.Single(await h.Db.LoanAuditLogs.Where(x => x.Action == "EmploymentReviewRequired").ToListAsync());
    }

    [Fact]
    public async Task InitialNoticeIsDetected_AndWithdrawalOrRehireDoesNotAutomaticallyClearReview()
    {
        await using var h = await Harness.Create();
        var notice = new EmployeeOffboarding { TenantId = h.Tid, EmployeeId = h.Employee.Id, Status = "InProgress", SeparationType = "Resignation", LastWorkingDay = h.Today.AddMonths(1) };
        h.Db.EmployeeOffboardings.Add(notice);
        await h.Db.SaveChangesAsync();
        var service = new LoanLifecycleService(h.Db);
        await service.RefreshAsync(h.Tid, h.Loan, default);
        Assert.True(h.Loan.ReviewRequired);
        notice.Status = "Cancelled"; h.Employee.JoiningDate = DateTime.UtcNow;
        await h.Db.SaveChangesAsync();
        await service.RefreshAsync(h.Tid, h.Loan, default);
        await h.Db.SaveChangesAsync();
        Assert.True(h.Loan.ReviewRequired);
        Assert.Contains("withdrawal", h.Loan.ReviewReason);
        Assert.Contains("rehire", h.Loan.ReviewReason);
        Assert.Equal(90m, h.Loan.OutstandingBalance);
    }

    [Fact]
    public async Task StringGradeAndSalaryCurrencyChanges_RequireReviewWithoutChangingApprovedAmount()
    {
        await using var h = await Harness.Create();
        var salary = new EmployeeSalaryStructure { TenantId = h.Tid, EmployeeId = h.Employee.Id, BasicSalary = 10_000m, Currency = "SAR", EffectiveDate = h.Today.AddYears(-1) };
        h.Db.EmployeeSalaryStructures.Add(salary);
        await h.Db.SaveChangesAsync();
        var service = new LoanLifecycleService(h.Db);
        await service.RefreshAsync(h.Tid, h.Loan, default);
        h.Employee.Grade = "Senior"; h.Employee.Designation = "Lead"; h.Employee.JobTitle = "Team lead"; salary.Currency = "AED";
        await h.Db.SaveChangesAsync();
        await service.RefreshAsync(h.Tid, h.Loan, default);
        Assert.True(h.Loan.ReviewRequired);
        Assert.Contains("Grade or designation", h.Loan.ReviewReason);
        Assert.Contains("Salary changed", h.Loan.ReviewReason);
        Assert.Equal("SAR", h.Loan.Currency);
        Assert.Equal(100m, h.Loan.ApprovedAmount);
    }

    [Fact]
    public async Task RecentCancelledSeparation_DoesNotHideOlderActiveNotice()
    {
        await using var h = await Harness.Create(paid: false);
        h.Db.EmployeeOffboardings.AddRange(
            new EmployeeOffboarding { TenantId = h.Tid, EmployeeId = h.Employee.Id, Status = "InProgress", SeparationType = "Resignation", LastWorkingDay = h.Today.AddMonths(1), CreatedAtUtc = DateTime.UtcNow.AddDays(-3) },
            new EmployeeOffboarding { TenantId = h.Tid, EmployeeId = h.Employee.Id, Status = "Cancelled", SeparationType = "Resignation", LastWorkingDay = h.Today.AddMonths(1), CreatedAtUtc = DateTime.UtcNow });
        await h.Db.SaveChangesAsync();
        await new LoanLifecycleService(h.Db).RefreshAsync(h.Tid, h.Loan, default);
        Assert.True(h.Loan.ReviewRequired);
        Assert.True(LoanLifecycleService.ReadSnapshot(h.Loan.EmploymentSnapshotJson)!.HasActiveSeparation);
        Assert.IsType<ConflictObjectResult>(await h.Controller("HR Director").ReviewLoanLifecycle(h.Loan.Id, new LoanLifecycleReviewRequest("Continue", "Attempt to ignore older notice"), default));
    }

    [Fact]
    public async Task OverdueTracking_IsAuditedOnce_AndHoldSuppressesCollectionReminder()
    {
        await using var h = await Harness.Create();
        h.Loan.CollectionStatus = "OnHold";
        var first = await h.Db.LoanInstallments.SingleAsync(x => x.InstallmentNumber == 1);
        first.DueDate = h.Today.AddDays(-1);
        await h.Db.SaveChangesAsync();
        var service = new LoanLifecycleService(h.Db);
        await service.RefreshAsync(h.Tid, h.Loan, default);
        await h.Db.SaveChangesAsync();
        Assert.Equal("Overdue", h.Loan.Status);
        Assert.Single(await h.Db.LoanAuditLogs.Where(x => x.Action == "LoanBecameOverdue").ToListAsync());
        Assert.Empty(await h.Db.Notifications.ToListAsync());
        await service.RefreshAsync(h.Tid, h.Loan, default);
        await h.Db.SaveChangesAsync();
        Assert.Single(await h.Db.LoanAuditLogs.Where(x => x.Action == "LoanBecameOverdue").ToListAsync());
        first.AmountPaid = first.AmountDue;
        await h.Db.SaveChangesAsync();
        await service.RefreshAsync(h.Tid, h.Loan, default);
        await h.Db.SaveChangesAsync();
        Assert.Equal("Active", h.Loan.Status);
        Assert.Equal("OnHold", h.Loan.CollectionStatus);
        Assert.Single(await h.Db.LoanAuditLogs.Where(x => x.Action == "LoanBecameCurrent").ToListAsync());
    }

    [Theory]
    [InlineData("Inactive")]
    [InlineData("Suspended")]
    [InlineData("Terminated")]
    public async Task InitialInactiveEmployment_IsFlaggedWithoutCancellingDebt(string status)
    {
        await using var h = await Harness.Create();
        h.Employee.Status = status;
        await h.Db.SaveChangesAsync();
        await new LoanLifecycleService(h.Db).RefreshAsync(h.Tid, h.Loan, default);
        Assert.True(h.Loan.ReviewRequired);
        Assert.Equal("Active", h.Loan.Status);
        Assert.Equal(90m, h.Loan.OutstandingBalance);
    }

    [Fact]
    public async Task InitialCompanyTransfer_PreservesLenderAndRequiresReview()
    {
        await using var h = await Harness.Create();
        var originalCompany = h.Loan.CompanyId;
        h.Employee.CompanyId = Guid.NewGuid();
        await h.Db.SaveChangesAsync();
        await new LoanLifecycleService(h.Db).RefreshAsync(h.Tid, h.Loan, default);
        Assert.True(h.Loan.ReviewRequired);
        Assert.Contains("legal entity", h.Loan.ReviewReason);
        Assert.Equal(originalCompany, h.Loan.CompanyId);
        Assert.Equal(90m, h.Loan.OutstandingBalance);
    }

    [Fact]
    public async Task SystemRefresh_AfterCompanyTransfer_DoesNotCopyNewEmployerCompensationIntoLenderSnapshotOrAudit()
    {
        await using var h = await Harness.Create();
        var service = new LoanLifecycleService(h.Db);
        await service.RefreshAsync(h.Tid, h.Loan, default);
        await h.Db.SaveChangesAsync();
        var originalLender = h.Loan.CompanyId;
        var employer = new Company { TenantId = h.Tid, LegalNameEn = "New employer", CountryCode = "USA", DefaultCurrency = "USD" };
        h.Db.Companies.Add(employer);
        h.Employee.CompanyId = employer.Id;
        h.Employee.Salary = 987654.32m;
        h.Employee.ContractType = "FOREIGN-CONTRACT-PRIVATE";
        h.Employee.Grade = "FOREIGN-GRADE-PRIVATE";
        h.Employee.Designation = "FOREIGN-DESIGNATION-PRIVATE";
        h.Employee.ContractEndDate = h.Today.AddYears(2);
        await h.Db.SaveChangesAsync();

        // This context has no HTTP company filter, matching the trusted lifecycle worker.
        await service.RefreshAsync(h.Tid, h.Loan, default);
        await h.Db.SaveChangesAsync();
        var snapshot = LoanLifecycleService.ReadSnapshot(h.Loan.EmploymentSnapshotJson)!;
        Assert.Equal(employer.Id, snapshot.CompanyId);
        Assert.Equal(0m, snapshot.Salary);
        Assert.Equal(string.Empty, snapshot.SalaryCurrency);
        Assert.Equal(string.Empty, snapshot.ContractType);
        Assert.Equal(string.Empty, snapshot.Grade);
        Assert.Equal(string.Empty, snapshot.Designation);
        Assert.Null(snapshot.ContractEndDate);
        Assert.Equal(originalLender, h.Loan.CompanyId);
        Assert.True(h.Loan.ReviewRequired);
        var auditPayloads = string.Join("\n", (await h.Db.LoanAuditLogs.ToListAsync()).Select(x => x.OldValuesJson + x.NewValuesJson));
        Assert.DoesNotContain("987654.32", auditPayloads);
        Assert.DoesNotContain("FOREIGN-CONTRACT-PRIVATE", auditPayloads);
        Assert.DoesNotContain("FOREIGN-GRADE-PRIVATE", auditPayloads);
        Assert.DoesNotContain("FOREIGN-DESIGNATION-PRIVATE", auditPayloads);
        Assert.Equal(90m, h.Loan.OutstandingBalance);
    }

    [Fact]
    public async Task DeathReview_HoldsCollection_PreservesDebt_AndDoesNotBroadcastNotifications()
    {
        await using var h = await Harness.Create();
        var user = new User { TenantId = h.Tid, Email = "borrower@example.test", NormalizedEmail = "BORROWER@EXAMPLE.TEST" };
        h.Db.Users.Add(user); h.Employee.UserAccountId = user.Id;
        h.Db.EmployeeOffboardings.Add(new EmployeeOffboarding { TenantId = h.Tid, EmployeeId = h.Employee.Id, SeparationType = "Death", Status = "InProgress", LastWorkingDay = h.Today });
        await h.Db.SaveChangesAsync();
        await new LoanLifecycleService(h.Db).RefreshAsync(h.Tid, h.Loan, default);
        await h.Db.SaveChangesAsync();
        Assert.True(h.Loan.ReviewRequired);
        Assert.Equal("OnHold", h.Loan.CollectionStatus);
        Assert.Equal(90m, h.Loan.OutstandingBalance);
        Assert.Equal(3, await h.Db.LoanInstallments.CountAsync());
        var notification = Assert.Single(await h.Db.Notifications.ToListAsync());
        Assert.Equal(user.Id, notification.UserId);
        Assert.DoesNotContain("Death", notification.Message);
        Assert.Empty(await h.Db.FinanceGlEntries.ToListAsync());
    }

    [Fact]
    public async Task Reschedule_RequiresIndependentApproval_AndPreservesPaidHistoryAndPrincipal()
    {
        await using var h = await Harness.Create();
        var makerId = Guid.NewGuid();
        var maker = h.Controller("Finance", makerId);
        Assert.IsType<OkObjectResult>(await maker.RequestLoanChange(h.Loan.Id,
            new LoanChangeRequestInput("Reschedule", "Employee approved extension", 3, h.Today.AddMonths(1)), default));
        var change = await h.Db.Set<LoanChangeRequest>().SingleAsync();
        Assert.IsType<BadRequestObjectResult>(await maker.DecideLoanChange(h.Loan.Id, change.Id, new LoanChangeDecisionRequest("Approved", "Self approval attempt"), default));
        Assert.Equal(3, await h.Db.LoanInstallments.CountAsync());
        Assert.IsType<OkObjectResult>(await h.Controller("Finance").DecideLoanChange(h.Loan.Id, change.Id, new LoanChangeDecisionRequest("Approved", "Approved affordable schedule"), default));
        var schedule = await h.Db.LoanInstallments.OrderBy(x => x.InstallmentNumber).ToListAsync();
        Assert.Equal(4, schedule.Count);
        Assert.Equal(100m, schedule.Sum(x => x.AmountDue));
        Assert.Equal(10m, schedule.Sum(x => x.AmountPaid));
        Assert.Equal(10m, schedule[0].AmountDue);
        Assert.Equal("Paid", schedule[0].Status);
        Assert.All(schedule.Skip(1), x => Assert.Equal(30m, x.AmountDue));
        Assert.Equal(90m, h.Loan.OutstandingBalance);
        Assert.Equal(100m, h.Loan.ApprovedAmount);
        var audit = Assert.Single(await h.Db.LoanAuditLogs.Where(x => x.Action == "ScheduleRestructured").ToListAsync());
        Assert.Contains("33.33", audit.OldValuesJson);
        Assert.Contains("10", audit.OldValuesJson);
        Assert.Empty(await h.Db.FinanceGlEntries.ToListAsync());
        Assert.IsType<ConflictObjectResult>(await h.Controller("Finance").DecideLoanChange(h.Loan.Id, change.Id, new LoanChangeDecisionRequest("Approved", "Repeated approval"), default));
    }

    [Fact]
    public async Task Reschedule_RefusesPayrollLoans_AndStaleBalance()
    {
        await using var h = await Harness.Create();
        h.Loan.RepaymentMethod = "PayrollDeduction";
        await h.Db.SaveChangesAsync();
        var request = new LoanChangeRequestInput("Reschedule", "Extend remaining installments", 4, h.Today.AddDays(10));
        Assert.IsType<BadRequestObjectResult>(await h.Controller("Finance").RequestLoanChange(h.Loan.Id, request, default));
        h.Loan.RepaymentMethod = "BankTransfer";
        await h.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await h.Controller("Finance").RequestLoanChange(h.Loan.Id, request, default));
        var change = await h.Db.Set<LoanChangeRequest>().SingleAsync();
        h.Loan.OutstandingBalance = 80m; h.Loan.TotalRepaid = 20m;
        await h.Db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await h.Controller("Finance").DecideLoanChange(h.Loan.Id, change.Id, new LoanChangeDecisionRequest("Approved", "Approve previous balance"), default));
        Assert.Equal("Pending", change.Status);
        Assert.Equal(3, await h.Db.LoanInstallments.CountAsync());
    }

    [Fact]
    public async Task Reschedule_EnforcesFrozenPolicyInstallmentLimit()
    {
        await using var h = await Harness.Create();
        h.Loan.PolicySnapshotJson = JsonSerializer.Serialize(new { AllowRescheduling = true, MaxInstallments = 2 });
        await h.Db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await h.Controller("Finance").RequestLoanChange(h.Loan.Id,
            new LoanChangeRequestInput("Reschedule", "Would exceed approved policy", 3, h.Today.AddMonths(1)), default));
        Assert.Empty(await h.Db.LoanChangeRequests.ToListAsync());
        Assert.Equal(90m, h.Loan.OutstandingBalance);
        Assert.Equal(3, await h.Db.LoanInstallments.CountAsync());
    }

    [Fact]
    public async Task PolicyException_IsDirectorOnly_SoftCodeOnly_AndBoundToFrozenPolicy()
    {
        await using var h = await Harness.Create(paid: false);
        Assert.IsType<BadRequestObjectResult>(await h.Controller("HR Manager").RequestLoanChange(h.Loan.Id,
            new LoanChangeRequestInput("PolicyException", "Override inactive employment", ExceptionCodes: ["EmploymentStatus"]), default));
        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").RequestLoanChange(h.Loan.Id,
            new LoanChangeRequestInput("PolicyException", "Exceptional service requirement", ExceptionCodes: ["MinService"]), default));
        var change = await h.Db.Set<LoanChangeRequest>().SingleAsync();
        var decision = new LoanChangeDecisionRequest("Approved", "Director documented exception");
        Assert.IsType<ForbidResult>(await h.Controller("Finance").DecideLoanChange(h.Loan.Id, change.Id, decision, default));
        Assert.IsType<OkObjectResult>(await h.Controller("HR Director").DecideLoanChange(h.Loan.Id, change.Id, decision, default));
        var service = new LoanLifecycleService(h.Db);
        Assert.True(await service.IsExceptionApprovedAsync(h.Tid, h.Loan, "MinService", default));
        Assert.False(await service.IsExceptionApprovedAsync(h.Tid, h.Loan, "EmploymentStatus", default));
        h.Loan.PolicyVersion = 2;
        Assert.False(await service.IsExceptionApprovedAsync(h.Tid, h.Loan, "MinService", default));
        Assert.Equal(0m, h.Loan.OutstandingBalance);
        Assert.Null(h.Loan.DisbursementDate);
        Assert.Empty(await h.Db.FinanceGlEntries.ToListAsync());
    }

    [Fact]
    public async Task LifecycleReview_RefusesSelfReview_PaidCancellation_AndUnreleasedBatch()
    {
        await using var h = await Harness.Create();
        Assert.IsType<BadRequestObjectResult>(await h.Controller("HR Manager", h.Loan.CreatedBy).ReviewLoanLifecycle(h.Loan.Id, new LoanLifecycleReviewRequest("Hold", "Self review attempt"), default));
        Assert.IsType<ConflictObjectResult>(await h.Controller("HR Director").ReviewLoanLifecycle(h.Loan.Id, new LoanLifecycleReviewRequest("Cancel", "Cannot cancel paid debt"), default));
        Assert.Equal(90m, h.Loan.OutstandingBalance);
        await using var unpaid = await Harness.Create(paid: false);
        var batch = new LoanDisbursementBatch { TenantId = unpaid.Tid, CompanyId = unpaid.Loan.CompanyId, Currency = "SAR", TotalAmount = 100m, BatchNumber = "PENDING" };
        unpaid.Db.AddRange(batch, new LoanDisbursementLine { TenantId = unpaid.Tid, BatchId = batch.Id, LoanId = unpaid.Loan.Id, Amount = 100m });
        await unpaid.Db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await unpaid.Controller("HR Director").ReviewLoanLifecycle(unpaid.Loan.Id, new LoanLifecycleReviewRequest("Cancel", "Release bank instructions first"), default));
        Assert.Equal("Approved", unpaid.Loan.Status);
    }

    [Fact]
    public async Task UnpaidInactiveLoan_CannotBeContinuedByHr_AndUnauthorizedRoleCannotReview()
    {
        await using var h = await Harness.Create(paid: false);
        h.Employee.Status = EmployeeStatuses.Inactive;
        await h.Db.SaveChangesAsync();
        var request = new LoanLifecycleReviewRequest("Continue", "Attempt to release blocked loan");
        Assert.IsType<ConflictObjectResult>(await h.Controller("HR Director").ReviewLoanLifecycle(h.Loan.Id, request, default));
        Assert.IsType<ForbidResult>(await h.Controller("Finance").ReviewLoanLifecycle(h.Loan.Id, request, default));
        Assert.Null(h.Loan.DisbursementDate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnpaidNoticeReview_HonorsFrozenPolicyNoticeToggle(bool blockDuringNotice)
    {
        await using var h = await Harness.Create(paid: false);
        h.Employee.Status = EmployeeStatuses.Offboarded;
        h.Loan.PolicySnapshotJson = JsonSerializer.Serialize(new LoanPolicy
        {
            Id = h.Loan.PolicyId!.Value, TenantId = h.Tid, CompanyId = h.Loan.CompanyId, LoanTypeId = h.Loan.LoanTypeId,
            Version = 1, BlockDuringNotice = blockDuringNotice, AllowedEmploymentStatusesJson = "[\"Active\",\"Offboarded\"]",
            MaxConcurrentLoans = 2, MaxAmount = 1000m, MaxInstallments = 12
        });
        h.Db.EmployeeOffboardings.Add(new EmployeeOffboarding { TenantId = h.Tid, EmployeeId = h.Employee.Id, Status = "InProgress", SeparationType = "Resignation", LastWorkingDay = h.Today.AddMonths(1) });
        await h.Db.SaveChangesAsync();
        await new LoanLifecycleService(h.Db).RefreshAsync(h.Tid, h.Loan, default);
        Assert.True(h.Loan.ReviewRequired);
        var result = await h.Controller("HR Director").ReviewLoanLifecycle(h.Loan.Id, new LoanLifecycleReviewRequest("Continue", "Independent notice policy review"), default);
        if (blockDuringNotice)
        {
            Assert.IsType<ConflictObjectResult>(result);
            Assert.True(h.Loan.ReviewRequired);
        }
        else
        {
            Assert.IsType<OkObjectResult>(result);
            Assert.False(h.Loan.ReviewRequired);
        }
        Assert.Null(h.Loan.DisbursementDate);
        Assert.Equal(0m, h.Loan.OutstandingBalance);
        Assert.Empty(await h.Db.FinanceGlEntries.ToListAsync());
    }

    private sealed class Harness : IAsyncDisposable
    {
        public DbContextOptions<ZayraDbContext> Options { get; } = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public ZayraDbContext Db { get; }
        private Harness() => Db = new(Options);
        public Guid Tid { get; } = Guid.NewGuid();
        public Employee Employee { get; private set; } = null!;
        public EmployeeLoan Loan { get; private set; } = null!;
        public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
        public static async Task<Harness> Create(bool paid = true)
        {
            var h = new Harness();
            var company = new Company { TenantId = h.Tid, LegalNameEn = "Loan Lender", CountryCode = "SAU", DefaultCurrency = "SAR" };
            var type = new LoanType { TenantId = h.Tid, Code = "PERSONAL", NameEn = "Personal", IsActive = true, MaxAmount = 1000m, MaxInstallments = 12 };
            h.Employee = new Employee { TenantId = h.Tid, CompanyId = company.Id, EmployeeCode = "LIFECYCLE", FullName = "Loan Employee", Status = "Active", Salary = 10_000m, JoiningDate = DateTime.UtcNow.AddYears(-3) };
            h.Db.AddRange(company, h.Employee, type);
            await h.Db.SaveChangesAsync();
            h.Loan = new EmployeeLoan
            {
                TenantId = h.Tid, CompanyId = company.Id, EmployeeId = h.Employee.PublicId, EmployeeIntId = h.Employee.Id,
                EmployeeName = h.Employee.FullName, LoanNumber = "LIFECYCLE-1", RepaymentMethod = "BankTransfer", Currency = "SAR",
                LoanTypeId = type.Id, LoanTypeName = type.NameEn,
                Status = paid ? "Active" : "Approved", RequestedAmount = 100m, ApprovedAmount = 100m, RequestedInstallments = 3,
                ApprovedInstallments = 3, InstallmentAmount = 33.33m, TotalRepaid = paid ? 10m : 0m, OutstandingBalance = paid ? 90m : 0m,
                DisbursementDate = paid ? h.Today.AddMonths(-1) : null, RepaymentStartDate = h.Today,
                CreatedBy = Guid.NewGuid(), PolicyId = Guid.NewGuid(), PolicyVersion = 1,
                PolicySnapshotJson = JsonSerializer.Serialize(new { AllowRescheduling = true, AllowExceptions = true })
            };
            h.Db.EmployeeLoans.Add(h.Loan);
            if (paid)
                for (var i = 1; i <= 3; i++) h.Db.LoanInstallments.Add(new LoanInstallment
                {
                    TenantId = h.Tid, LoanId = h.Loan.Id, InstallmentNumber = i, DueDate = h.Today.AddMonths(i - 1),
                    AmountDue = i == 3 ? 33.34m : 33.33m, AmountPaid = i == 1 ? 10m : 0m
                });
            await h.Db.SaveChangesAsync();
            return h;
        }
        public LoansController Controller(string role, Guid? userId = null) => new(Db, new Scope())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", Tid.ToString()), new Claim(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
                        new Claim(ClaimTypes.Name, "Loan Reviewer"), new Claim(ClaimTypes.Role, role)
                    }, "test"))
                }
            }
        };
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    private sealed class Scope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
