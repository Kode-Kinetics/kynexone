using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// <c>POST /api/employees/{id}/resolve-duplicate</c> with <c>merge</c> on a REAL PostgreSQL: what happens to the money and compliance
/// records the duplicate owns. Today the merge MOVES NOTHING (it links <c>DuplicateOfEmployeeId</c>, soft-deletes the duplicate, cancels
/// its approvals and deactivates its payroll profile and salary structures), so every record below stays keyed to the soft-deleted row.
///
/// The merge moves nothing, so it REFUSES (HTTP 409 <c>merge_blocked_by_records</c>) when the duplicate owns any money or compliance
/// record, before any write. A duplicate that owns none, or only rows that carry no value, still merges.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeMergeOwnedRecordsPostgresTests
{
    private readonly PostgresFixture _fx;
    public EmployeeMergeOwnedRecordsPostgresTests(PostgresFixture fx) => _fx = fx;

    public sealed record Twins(Guid TenantId, Guid CompanyId, Employee Duplicate, Employee Survivor);

    private async Task<Twins> SeedTwinsAsync()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Merge PG", CountryCode = "SAU", Jurisdiction = "KSA-mainland", RegistrationNumber = $"M-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        Employee Row(string code) => new()
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = code, FullName = $"Merge {code}", EnglishName = $"Merge {code}",
            Status = "Active", ReadinessState = "Ready", JoiningDate = DateTime.SpecifyKind(new DateTime(2025, 1, 1), DateTimeKind.Utc),
        };
        var dup = Row("DUP-1");
        var survivor = Row("KEEP-1");
        db.Employees.AddRange(dup, survivor);
        await db.SaveChangesAsync();
        return new Twins(tenantId, company.Id, dup, survivor);
    }

    private EmployeesController Controller(ZayraDbContext db, Twins t, Func<ZayraDbContext, IAuditService>? audit = null, bool mayDelete = true)
    {
        var controller = new EmployeesController(db, new Pbkdf2PasswordHasher(), audit?.Invoke(db) ?? new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db),
            new MergePgHijri(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new MergePgLetters());
        var claims = new List<Claim>
        {
            new("tenant_id", t.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, "Admin"),
            new("permission", "employees.read"), new("permission", "employees.write"),
            new("is_group_scope", "true"),
        };
        if (mayDelete) claims.Add(new Claim("permission", "employees.delete"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };
        return controller;
    }

    private async Task<IActionResult> MergeAsync(Twins t, Func<ZayraDbContext, IAuditService>? audit = null)
    {
        await using var db = _fx.CreateDb();
        return await Controller(db, t, audit).ResolveDuplicate(t.Duplicate.Id, new ResolveDuplicateRequest("merge", t.Survivor.Id, "same person"), CancellationToken.None);
    }

    private async Task<bool> IsDeletedAsync(int id)
    {
        await using var db = _fx.CreateDb();
        return await db.Employees.IgnoreQueryFilters().Where(e => e.Id == id).Select(e => e.IsDeleted).SingleAsync();
    }

    /// <summary>One record type the duplicate can own: how to seed it, and how many of that type each employee owns afterwards.</summary>
    public sealed record OwnedRecord(string Name, string Type, Func<ZayraDbContext, Twins, Task> Seed, Func<ZayraDbContext, int, Task<int>> CountOwnedBy)
    {
        public override string ToString() => Name;
    }

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public static IEnumerable<object[]> Blocking() => Catalogue().Select(r => new object[] { r });

    private static IEnumerable<OwnedRecord> Catalogue()
    {
        yield return new("open loan", "open_loans", (db, t) =>
        {
            db.EmployeeLoans.Add(new EmployeeLoan
            {
                TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.PublicId, EmployeeIntId = t.Duplicate.Id, EmployeeName = t.Duplicate.FullName,
                LoanTypeId = Guid.NewGuid(), LoanTypeName = "Personal", LoanNumber = "LN-" + Guid.NewGuid().ToString("N"), ApprovedAmount = 3000m, ApprovedInstallments = 6,
                InstallmentAmount = 500m, OutstandingBalance = 1500m, Status = "Active", RepaymentMethod = "PayrollDeduction", Currency = "SAR",
            });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeeLoans.IgnoreQueryFilters().CountAsync(x => x.EmployeeIntId == id));

        yield return new("open salary advance", "open_advances", (db, t) =>
        {
            db.SalaryAdvances.Add(new SalaryAdvance
            {
                TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.PublicId, EmployeeIntId = t.Duplicate.Id, EmployeeName = t.Duplicate.FullName,
                AdvanceNumber = "ADV-" + Guid.NewGuid().ToString("N"), ApprovedAmount = 1000m, OutstandingBalance = 1000m, Status = "Active",
            });
            return Task.CompletedTask;
        }, (db, id) => db.SalaryAdvances.IgnoreQueryFilters().CountAsync(x => x.EmployeeIntId == id));

        yield return new("approved bonus not yet paid", "unpaid_bonuses", (db, t) =>
        {
            db.EmployeeBonuses.Add(new EmployeeBonus
            {
                TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.PublicId, EmployeeIntId = t.Duplicate.Id, EmployeeName = t.Duplicate.FullName,
                BonusBatchId = Guid.NewGuid(), BonusTypeId = Guid.NewGuid(), BonusTypeName = "Annual", BonusAmount = 2000m, GrossBonusAmount = 2000m, Status = "Approved",
            });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeeBonuses.IgnoreQueryFilters().CountAsync(x => x.EmployeeIntId == id));

        yield return new("leave balance with days left", "leave_balances", (db, t) =>
        {
            db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
            {
                TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, EmployeeName = t.Duplicate.FullName, LeaveTypeId = Guid.NewGuid(), LeaveTypeName = "Annual",
                Year = DateTime.UtcNow.Year, Entitled = 21m, Accrued = 21m,
            });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeeLeaveBalances.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("pending leave request", "leave_requests", (db, t) =>
        {
            db.LeaveRequests.Add(new LeaveRequest
            {
                TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.Id, EmployeeName = t.Duplicate.FullName, LeaveTypeId = Guid.NewGuid(),
                LeaveTypeName = "Annual", StartDate = Today.AddDays(10), EndDate = Today.AddDays(12), TotalDays = 3m, Status = "Pending",
            });
            return Task.CompletedTask;
        }, (db, id) => db.LeaveRequests.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("active benefit enrolment", "benefit_enrollments", (db, t) =>
        {
            db.BenefitEnrollments.Add(new BenefitEnrollment
            {
                TenantId = t.TenantId, CompanyId = t.CompanyId, BenefitPlanId = Guid.NewGuid(), EmployeeId = t.Duplicate.Id, EmployeeName = t.Duplicate.FullName, Status = "Active",
            });
            return Task.CompletedTask;
        }, (db, id) => db.BenefitEnrollments.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("payroll slip in an open run", "payroll_slips", (db, t) =>
        {
            db.PayrollSlips.Add(new PayrollSlip
            {
                TenantId = t.TenantId, CompanyId = t.CompanyId, RunId = Guid.NewGuid(), EmployeeId = t.Duplicate.Id, EmployeeCode = t.Duplicate.EmployeeCode,
                EmployeeName = t.Duplicate.FullName, BasicSalary = 8000m, GrossSalary = 8000m, NetSalary = 8000m, Status = "Draft",
            });
            return Task.CompletedTask;
        }, (db, id) => db.PayrollSlips.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("social-insurance (GOSI) registration reference", "payroll_profile", (db, t) =>
        {
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, SocialInsuranceReference = "GOSI-123456", SalaryCurrency = "SAR", WpsEligible = false,
            });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeePayrollProfiles.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id && x.SocialInsuranceReference != ""));

        yield return new("end-of-service calculation", "eosb_calculations", (db, t) =>
        {
            db.EOSBCalculations.Add(new EOSBCalculation
            {
                TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, CalculationDate = Today, EligibleSalary = 8000m, CalculatedAmount = 12000m, Status = "Approved",
            });
            return Task.CompletedTask;
        }, (db, id) => db.EOSBCalculations.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("employee document", "documents", (db, t) =>
        {
            db.EmployeeDocuments.Add(new EmployeeDocument
            {
                TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.Id, DocumentType = "Contract", FileName = "contract.pdf", ContentType = "application/pdf",
            });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeeDocuments.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("identity / compliance record", "compliance_records", (db, t) =>
        {
            db.EmployeeComplianceRecords.Add(new EmployeeComplianceRecord
            {
                TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, CountryCode = "SA", FieldKey = "iqama_number", FieldLabel = "Iqama", FieldValue = "2000000006",
            });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeeComplianceRecords.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("salary structure assignment", "salary_structures", (db, t) =>
        {
            db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = 8000m, EffectiveDate = Today, Currency = "SAR", IsActive = true,
            });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeeSalaryStructures.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("receivable the employee owes", "payroll_receivables", (db, t) =>
        {
            db.PayrollEmployeeReceivables.Add(new PayrollEmployeeReceivable { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, Amount = 3000m, RecoveredAmount = 0m, Status = "Outstanding" });
            return Task.CompletedTask;
        }, (db, id) => db.PayrollEmployeeReceivables.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("pending pay arrears", "payroll_arrears", (db, t) =>
        {
            db.PayrollArrearsLines.Add(new PayrollArrearsLine { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, Amount = 500m, EntitledAmount = 500m, Status = "PendingRecovery" });
            return Task.CompletedTask;
        }, (db, id) => db.PayrollArrearsLines.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("final settlement", "final_settlements", (db, t) =>
        {
            db.EmployeeFinalSettlements.Add(new EmployeeFinalSettlement { TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.Id, EmployeeCode = t.Duplicate.EmployeeCode, NetPayable = 9000m, Status = "Approved" });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeeFinalSettlements.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("payroll opening balance", "opening_balances", (db, t) =>
        {
            db.PayrollOpeningBalances.Add(new PayrollOpeningBalance { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, EmployeeCode = t.Duplicate.EmployeeCode, BalanceType = "Loan", Amount = 1200m });
            return Task.CompletedTask;
        }, (db, id) => db.PayrollOpeningBalances.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("end-of-service provision snapshot", "eosb_provisions", (db, t) =>
        {
            var run = new PayrollRun { TenantId = t.TenantId, CompanyId = t.CompanyId, Year = 2026, Month = 6, Status = "Draft" };
            db.PayrollRuns.Add(run);
            db.EosbProvisionSnapshots.Add(new EosbProvisionSnapshot { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, PayrollRunId = run.Id });
            return Task.CompletedTask;
        }, (db, id) => db.EosbProvisionSnapshots.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("bank payment record", "payment_records", (db, t) =>
        {
            db.PayrollPaymentRecords.Add(new PayrollPaymentRecord { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, PaymentBatchId = Guid.NewGuid(), Amount = 8000m, Status = "Pending" });
            return Task.CompletedTask;
        }, (db, id) => db.PayrollPaymentRecords.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("payroll adjustment", "payroll_adjustments", (db, t) =>
        {
            db.PayrollAdjustments.Add(new PayrollAdjustment { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, PayrollRunId = Guid.NewGuid(), Amount = 250m, Status = "Pending" });
            return Task.CompletedTask;
        }, (db, id) => db.PayrollAdjustments.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("overtime approved but unpaid", "unpaid_overtime", (db, t) =>
        {
            db.OvertimeRequests.Add(new OvertimeRequest { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, EmployeeName = t.Duplicate.FullName, Status = "Approved" });
            return Task.CompletedTask;
        }, (db, id) => db.OvertimeRequests.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("leave encashment request", "leave_encashments", (db, t) =>
        {
            db.LeaveEncashmentRequests.Add(new LeaveEncashmentRequest { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, EmployeeName = t.Duplicate.FullName, DaysToEncash = 3m, TotalAmount = 900m, Status = "Pending" });
            return Task.CompletedTask;
        }, (db, id) => db.LeaveEncashmentRequests.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("compensatory-leave credit", "comp_off_credits", (db, t) =>
        {
            db.CompOffCredits.Add(new CompOffCredit { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, EmployeeName = t.Duplicate.FullName, HoursWorked = 8m, DaysEarned = 1m, Status = "Active" });
            return Task.CompletedTask;
        }, (db, id) => db.CompOffCredits.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("pending salary change", "salary_change_requests", (db, t) =>
        {
            db.SalaryChangeRequests.Add(new SalaryChangeRequest { TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.Id, EmployeeCode = t.Duplicate.EmployeeCode, EmployeeName = t.Duplicate.FullName, SalaryStructureId = Guid.NewGuid(), Status = "Pending" });
            return Task.CompletedTask;
        }, (db, id) => db.SalaryChangeRequests.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("live separation", "live_separations", (db, t) =>
        {
            db.EmployeeOffboardings.Add(new EmployeeOffboarding { TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.Id, EmployeeName = t.Duplicate.FullName, EmployeeCode = t.Duplicate.EmployeeCode, LastWorkingDay = Today, Status = "InProgress" });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeeOffboardings.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("attendance effect on pay not yet applied", "pending_payroll_impacts", (db, t) =>
        {
            db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, Status = "PendingPayroll" });
            return Task.CompletedTask;
        }, (db, id) => db.AttendancePayrollImpacts.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("visa record (keyed by the public id)", "contracts_and_permits", (db, t) =>
        {
            db.VisaRecords.Add(new VisaRecord { TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.PublicId, EmployeeName = t.Duplicate.FullName, IssueDate = Today, ExpiryDate = Today.AddYears(1) });
            return Task.CompletedTask;
        }, (db, id) => db.VisaRecords.IgnoreQueryFilters().CountAsync(x => db.Employees.IgnoreQueryFilters().Where(e => e.Id == id).Select(e => e.PublicId).Contains(x.EmployeeId)));

        yield return new("dependent", "dependents", (db, t) =>
        {
            db.EmployeeDependents.Add(new EmployeeDependent { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, FullName = "Child One", Relationship = "Child" });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeeDependents.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("approval authority", "approval_authority", (db, t) =>
        {
            db.ApprovalAuthorities.Add(new ApprovalAuthority { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, ApproverRole = "Finance", IsActive = true });
            return Task.CompletedTask;
        }, (db, id) => db.ApprovalAuthorities.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("approval delegation to the duplicate", "approval_authority", (db, t) =>
        {
            db.ApprovalDelegations.Add(new ApprovalDelegation { TenantId = t.TenantId, FromEmployeeId = t.Survivor.Id, ToEmployeeId = t.Duplicate.Id, StartDate = Today.AddDays(-1), EndDate = Today.AddDays(30), Status = "Active" });
            return Task.CompletedTask;
        }, (db, id) => db.ApprovalDelegations.IgnoreQueryFilters().CountAsync(x => x.ToEmployeeId == id));

        yield return new("legacy loan with no int employee id", "open_loans", (db, t) =>
        {
            db.EmployeeLoans.Add(new EmployeeLoan { TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.PublicId, EmployeeIntId = null, LoanTypeId = Guid.NewGuid(), LoanNumber = "LN-" + Guid.NewGuid().ToString("N"), OutstandingBalance = 700m, Status = "Active" });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeeLoans.IgnoreQueryFilters().CountAsync(x => x.EmployeeIntId == null && db.Employees.IgnoreQueryFilters().Where(e => e.Id == id).Select(e => e.PublicId).Contains(x.EmployeeId)));

        yield return new("person who reports to the duplicate", "direct_reports", (db, t) =>
        {
            db.Employees.Add(new Employee { TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeCode = "RPT-" + Guid.NewGuid().ToString("N")[..6], FullName = "Report Person", EnglishName = "Report Person", Status = "Active", ReadinessState = "Ready", JoiningDate = DateTime.SpecifyKind(new DateTime(2025, 1, 1), DateTimeKind.Utc), ManagerEmployeeId = t.Duplicate.Id });
            return Task.CompletedTask;
        }, (db, id) => db.Employees.IgnoreQueryFilters().CountAsync(x => x.ManagerEmployeeId == id));

        yield return new("reporting line managed by the duplicate", "reporting_lines", (db, t) =>
        {
            db.ReportingLines.Add(new ReportingLine { TenantId = t.TenantId, EmployeeId = t.Survivor.Id, ManagerEmployeeId = t.Duplicate.Id });
            return Task.CompletedTask;
        }, (db, id) => db.ReportingLines.IgnoreQueryFilters().CountAsync(x => x.ManagerEmployeeId == id));

        yield return new("department managed by the duplicate", "department_manager", (db, t) =>
        {
            db.Departments.Add(new Department { TenantId = t.TenantId, CompanyId = t.CompanyId, Code = "D-" + Guid.NewGuid().ToString("N")[..6], NameEn = "Dept", ManagerEmployeeId = t.Duplicate.Id });
            return Task.CompletedTask;
        }, (db, id) => db.Departments.IgnoreQueryFilters().CountAsync(x => x.ManagerEmployeeId == id));

        yield return new("position held by the duplicate", "position_incumbent", (db, t) =>
        {
            db.Positions.Add(new Position { TenantId = t.TenantId, CompanyId = t.CompanyId, Code = "P-" + Guid.NewGuid().ToString("N")[..6], Title = "Role", IncumbentEmployeeId = t.Duplicate.Id });
            return Task.CompletedTask;
        }, (db, id) => db.Positions.IgnoreQueryFilters().CountAsync(x => x.IncumbentEmployeeId == id));
    }

    // ── 1. LIVE ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_ADuplicateThatOwnsNoMoneyOrComplianceRecords_StillMerges()
    {
        var t = await SeedTwinsAsync();
        var result = await MergeAsync(t);
        Assert.IsType<OkObjectResult>(result);
        (await IsDeletedAsync(t.Duplicate.Id)).Should().BeTrue();
        (await IsDeletedAsync(t.Survivor.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Merge_ARowThatCarriesNoValue_DoesNotBlock_AndNeitherDoesALoginLink()
    {
        var t = await SeedTwinsAsync();
        await using (var seed = _fx.CreateDb())
        {
            // The empty shells a fresh record can hold: a payroll profile with no bank/insurance value, a compliance row with no value or
            // date, a leave balance of zero, a settled loan, a paid bonus, a rejected leave request, a waived benefit, and a login link.
            seed.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id });
            seed.EmployeeComplianceRecords.Add(new EmployeeComplianceRecord { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, CountryCode = "SA", FieldKey = "iqama_number" });
            seed.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, LeaveTypeId = Guid.NewGuid(), Year = DateTime.UtcNow.Year });
            seed.EmployeeLoans.Add(new EmployeeLoan { TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.PublicId, EmployeeIntId = t.Duplicate.Id, LoanTypeId = Guid.NewGuid(), Status = "Settled" });
            seed.EmployeeBonuses.Add(new EmployeeBonus { TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.PublicId, EmployeeIntId = t.Duplicate.Id, BonusBatchId = Guid.NewGuid(), BonusTypeId = Guid.NewGuid(), Status = "PaidInPayroll" });
            seed.LeaveRequests.Add(new LeaveRequest { TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.Id, LeaveTypeId = Guid.NewGuid(), StartDate = Today, EndDate = Today, Status = "Rejected" });
            seed.BenefitEnrollments.Add(new BenefitEnrollment { TenantId = t.TenantId, CompanyId = t.CompanyId, BenefitPlanId = Guid.NewGuid(), EmployeeId = t.Duplicate.Id, Status = "Waived" });
            seed.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, Status = "Active", RequiresPasswordSetup = false });
            await seed.SaveChangesAsync();
        }

        var result = await MergeAsync(t);

        Assert.IsType<OkObjectResult>(result);
        (await IsDeletedAsync(t.Duplicate.Id)).Should().BeTrue();
    }

    // ── 2. The refusal ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Blocking))]
    public async Task Merge_IsRefusedWith409NamingTheRecord_AndDeletesNothing_WhenTheDuplicateOwnsOne(OwnedRecord record)
    {
        var t = await SeedTwinsAsync();
        await using (var seed = _fx.CreateDb())
        {
            await record.Seed(seed, t);
            await seed.SaveChangesAsync();
        }

        var result = await MergeAsync(t);

        var conflict = Assert.IsAssignableFrom<ObjectResult>(result);
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        using var body = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(conflict.Value));
        var root = body.RootElement;
        root.GetProperty("error").GetString().Should().Be("merge_blocked_by_records");
        root.GetProperty("code").GetString().Should().Be("merge_blocked_by_records");
        root.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("messageAr").GetString().Should().MatchRegex("[\\u0600-\\u06FF]");
        var blockers = root.GetProperty("blockers").EnumerateArray().ToList();
        blockers.Should().ContainSingle(b => b.GetProperty("type").GetString() == record.Type, $"'{record.Name}' is the one blocker seeded");
        var b1 = blockers.Single(b => b.GetProperty("type").GetString() == record.Type);
        b1.GetProperty("count").GetInt32().Should().Be(1);
        b1.GetProperty("label").GetString().Should().NotBeNullOrWhiteSpace();
        b1.GetProperty("labelAr").GetString().Should().MatchRegex("[\\u0600-\\u06FF]");
        (await IsDeletedAsync(t.Duplicate.Id)).Should().BeFalse("a refused merge deletes nothing");
        await using var read = _fx.CreateDb();
        (await read.Employees.IgnoreQueryFilters().Where(e => e.Id == t.Duplicate.Id).Select(e => e.DuplicateOfEmployeeId).SingleAsync()).Should().BeNull();
        (await record.CountOwnedBy(read, t.Duplicate.Id)).Should().Be(1);
        (await record.CountOwnedBy(read, t.Survivor.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Merge_Refusal_ListsEveryBlockerWithItsCount()
    {
        var t = await SeedTwinsAsync();
        await using (var seed = _fx.CreateDb())
        {
            foreach (var r in Catalogue().Where(r => r.Name is "open loan" or "employee document"))
            {
                await r.Seed(seed, t);
                await r.Seed(seed, t); // two of each
            }
            await seed.SaveChangesAsync();
        }

        var conflict = Assert.IsAssignableFrom<ObjectResult>(await MergeAsync(t));

        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        using var body = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(conflict.Value));
        var counts = body.RootElement.GetProperty("blockers").EnumerateArray().ToDictionary(b => b.GetProperty("type").GetString()!, b => b.GetProperty("count").GetInt32());
        counts.Should().BeEquivalentTo(new Dictionary<string, int> { ["open_loans"] = 2, ["documents"] = 2 });
    }

    [Fact]
    public async Task Merge_Refusal_LeavesNoPartialState()
    {
        var t = await SeedTwinsAsync();
        Guid approvalId;
        await using (var seed = _fx.CreateDb())
        {
            await Catalogue().Single(r => r.Type == "payroll_profile").Seed(seed, t); // WPS-ineligible by the seeder; flip it so a footprint change would show
            await Catalogue().Single(r => r.Type == "salary_structures").Seed(seed, t);
            var approval = new ApprovalRequest
            {
                TenantId = t.TenantId, RequestedForEmployeeId = t.Duplicate.Id, EntityName = "Employee", EntityId = t.Duplicate.Id.ToString(), Status = "Pending",
            };
            approvalId = approval.Id;
            seed.ApprovalRequests.Add(approval);
            seed.EmployeeImportGaps.Add(new EmployeeImportGap { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, GapType = "dup:strong" });
            await seed.SaveChangesAsync();
            var profile = await seed.EmployeePayrollProfiles.SingleAsync(p => p.EmployeeId == t.Duplicate.Id);
            profile.WpsEligible = true;
            await seed.SaveChangesAsync();
        }

        var result = await MergeAsync(t);

        Assert.IsAssignableFrom<ObjectResult>(result).StatusCode.Should().Be(StatusCodes.Status409Conflict);
        await using var read = _fx.CreateDb();
        var dup = await read.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == t.Duplicate.Id);
        dup.IsDeleted.Should().BeFalse();
        dup.Status.Should().Be("Active");
        dup.DuplicateOfEmployeeId.Should().BeNull();
        dup.PrivacyStatus.Should().BeEmpty();
        dup.RetentionUntilUtc.Should().BeNull();
        (await read.ApprovalRequests.IgnoreQueryFilters().SingleAsync(a => a.Id == approvalId)).Status.Should().Be("Pending", "no approval is cancelled by a refused merge");
        (await read.EmployeePayrollProfiles.IgnoreQueryFilters().SingleAsync(p => p.EmployeeId == t.Duplicate.Id)).WpsEligible.Should().BeTrue();
        (await read.EmployeeSalaryStructures.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == t.Duplicate.Id)).IsActive.Should().BeTrue();
        (await read.EmployeeImportGaps.IgnoreQueryFilters().SingleAsync(g => g.EmployeeId == t.Duplicate.Id)).ResolvedAtUtc.Should().BeNull();
        (await read.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.TenantId == t.TenantId && a.Action == "employee.merged_into_existing")).Should().BeFalse();
    }

    // ── 3. The merge is one unit of work, and it closes the duplicate's login ───────────────────────

    private async Task<(Guid UserId, Guid LinkId, Guid RefreshId)> SeedLoginAsync(Twins t)
    {
        await using var db = _fx.CreateDb();
        var user = new User
        {
            TenantId = t.TenantId, Email = $"{Guid.NewGuid():N}@merge.test", NormalizedEmail = $"{Guid.NewGuid():N}@MERGE.TEST", FullName = "Dup Person",
            PasswordHash = "unimportant", Status = "Active", AccessMode = AccessModes.EssOnly, IsActive = true, IsEmailConfirmed = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var dup = await db.Employees.SingleAsync(e => e.Id == t.Duplicate.Id);
        dup.UserAccountId = user.Id;
        var link = new EmployeeUserAccount
        {
            TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, UserId = user.Id, AccessMode = AccessModes.EssOnly, Status = "Active",
            InvitationTokenHash = "live-invitation", InvitationExpiresAtUtc = DateTime.UtcNow.AddDays(1),
        };
        var refresh = new RefreshToken { UserId = user.Id, TokenHash = "live-refresh-" + Guid.NewGuid().ToString("N"), ExpiresAtUtc = DateTime.UtcNow.AddDays(7) };
        db.AddRange(link, refresh);
        await db.SaveChangesAsync();
        return (user.Id, link.Id, refresh.Id);
    }

    [Fact]
    public async Task Merge_ClosesTheDuplicatesLogin_AndItsRefreshTokens()
    {
        var t = await SeedTwinsAsync();
        var (userId, linkId, refreshId) = await SeedLoginAsync(t);

        Assert.IsType<OkObjectResult>(await MergeAsync(t));

        await using var read = _fx.CreateDb();
        var link = await read.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync(x => x.Id == linkId);
        link.AccessMode.Should().Be(AccessModes.NoLogin, "a merged duplicate's self-service login must not authenticate");
        link.Status.Should().Be("NoLogin");
        link.InvitationTokenHash.Should().BeEmpty();
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == userId);
        user.IsActive.Should().BeFalse();
        user.Status.Should().Be("Deactivated");
        (await read.RefreshTokens.SingleAsync(x => x.Id == refreshId)).RevokedAtUtc.Should().NotBeNull("a live refresh token kept minting access tokens after the merge");
        var audit = await read.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.Action == "employee.merged_into_existing" && a.EntityId == t.Duplicate.Id.ToString());
        audit.Metadata.Should().Contain("\"refreshTokens\":1").And.Contain("\"links\":1");
    }

    [Fact]
    public async Task Merge_AFailureMidWay_LeavesNothingChanged()
    {
        var t = await SeedTwinsAsync();
        var (userId, linkId, refreshId) = await SeedLoginAsync(t);
        Guid approvalId;
        await using (var seed = _fx.CreateDb())
        {
            var approval = new ApprovalRequest { TenantId = t.TenantId, RequestedForEmployeeId = t.Duplicate.Id, EntityName = "Employee", EntityId = t.Duplicate.Id.ToString(), Status = "Pending" };
            approvalId = approval.Id;
            seed.ApprovalRequests.Add(approval);
            seed.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, WpsEligible = true });
            seed.EmployeeImportGaps.Add(new EmployeeImportGap { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, GapType = "dup:strong" });
            await seed.SaveChangesAsync();
        }

        // The audit row is the last thing the merge writes: if it cannot be written, NOTHING before it may have been saved.
        var act = async () => await MergeAsync(t, db => new FailingMergeAudit(new AuditService(db)));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*audit unavailable*");

        await using var read = _fx.CreateDb();
        var dup = await read.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == t.Duplicate.Id);
        dup.IsDeleted.Should().BeFalse();
        dup.DuplicateOfEmployeeId.Should().BeNull();
        dup.Status.Should().Be("Active");
        (await read.ApprovalRequests.IgnoreQueryFilters().SingleAsync(a => a.Id == approvalId)).Status.Should().Be("Pending");
        (await read.EmployeePayrollProfiles.IgnoreQueryFilters().SingleAsync(p => p.EmployeeId == t.Duplicate.Id)).WpsEligible.Should().BeTrue();
        (await read.EmployeeImportGaps.IgnoreQueryFilters().SingleAsync(g => g.EmployeeId == t.Duplicate.Id)).ResolvedAtUtc.Should().BeNull();
        (await read.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync(x => x.Id == linkId)).AccessMode.Should().Be(AccessModes.EssOnly);
        (await read.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == userId)).IsActive.Should().BeTrue();
        (await read.RefreshTokens.SingleAsync(x => x.Id == refreshId)).RevokedAtUtc.Should().BeNull();
    }

    // ── 4. A merge only combines duplicate entries within ONE company ───────────────────────────────

    private async Task SetCompaniesAsync(Twins t, Guid? duplicateCompany, Guid? survivorCompany)
    {
        await using var db = _fx.CreateDb();
        (await db.Employees.SingleAsync(e => e.Id == t.Duplicate.Id)).CompanyId = duplicateCompany;
        (await db.Employees.SingleAsync(e => e.Id == t.Survivor.Id)).CompanyId = survivorCompany;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedOtherCompanyAsync(Twins t)
    {
        await using var db = _fx.CreateDb();
        var other = new Company { Id = Guid.NewGuid(), TenantId = t.TenantId, LegalNameEn = "Merge PG Two", CountryCode = "SAU", Jurisdiction = "KSA-mainland", RegistrationNumber = $"M-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        db.Companies.Add(other);
        await db.SaveChangesAsync();
        return other.Id;
    }

    [Fact]
    public async Task Merge_AcrossTwoCompanies_IsRefused409_WithAPlainMessage_AndDeletesNothing()
    {
        var t = await SeedTwinsAsync();
        await SetCompaniesAsync(t, t.CompanyId, await SeedOtherCompanyAsync(t));

        var conflict = Assert.IsAssignableFrom<ObjectResult>(await MergeAsync(t));

        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        using var body = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(conflict.Value));
        var root = body.RootElement;
        root.GetProperty("error").GetString().Should().Be("merge_across_companies");
        root.GetProperty("code").GetString().Should().Be("merge_across_companies");
        root.GetProperty("message").GetString().Should().Be("These two records belong to different companies. A merge only combines duplicate entries of the same person within one company; moving someone between companies is not a merge.");
        root.GetProperty("message").GetString().Should().NotContainEquivalentOf("transfer", "the same-group transfer feature does not exist yet");
        root.GetProperty("messageAr").GetString().Should().MatchRegex("[\\u0600-\\u06FF]");
        (await IsDeletedAsync(t.Duplicate.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Merge_WhenOnlyOneRecordHasACompany_IsRefused_BecauseTheyCannotBeShownToBeTheSameCompany()
    {
        var t = await SeedTwinsAsync();
        await SetCompaniesAsync(t, null, t.CompanyId);

        var conflict = Assert.IsAssignableFrom<ObjectResult>(await MergeAsync(t));

        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        System.Text.Json.JsonSerializer.Serialize(conflict.Value).Should().Contain("merge_across_companies");
        (await IsDeletedAsync(t.Duplicate.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Merge_WhenNeitherRecordHasACompany_StillMerges()
    {
        var t = await SeedTwinsAsync();
        await SetCompaniesAsync(t, null, null);

        Assert.IsType<OkObjectResult>(await MergeAsync(t));
        (await IsDeletedAsync(t.Duplicate.Id)).Should().BeTrue();
    }

    // ── 5. Unmerge needs the same permission as the merge, and only undoes what the merge did ───────

    private async Task MarkMergedAsync(Twins t)
    {
        await using var db = _fx.CreateDb();
        var dup = await db.Employees.SingleAsync(e => e.Id == t.Duplicate.Id);
        dup.IsDeleted = true;
        dup.DuplicateOfEmployeeId = t.Survivor.Id;
        dup.Status = "Inactive";
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Unmerge_WithoutTheDeletePermission_Is403WithACodeAndBothLanguages_AndRestoresNothing()
    {
        var t = await SeedTwinsAsync();
        await MarkMergedAsync(t);

        IActionResult result;
        await using (var db = _fx.CreateDb())
            result = await Controller(db, t, mayDelete: false).ResolveDuplicate(t.Duplicate.Id, new ResolveDuplicateRequest("unmerge", null, "wrong twins"), CancellationToken.None);

        var forbidden = Assert.IsAssignableFrom<ObjectResult>(result);
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        using var body = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(forbidden.Value));
        body.RootElement.GetProperty("error").GetString().Should().Be("unmerge_requires_delete_permission");
        body.RootElement.GetProperty("code").GetString().Should().Be("unmerge_requires_delete_permission");
        body.RootElement.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.GetProperty("messageAr").GetString().Should().MatchRegex("[\\u0600-\\u06FF]");
        body.RootElement.GetProperty("requiredPermission").GetString().Should().Be("employees.delete");
        (await IsDeletedAsync(t.Duplicate.Id)).Should().BeTrue("a refused unmerge restores nothing");
    }

    [Fact]
    public async Task Unmerge_WithTheDeletePermission_RestoresTheRecord()
    {
        var t = await SeedTwinsAsync();
        await MarkMergedAsync(t);

        await using var db = _fx.CreateDb();
        var result = await Controller(db, t).ResolveDuplicate(t.Duplicate.Id, new ResolveDuplicateRequest("unmerge", null, "wrong twins"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        (await IsDeletedAsync(t.Duplicate.Id)).Should().BeFalse();
    }

    private async Task<Dictionary<string, bool>> UnmergeWithStructuresAsync(Twins t, int mergeDeactivated, params (string Name, int EffectiveYear)[] inactiveStructures)
    {
        await MarkMergedAsync(t);
        await using (var seed = _fx.CreateDb())
        {
            foreach (var (name, year) in inactiveStructures)
                seed.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
                {
                    TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = 1000m * year, Currency = name,
                    EffectiveDate = new DateOnly(year, 1, 1), IsActive = false,
                });
            if (mergeDeactivated > 0)
                seed.AuditLogs.Add(new AuditLog
                {
                    TenantId = t.TenantId, Action = "employee.payroll_footprint_deactivated", EntityName = "Employee", EntityId = t.Duplicate.Id.ToString(),
                    Metadata = System.Text.Json.JsonSerializer.Serialize(new { reason = "merged_duplicate", salaryDeactivated = mergeDeactivated, profileDeactivated = 0 }),
                });
            await seed.SaveChangesAsync();
        }

        await using (var db = _fx.CreateDb())
            Assert.IsType<OkObjectResult>(await Controller(db, t).ResolveDuplicate(t.Duplicate.Id, new ResolveDuplicateRequest("unmerge", null, "wrong twins"), CancellationToken.None));

        await using var read = _fx.CreateDb();
        return await read.EmployeeSalaryStructures.IgnoreQueryFilters().Where(x => x.EmployeeId == t.Duplicate.Id).ToDictionaryAsync(x => x.Currency, x => x.IsActive);
    }

    [Fact]
    public async Task Unmerge_DoesNotReactivate_ASalaryStructureThatWasAlreadyInactiveBeforeTheMerge()
    {
        var t = await SeedTwinsAsync();

        // The merge deactivated nothing (no footprint audit row): the only structure is an old, closed one and it must stay closed.
        var states = await UnmergeWithStructuresAsync(t, mergeDeactivated: 0, ("closed-2021", 2021));

        states["closed-2021"].Should().BeFalse();
    }

    [Fact]
    public async Task Unmerge_ReactivatesOnlyAsManyStructuresAsTheMergeDeactivated_LatestFirst()
    {
        var t = await SeedTwinsAsync();

        // The merge deactivated exactly one structure; an older closed one existed before and must stay closed.
        var states = await UnmergeWithStructuresAsync(t, mergeDeactivated: 1, ("closed-2021", 2021), ("was-active-2024", 2024));

        states["was-active-2024"].Should().BeTrue("the merge deactivated it, so unmerge puts it back");
        states["closed-2021"].Should().BeFalse("it was already inactive before the merge");
    }
}

file sealed class MergePgHijri : Zayra.Api.Infrastructure.Localization.IHijriDateService
{
    public Zayra.Api.Infrastructure.Localization.DateConversionDto FromGregorian(DateOnly date) =>
        new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}

file sealed class MergePgLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class FailingMergeAudit(IAuditService inner) : IAuditService
{
    public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken)
        => action == "employee.merged_into_existing" ? throw new InvalidOperationException("audit unavailable") : inner.WriteAsync(action, entityName, entityId, context, metadata, cancellationToken);

    public void Stage(string action, string entityName, string? entityId, RequestContext context, string? metadata)
    {
        if (action == "employee.merged_into_existing") throw new InvalidOperationException("audit unavailable");
        inner.Stage(action, entityName, entityId, context, metadata);
    }

    public void DiscardStaged() => inner.DiscardStaged();
}
