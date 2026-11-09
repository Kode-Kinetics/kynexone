using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>One kind of record the duplicate owns that a merge would leave behind on the soft-deleted row.</summary>
public sealed record MergeBlocker(string Type, string Label, string LabelAr, int Count);

/// <summary>
/// A merge (<c>resolve-duplicate</c> with <c>merge</c>) soft-deletes the duplicate and MOVES NOTHING to the kept employee. A money or
/// compliance record keyed to the duplicate would be orphaned on a hidden row (an open loan nobody deducts, an approved bonus nobody pays,
/// a salary package, leave days, a bank account), so the merge refuses instead and names what is in the way. This is read-only: it runs
/// before any write so a refusal leaves no partial state.
///
/// Only a row that carries something counts: an empty payroll profile, a compliance row with no value or date, a zero leave balance, a
/// settled loan, a paid bonus, a rejected request or a waived benefit is not a record of money or compliance and does not block.
/// The scope filters are dropped on purpose (the tenant is pinned): a record in a company the caller cannot see must still block.
/// </summary>
public static class EmployeeMergeBlockers
{
    private const string Why = "Merge pre-check: every record the duplicate owns must be seen whatever company the caller is scoped to; the tenant is pinned.";

    public static async Task<IReadOnlyList<MergeBlocker>> FindAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct)
    {
        var found = new List<MergeBlocker>();
        void Add(string type, string label, string labelAr, int count)
        {
            if (count > 0) found.Add(new MergeBlocker(type, label, labelAr, count));
        }

        Add("open_loans", "Open loans", "قروض مفتوحة", await ScopedBypass.TenantWide(db.EmployeeLoans, tenantId, Why)
            .CountAsync(x => x.EmployeeIntId == employeeId && !x.IsDeleted && x.Status != "Settled" && x.Status != "Cancelled" && x.Status != "Rejected", ct));
        Add("open_advances", "Open salary advances", "سلف رواتب مفتوحة", await ScopedBypass.TenantWide(db.SalaryAdvances, tenantId, Why)
            .CountAsync(x => x.EmployeeIntId == employeeId && !x.IsDeleted && x.Status != "Settled" && x.Status != "Rejected", ct));
        Add("unpaid_bonuses", "Bonuses not yet paid", "مكافآت لم تُصرف بعد", await ScopedBypass.TenantWide(db.EmployeeBonuses, tenantId, Why)
            .CountAsync(x => x.EmployeeIntId == employeeId && !x.IsDeleted && (x.Status == "Draft" || x.Status == "Approved"), ct));
        Add("salary_structures", "Salary structure assignments", "تعيينات هيكل الراتب", await ScopedBypass.TenantWide(db.EmployeeSalaryStructures, tenantId, Why)
            .CountAsync(x => x.EmployeeId == employeeId, ct));
        Add("payroll_profile", "Bank or social-insurance details", "بيانات البنك أو التأمينات الاجتماعية", await ScopedBypass.TenantWide(db.EmployeePayrollProfiles, tenantId, Why)
            .CountAsync(x => x.EmployeeId == employeeId && !x.IsDeleted
                && (x.Iban != "" || x.AccountNumber != "" || x.BankName != "" || x.SocialInsuranceReference != "" || x.MolId != ""), ct));
        Add("payroll_slips", "Payslips", "قسائم الرواتب", await ScopedBypass.TenantWide(db.PayrollSlips, tenantId, Why)
            .CountAsync(x => x.EmployeeId == employeeId && x.Status != "Voided", ct));
        Add("leave_balances", "Leave balances", "أرصدة الإجازات", await ScopedBypass.TenantWide(db.EmployeeLeaveBalances, tenantId, Why)
            .CountAsync(x => x.EmployeeId == employeeId
                && (x.Entitled != 0 || x.Accrued != 0 || x.Used != 0 || x.Pending != 0 || x.CarriedForward != 0 || x.Encashed != 0 || x.ManualAdjustment != 0), ct));
        Add("leave_requests", "Leave requests", "طلبات الإجازة", await ScopedBypass.TenantWide(db.LeaveRequests, tenantId, Why)
            .CountAsync(x => x.EmployeeId == employeeId && x.Status != "Draft" && x.Status != "Rejected" && x.Status != "Cancelled", ct));
        Add("benefit_enrollments", "Benefit enrolments", "الاشتراكات في المزايا", await ScopedBypass.TenantWide(db.BenefitEnrollments, tenantId, Why)
            .CountAsync(x => x.EmployeeId == employeeId && x.Status == "Active", ct));
        Add("eosb_calculations", "End-of-service calculations", "حسابات نهاية الخدمة", await ScopedBypass.TenantWide(db.EOSBCalculations, tenantId, Why)
            .CountAsync(x => x.EmployeeId == employeeId, ct));
        Add("documents", "Employee documents", "مستندات الموظف", await ScopedBypass.TenantWide(db.EmployeeDocuments, tenantId, Why)
            .CountAsync(x => x.EmployeeId == employeeId && !x.IsDeleted, ct));
        Add("compliance_records", "Identity and compliance records", "سجلات الهوية والامتثال", await ScopedBypass.TenantWide(db.EmployeeComplianceRecords, tenantId, Why)
            .CountAsync(x => x.EmployeeId == employeeId && !x.IsDeleted
                && (x.FieldValue != "" || x.IssueDate != null || x.ExpiryDate != null), ct));
        return found;
    }
}
