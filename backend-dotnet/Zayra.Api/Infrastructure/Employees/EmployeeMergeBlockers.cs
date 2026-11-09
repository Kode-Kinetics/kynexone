using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>One kind of record the duplicate owns that a merge would leave behind on the soft-deleted row.</summary>
public sealed record MergeBlocker(string Type, string Label, string LabelAr, int Count);

/// <summary>How a table that points at an employee is treated by a merge.</summary>
public sealed record MergeEntityRule(string Entity, string? BlockerType, string? IgnorableReason)
{
    public bool IsBlocker => BlockerType is not null;
}

/// <summary>
/// A merge (<c>resolve-duplicate</c> with <c>merge</c>) soft-deletes the duplicate and MOVES NOTHING to the kept employee. A money,
/// compliance or routing record keyed to the duplicate would be orphaned on a hidden row (an open loan nobody deducts, an approved
/// bonus nobody pays, a receivable nobody recovers, a manager who no longer exists), so the merge refuses and names what is in the way.
///
/// THE REGISTRY. Every entity in the EF model that carries an <c>EmployeeId</c> or <c>EmployeeIntId</c> is classified here as a
/// BLOCKER (it has a counter below and a type/label) or IGNORABLE (with a one-line reason). A test walks the EF model and fails when
/// an entity is not classified, so a new table cannot slip through unnoticed. This is read-only and runs before any write.
///
/// Only a row that carries something counts: an empty payroll profile, a zero balance, a settled loan, a paid bonus, a rejected request
/// or a cancelled settlement is not a record of money or compliance and does not block.
/// The scope filters are dropped on purpose (the tenant is pinned): a record in a company the caller cannot see must still block.
/// </summary>
public static class EmployeeMergeBlockers
{
    private const string Why = "Merge pre-check: every record the duplicate owns must be seen whatever company the caller is scoped to; the tenant is pinned.";

    /// <summary>The employee being merged away: both of its keys, because some tables key on the public id.</summary>
    private sealed record Target(Guid TenantId, int Id, Guid PublicId, DateOnly Today);

    private sealed record BlockerType(string Key, string Label, string LabelAr);

    /// <summary>One counted group: how many rows of one blocker type an entity contributes. Many of these become ONE UNION ALL statement.</summary>
    public sealed class CountRow
    {
        public string Type { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    private delegate IQueryable<CountRow> Counter(ZayraDbContext db, Target t);

    private static IQueryable<CountRow> R<T>(string type, IQueryable<T> rows) =>
        rows.GroupBy(_ => 1).Select(g => new CountRow { Type = type, Count = g.Count() });

    // ── Blocker types (the labels the 409 carries) ─────────────────────────────────────────────────
    private static readonly BlockerType[] Types =
    [
        new("open_loans", "Open loans", "قروض مفتوحة"),
        new("open_advances", "Open salary advances", "سلف رواتب مفتوحة"),
        new("unpaid_bonuses", "Bonuses not yet paid", "مكافآت لم تُصرف بعد"),
        new("salary_structures", "Salary structure assignments", "تعيينات هيكل الراتب"),
        new("payroll_profile", "Bank or social-insurance details", "بيانات البنك أو التأمينات الاجتماعية"),
        new("payroll_slips", "Payslips and payroll run lines", "قسائم الرواتب وبنود مسيرات الرواتب"),
        new("leave_balances", "Leave balances", "أرصدة الإجازات"),
        new("leave_requests", "Leave requests and balance adjustments", "طلبات الإجازة وتعديلات الرصيد"),
        new("benefit_enrollments", "Benefit enrolments", "الاشتراكات في المزايا"),
        new("eosb_calculations", "End-of-service calculations", "حسابات نهاية الخدمة"),
        new("documents", "Employee documents and issued letters", "مستندات الموظف والخطابات الصادرة"),
        new("compliance_records", "Identity and compliance records", "سجلات الهوية والامتثال"),
        new("payroll_receivables", "Amounts the employee owes the company", "مبالغ مستحقة على الموظف للشركة"),
        new("payroll_arrears", "Pay arrears", "متأخرات الراتب"),
        new("final_settlements", "Final settlements", "التسويات النهائية"),
        new("opening_balances", "Opening balances", "الأرصدة الافتتاحية"),
        new("eosb_provisions", "End-of-service provisions", "مخصصات نهاية الخدمة"),
        new("payment_records", "Bank payment and wage-file records", "سجلات الدفع البنكي وملفات الأجور"),
        new("payroll_adjustments", "Payroll adjustments, allowances and benefit contributions", "تسويات الراتب والبدلات واشتراكات المزايا"),
        new("unpaid_overtime", "Overtime not yet paid", "ساعات إضافية لم تُصرف بعد"),
        new("leave_encashments", "Leave encashment requests", "طلبات صرف الإجازات نقداً"),
        new("comp_off_credits", "Compensatory-leave credits", "أرصدة الإجازة التعويضية"),
        new("salary_change_requests", "Pending salary changes", "تغييرات الراتب المعلقة"),
        new("live_separations", "Open separation (offboarding) records", "سجلات إنهاء الخدمة المفتوحة"),
        new("pending_payroll_impacts", "Attendance and leave effects on pay not yet applied", "آثار الحضور والإجازات على الراتب غير المطبقة بعد"),
        new("contracts_and_permits", "Contracts, passports, visas and work permits", "العقود وجوازات السفر والتأشيرات وتصاريح العمل"),
        new("dependents", "Dependents", "المعالون"),
        new("transfer_routing", "Open transfers of other people that name this employee as manager", "طلبات نقل مفتوحة لأشخاص آخرين تذكر هذا الموظف كمدير"),
        new("approval_routing", "Approval steps that route to this employee", "خطوات اعتماد توجه إلى هذا الموظف"),
        new("merit_worksheets", "Open merit worksheets managed by this employee", "أوراق عمل الجدارة المفتوحة التي يديرها هذا الموظف"),
        new("acquired_rights", "Acquired-rights entitlements", "الاستحقاقات المكتسبة"),
        new("approval_authority", "Approval authority and delegations", "صلاحيات الاعتماد والتفويضات"),
        new("direct_reports", "People who report to this employee", "أشخاص يتبعون هذا الموظف"),
        new("reporting_lines", "Reporting lines managed by this employee", "خطوط التقارير التي يديرها هذا الموظف"),
        new("department_manager", "Departments managed by this employee", "أقسام يديرها هذا الموظف"),
        new("position_incumbent", "Positions held by this employee", "وظائف يشغلها هذا الموظف"),
    ];

    private static IQueryable<T> Q<T>(ZayraDbContext db, Target t) where T : class, ITenantOwned => ScopedBypass.TenantWide(db.Set<T>(), t.TenantId, Why);

    private static readonly string[] OpenTransferStates = ["PendingCurrentManager", "PendingNewManager", "PendingHrApproval"];
    private static readonly string[] DeadStates = ["Cancelled", "Rejected", "Voided", "Void", "Withdrawn", "Draft"];
    private static readonly string[] SettledStates = ["Cancelled", "Rejected", "Voided", "Void", "Withdrawn", "Draft", "Paid", "Processed", "Applied", "Settled", "Completed", "Used", "Expired", "Closed"];

    // ── Counters: entity name -> how many rows of it carry something ───────────────────────────────
    private static readonly Dictionary<string, Counter> Counters = new()
    {
        [nameof(EmployeeLoan)] = (db, t) => R(TypeOf(nameof(EmployeeLoan))!, Q<EmployeeLoan>(db, t).Where(x => !x.IsDeleted && x.Status != "Settled" && x.Status != "Cancelled" && x.Status != "Rejected" && x.Status != "Closed"
            && !((x.Status == "Active" || x.Status == "Overdue") && x.OutstandingBalance <= 0)
            && (x.EmployeeIntId == t.Id || (x.EmployeeIntId == null && x.EmployeeId == t.PublicId)))),
        [nameof(SalaryAdvance)] = (db, t) => R(TypeOf(nameof(SalaryAdvance))!, Q<SalaryAdvance>(db, t).Where(x => !x.IsDeleted && x.Status != "Settled" && x.Status != "Rejected" && x.Status != "Closed"
            && !((x.Status == "Active" || x.Status == "Overdue") && x.OutstandingBalance <= 0)
            && (x.EmployeeIntId == t.Id || (x.EmployeeIntId == null && x.EmployeeId == t.PublicId)))),
        [nameof(EmployeeBonus)] = (db, t) => R(TypeOf(nameof(EmployeeBonus))!, Q<EmployeeBonus>(db, t).Where(x => !x.IsDeleted && (x.Status == "Draft" || x.Status == "Approved")
            && (x.EmployeeIntId == t.Id || (x.EmployeeIntId == null && x.EmployeeId == t.PublicId)))),
        [nameof(EmployeeSalaryStructure)] = (db, t) => R(TypeOf(nameof(EmployeeSalaryStructure))!, Q<EmployeeSalaryStructure>(db, t).Where(x => x.EmployeeId == t.Id)),
        [nameof(EmployeePayrollProfile)] = (db, t) => R(TypeOf(nameof(EmployeePayrollProfile))!, Q<EmployeePayrollProfile>(db, t).Where(x => x.EmployeeId == t.Id && !x.IsDeleted
            && (x.Iban != "" || x.AccountNumber != "" || x.BankName != "" || x.SocialInsuranceReference != "" || x.MolId != ""))),
        [nameof(PayrollSlip)] = (db, t) => R(TypeOf(nameof(PayrollSlip))!, Q<PayrollSlip>(db, t).Where(x => x.EmployeeId == t.Id && x.Status != "Voided")),
        [nameof(Payslip)] = (db, t) => R(TypeOf(nameof(Payslip))!, Q<Payslip>(db, t).Where(x => x.EmployeeId == t.Id)),
        [nameof(PayrollRunEmployee)] = (db, t) => R(TypeOf(nameof(PayrollRunEmployee))!, Q<PayrollRunEmployee>(db, t).Where(x => x.EmployeeId == t.Id && (x.GrossEarnings != 0 || x.NetPay != 0 || x.TotalDeductions != 0))),
        [nameof(EmployeeLeaveBalance)] = (db, t) => R(TypeOf(nameof(EmployeeLeaveBalance))!, Q<EmployeeLeaveBalance>(db, t).Where(x => x.EmployeeId == t.Id
            && (x.Entitled != 0 || x.Accrued != 0 || x.Used != 0 || x.Pending != 0 || x.CarriedForward != 0 || x.Encashed != 0 || x.ManualAdjustment != 0))),
        [nameof(LeaveRequest)] = (db, t) => R(TypeOf(nameof(LeaveRequest))!, Q<LeaveRequest>(db, t).Where(x => x.EmployeeId == t.Id && x.Status != "Draft" && x.Status != "Rejected" && x.Status != "Cancelled")),
        [nameof(LeaveBalanceAdjustmentRequest)] = (db, t) => R(TypeOf(nameof(LeaveBalanceAdjustmentRequest))!, Q<LeaveBalanceAdjustmentRequest>(db, t).Where(x => x.EmployeeId == t.Id && x.Amount != 0 && !DeadStates.Contains(x.Status) && !SettledStates.Contains(x.Status))),
        [nameof(BenefitEnrollment)] = (db, t) => R(TypeOf(nameof(BenefitEnrollment))!, Q<BenefitEnrollment>(db, t).Where(x => x.EmployeeId == t.Id && x.Status == "Active")),
        [nameof(EOSBCalculation)] = (db, t) => R(TypeOf(nameof(EOSBCalculation))!, Q<EOSBCalculation>(db, t).Where(x => x.EmployeeId == t.Id)),
        [nameof(EmployeeDocument)] = (db, t) => R(TypeOf(nameof(EmployeeDocument))!, Q<EmployeeDocument>(db, t).Where(x => x.EmployeeId == t.Id && !x.IsDeleted)),
        [nameof(IssuedLetter)] = (db, t) => R(TypeOf(nameof(IssuedLetter))!, Q<IssuedLetter>(db, t).Where(x => x.EmployeeId == t.Id && !x.IsDeleted)),
        [nameof(EmployeeComplianceRecord)] = (db, t) => R(TypeOf(nameof(EmployeeComplianceRecord))!, Q<EmployeeComplianceRecord>(db, t).Where(x => x.EmployeeId == t.Id && !x.IsDeleted
            && (x.FieldValue != "" || x.IssueDate != null || x.ExpiryDate != null))),
        [nameof(PayrollEmployeeReceivable)] = (db, t) => R(TypeOf(nameof(PayrollEmployeeReceivable))!, Q<PayrollEmployeeReceivable>(db, t).Where(x => x.EmployeeId == t.Id && x.Status != PayrollReceivableStatuses.Recovered && x.Amount > x.RecoveredAmount)),
        [nameof(PayrollArrearsLine)] = (db, t) => R(TypeOf(nameof(PayrollArrearsLine))!, Q<PayrollArrearsLine>(db, t).Where(x => x.EmployeeId == t.Id && x.Status != PayrollArrearsStatuses.Settled && x.Status != PayrollArrearsStatuses.Voided && x.Amount != 0)),
        [nameof(EmployeeFinalSettlement)] = (db, t) => R(TypeOf(nameof(EmployeeFinalSettlement))!, Q<EmployeeFinalSettlement>(db, t).Where(x => x.EmployeeId == t.Id && x.Status != FinalSettlementStatuses.Cancelled)),
        [nameof(PayrollOpeningBalance)] = (db, t) => R(TypeOf(nameof(PayrollOpeningBalance))!, Q<PayrollOpeningBalance>(db, t).Where(x => x.EmployeeId == t.Id && x.Amount != 0)),
        [nameof(EmployeeEosbOpeningBalance)] = (db, t) => R(TypeOf(nameof(EmployeeEosbOpeningBalance))!, Q<EmployeeEosbOpeningBalance>(db, t).Where(x => x.EmployeeId == t.Id && x.AccruedAmount != 0)),
        [nameof(EosbProvisionSnapshot)] = (db, t) => R(TypeOf(nameof(EosbProvisionSnapshot))!, Q<EosbProvisionSnapshot>(db, t).Where(x => x.EmployeeId == t.Id)),
        [nameof(PayrollPaymentRecord)] = (db, t) => R(TypeOf(nameof(PayrollPaymentRecord))!, Q<PayrollPaymentRecord>(db, t).Where(x => x.EmployeeId == t.Id && x.Amount != 0)),
        [nameof(BankPaymentConfirmation)] = (db, t) => R(TypeOf(nameof(BankPaymentConfirmation))!, Q<BankPaymentConfirmation>(db, t).Where(x => x.EmployeeId == t.Id && x.ConfirmedAmount != 0)),
        [nameof(SIFFileRecord)] = (db, t) => R(TypeOf(nameof(SIFFileRecord))!, Q<SIFFileRecord>(db, t).Where(x => x.EmployeeId == t.Id && x.NetPay != 0)),
        [nameof(PayrollAdjustment)] = (db, t) => R(TypeOf(nameof(PayrollAdjustment))!, Q<PayrollAdjustment>(db, t).Where(x => x.EmployeeId == t.Id && x.Amount != 0 && !DeadStates.Contains(x.Status))),
        [nameof(PayrollAllowance)] = (db, t) => R(TypeOf(nameof(PayrollAllowance))!, Q<PayrollAllowance>(db, t).Where(x => x.EmployeeId == t.Id && x.Amount != 0)),
        [nameof(BenefitContribution)] = (db, t) => R(TypeOf(nameof(BenefitContribution))!, Q<BenefitContribution>(db, t).Where(x => x.EmployeeId == t.Id && (x.EmployeeAmount != 0 || x.EmployerAmount != 0))),
        [nameof(OvertimeRequest)] = (db, t) => R(TypeOf(nameof(OvertimeRequest))!, Q<OvertimeRequest>(db, t).Where(x => x.EmployeeId == t.Id && !SettledStates.Contains(x.Status))),
        [nameof(OvertimePayrollImpact)] = (db, t) => R(TypeOf(nameof(OvertimePayrollImpact))!, Q<OvertimePayrollImpact>(db, t).Where(x => x.EmployeeId == t.Id && x.Amount != 0 && !SettledStates.Contains(x.Status))),
        [nameof(LeaveEncashmentRequest)] = (db, t) => R(TypeOf(nameof(LeaveEncashmentRequest))!, Q<LeaveEncashmentRequest>(db, t).Where(x => x.EmployeeId == t.Id && x.Status != LeaveEncashmentStatuses.Rejected && x.Status != LeaveEncashmentStatuses.Voided && x.Status != LeaveEncashmentStatuses.Processed && x.Status != "Cancelled")),
        [nameof(CompOffCredit)] = (db, t) => R(TypeOf(nameof(CompOffCredit))!, Q<CompOffCredit>(db, t).Where(x => x.EmployeeId == t.Id && x.DaysEarned != 0 && !SettledStates.Contains(x.Status))),
        [nameof(SalaryChangeRequest)] = (db, t) => R(TypeOf(nameof(SalaryChangeRequest))!, Q<SalaryChangeRequest>(db, t).Where(x => x.EmployeeId == t.Id && !SettledStates.Contains(x.Status) && !DeadStates.Contains(x.Status) && x.Status != "Approved")),
        [nameof(EmployeeOffboarding)] = (db, t) => R(TypeOf(nameof(EmployeeOffboarding))!, Q<EmployeeOffboarding>(db, t).Where(x => x.EmployeeId == t.Id && x.Status != "Cancelled" && x.Status != "Completed")),
        [nameof(AttendancePayrollImpact)] = (db, t) => R(TypeOf(nameof(AttendancePayrollImpact))!, Q<AttendancePayrollImpact>(db, t).Where(x => x.EmployeeId == t.Id && !SettledStates.Contains(x.Status))),
        [nameof(LeavePayrollImpact)] = (db, t) => R(TypeOf(nameof(LeavePayrollImpact))!, Q<LeavePayrollImpact>(db, t).Where(x => x.EmployeeId == t.Id && x.Amount != 0 && !SettledStates.Contains(x.Status))),
        [nameof(VisaRecord)] = (db, t) => R(TypeOf(nameof(VisaRecord))!, Q<VisaRecord>(db, t).Where(x => x.EmployeeId == t.PublicId && !x.IsDeleted)),
        [nameof(PassportRecord)] = (db, t) => R(TypeOf(nameof(PassportRecord))!, Q<PassportRecord>(db, t).Where(x => x.EmployeeId == t.PublicId && !x.IsDeleted)),
        [nameof(WorkPermitRecord)] = (db, t) => R(TypeOf(nameof(WorkPermitRecord))!, Q<WorkPermitRecord>(db, t).Where(x => x.EmployeeId == t.PublicId && !x.IsDeleted)),
        [nameof(EmployeeContract)] = (db, t) => R(TypeOf(nameof(EmployeeContract))!, Q<EmployeeContract>(db, t).Where(x => x.EmployeeId == t.PublicId && !x.IsDeleted
            && x.Status != "Expired" && x.Status != "Terminated" && x.Status != "Superseded")),
        [nameof(EmployeeDependent)] = (db, t) => R(TypeOf(nameof(EmployeeDependent))!, Q<EmployeeDependent>(db, t).Where(x => x.EmployeeId == t.Id && !x.IsDeleted)),
        [nameof(OvertimeAdjustment)] = (db, t) => R(TypeOf(nameof(OvertimeAdjustment))!, Q<OvertimeAdjustment>(db, t).Where(x => x.EmployeeId == t.Id && x.AmountAdjustment != 0 && !SettledStates.Contains(x.Status))),
        [nameof(OvertimeCompOffConversion)] = (db, t) => R(TypeOf(nameof(OvertimeCompOffConversion))!, Q<OvertimeCompOffConversion>(db, t).Where(x => x.EmployeeId == t.Id && (x.Status == "Pending" || x.Status == "Approved"))),
        // The in-effect acquired-rights package (Art. 59). The contract counter alone is not enough: the database lets a row sit on a contract that later
        // becomes Expired or Terminated, which the contract counter does not count.
        [nameof(EmployeeEntitlement)] = (db, t) => R(TypeOf(nameof(EmployeeEntitlement))!, Q<EmployeeEntitlement>(db, t).Where(x => x.EmployeeId == t.PublicId && (x.EffectiveTo == null || x.EffectiveTo >= t.Today))),
        // People, lines, departments, positions and delegations that point AT the duplicate as their manager, approver or incumbent.
        // Keyed "Entity.Property" because the column is not named EmployeeId.
        ["Employee.ManagerEmployeeId"] = (db, t) => R(TypeOf("Employee.ManagerEmployeeId")!, ScopedBypass.NullableTenantWide(db.Employees, t.TenantId, Why).Where(x => !x.IsDeleted && x.Id != t.Id && x.ManagerEmployeeId == t.Id)),
        ["Employee.SecondLevelManagerEmployeeId"] = (db, t) => R(TypeOf("Employee.SecondLevelManagerEmployeeId")!, ScopedBypass.NullableTenantWide(db.Employees, t.TenantId, Why).Where(x => !x.IsDeleted && x.Id != t.Id && x.SecondLevelManagerEmployeeId == t.Id)),
        ["Employee.SupervisorEmployeeId"] = (db, t) => R(TypeOf("Employee.SupervisorEmployeeId")!, ScopedBypass.NullableTenantWide(db.Employees, t.TenantId, Why).Where(x => !x.IsDeleted && x.Id != t.Id && x.SupervisorEmployeeId == t.Id)),
        ["Employee.HRBusinessPartnerEmployeeId"] = (db, t) => R(TypeOf("Employee.HRBusinessPartnerEmployeeId")!, ScopedBypass.NullableTenantWide(db.Employees, t.TenantId, Why).Where(x => !x.IsDeleted && x.Id != t.Id && x.HRBusinessPartnerEmployeeId == t.Id)),
        ["ReportingLine.ManagerEmployeeId"] = (db, t) => R(TypeOf("ReportingLine.ManagerEmployeeId")!, Q<ReportingLine>(db, t).Where(x => x.ManagerEmployeeId == t.Id && x.IsActive && x.EmployeeId != t.Id)),
        ["Department.ManagerEmployeeId"] = (db, t) => R(TypeOf("Department.ManagerEmployeeId")!, Q<Department>(db, t).Where(x => x.ManagerEmployeeId == t.Id && !x.IsDeleted)),
        ["Position.IncumbentEmployeeId"] = (db, t) => R(TypeOf("Position.IncumbentEmployeeId")!, Q<Position>(db, t).Where(x => x.IncumbentEmployeeId == t.Id && !x.IsDeleted)),
        ["ApprovalDelegation.FromEmployeeId"] = (db, t) => R(TypeOf("ApprovalDelegation.FromEmployeeId")!, Q<ApprovalDelegation>(db, t).Where(x => x.FromEmployeeId == t.Id && x.Status == "Active" && x.EndDate >= t.Today)),
        ["ApprovalDelegation.ToEmployeeId"] = (db, t) => R(TypeOf("ApprovalDelegation.ToEmployeeId")!, Q<ApprovalDelegation>(db, t).Where(x => x.ToEmployeeId == t.Id && x.Status == "Active" && x.EndDate >= t.Today)),
        ["LeaveDelegation.DelegateEmployeeId"] = (db, t) => R(TypeOf("LeaveDelegation.DelegateEmployeeId")!, Q<LeaveDelegation>(db, t).Where(x => x.DelegateEmployeeId == t.Id && x.Status == "Active" && x.EndDate >= t.Today)),
        // An open transfer of ANOTHER employee names the duplicate as the manager whose approval it waits for, and on approval installs it as the new manager.
        ["EmployeeTransferRequest.CurrentManagerEmployeeId"] = (db, t) => R(TypeOf("EmployeeTransferRequest.CurrentManagerEmployeeId")!, Q<EmployeeTransferRequest>(db, t).Where(x => x.EmployeeId != t.Id && x.CurrentManagerEmployeeId == t.Id && OpenTransferStates.Contains(x.Status))),
        ["EmployeeTransferRequest.NewManagerEmployeeId"] = (db, t) => R(TypeOf("EmployeeTransferRequest.NewManagerEmployeeId")!, Q<EmployeeTransferRequest>(db, t).Where(x => x.EmployeeId != t.Id && x.NewManagerEmployeeId == t.Id && OpenTransferStates.Contains(x.Status))),
        ["ApprovalPolicyStep.SpecificEmployeeId"] = (db, t) => R(TypeOf("ApprovalPolicyStep.SpecificEmployeeId")!, Q<ApprovalPolicyStep>(db, t).Where(x => x.SpecificEmployeeId == t.Id)),
        ["ApprovalWorkflowStep.SpecificEmployeeId"] = (db, t) => R(TypeOf("ApprovalWorkflowStep.SpecificEmployeeId")!, Q<ApprovalWorkflowStep>(db, t).Where(x => x.SpecificEmployeeId == t.Id)),
        ["CompWorksheet.ManagerEmployeeId"] = (db, t) => R(TypeOf("CompWorksheet.ManagerEmployeeId")!, Q<CompWorksheet>(db, t).Where(x => x.ManagerEmployeeId == t.Id && (x.Status == "Draft" || x.Status == "Submitted" || x.Status == "Returned"))),
        [nameof(ApprovalAuthority)] = (db, t) => R(TypeOf(nameof(ApprovalAuthority))!, Q<ApprovalAuthority>(db, t).Where(x => x.EmployeeId == t.Id && x.IsActive)),
    };

    // ── The registry: every EF entity with an EmployeeId / EmployeeIntId, classified ───────────────
    private static string? TypeOf(string entity) => entity switch
    {
        nameof(EmployeeLoan) => "open_loans",
        nameof(SalaryAdvance) => "open_advances",
        nameof(EmployeeBonus) => "unpaid_bonuses",
        nameof(EmployeeSalaryStructure) => "salary_structures",
        nameof(EmployeePayrollProfile) => "payroll_profile",
        nameof(PayrollSlip) or nameof(Payslip) or nameof(PayrollRunEmployee) => "payroll_slips",
        nameof(EmployeeLeaveBalance) => "leave_balances",
        nameof(LeaveRequest) or nameof(LeaveBalanceAdjustmentRequest) => "leave_requests",
        nameof(BenefitEnrollment) => "benefit_enrollments",
        nameof(EOSBCalculation) => "eosb_calculations",
        nameof(EmployeeDocument) or nameof(IssuedLetter) => "documents",
        nameof(EmployeeComplianceRecord) => "compliance_records",
        nameof(PayrollEmployeeReceivable) => "payroll_receivables",
        nameof(PayrollArrearsLine) => "payroll_arrears",
        nameof(EmployeeFinalSettlement) => "final_settlements",
        nameof(PayrollOpeningBalance) or nameof(EmployeeEosbOpeningBalance) => "opening_balances",
        nameof(EosbProvisionSnapshot) => "eosb_provisions",
        nameof(PayrollPaymentRecord) or nameof(BankPaymentConfirmation) or nameof(SIFFileRecord) => "payment_records",
        nameof(PayrollAdjustment) or nameof(PayrollAllowance) or nameof(BenefitContribution) => "payroll_adjustments",
        nameof(OvertimeRequest) or nameof(OvertimePayrollImpact) or nameof(OvertimeAdjustment) => "unpaid_overtime",
        nameof(LeaveEncashmentRequest) => "leave_encashments",
        nameof(CompOffCredit) or nameof(OvertimeCompOffConversion) => "comp_off_credits",
        nameof(EmployeeEntitlement) => "acquired_rights",
        nameof(SalaryChangeRequest) => "salary_change_requests",
        nameof(EmployeeOffboarding) => "live_separations",
        nameof(AttendancePayrollImpact) or nameof(LeavePayrollImpact) => "pending_payroll_impacts",
        nameof(VisaRecord) or nameof(PassportRecord) or nameof(WorkPermitRecord) or nameof(EmployeeContract) => "contracts_and_permits",
        nameof(EmployeeDependent) => "dependents",
        nameof(ApprovalAuthority) => "approval_authority",
        "Employee.ManagerEmployeeId" or "Employee.SecondLevelManagerEmployeeId" or "Employee.SupervisorEmployeeId" or "Employee.HRBusinessPartnerEmployeeId" => "direct_reports",
        "ReportingLine.ManagerEmployeeId" => "reporting_lines",
        "Department.ManagerEmployeeId" => "department_manager",
        "Position.IncumbentEmployeeId" => "position_incumbent",
        "ApprovalDelegation.FromEmployeeId" or "ApprovalDelegation.ToEmployeeId" or "LeaveDelegation.DelegateEmployeeId" => "approval_authority",
        "ApprovalPolicyStep.SpecificEmployeeId" or "ApprovalWorkflowStep.SpecificEmployeeId" => "approval_routing",
        "CompWorksheet.ManagerEmployeeId" => "merit_worksheets",
        "EmployeeTransferRequest.CurrentManagerEmployeeId" or "EmployeeTransferRequest.NewManagerEmployeeId" => "transfer_routing",
        _ => null,
    };

    private static readonly (string Reason, string[] Entities)[] IgnorableGroups =
    [
        ("AI/analytics output about the person, derived and re-creatable, carries no money or compliance fact",
            ["AIHRQueryCache", "AIHRQueryLog", "AIInsight", "AIRecommendation", "AttendanceAIInsight", "BurnoutRiskSignal", "EmployeeChurnPrediction", "EmployeeRiskScore",
             "PayrollAIValidationResult", "ComplianceAIInsight", "LeaveAIInsight.AffectedEmployeeId"]),
        ("attendance/time history; the pay effect is carried by AttendancePayrollImpact and OvertimePayrollImpact, which block",
            ["AttendanceDailyRecord", "AttendanceEvidence", "AttendanceException", "AttendanceRawEvent", "AttendanceRecord", "AttendanceRegularizationRequest", "AbsenceRecord",
             "AbsenceRegularizationRequest", "Timesheet", "TimesheetDayReconciliation", "TimesheetEntry", "ShiftAssignment"]),
        ("compliance evidence: consent or acknowledgement proof that stays on the retained (soft-deleted) record; the kept employee simply shows none, which fails safe (consent is asked again)",
            ["BiometricConsent", "EmployeePolicyAcknowledgement"]),
        ("performance, learning and talent records: HR development history, no payment or legal record is held here",
            ["AppraisalAppeal", "AppraisalReview", "ContinuousFeedback", "DevelopmentPlan", "EmployeeGoal", "PerformanceCycleEmployee", "PerformanceImprovementPlan", "ProbationReview",
             "SuccessionNomination", "TalentAssessment", "LearningAssignment", "LearningAttempt",
             "LearningCredential", "LearningEnrollment", "LearningLaunchSession", "EngagementSurveyInvitation", "EmployeeSentimentPulse"]),
        ("talent, engagement and development ownership or authorship (owner, mentor, reviewer, assessor, nominator): HR reassigns it; no money or compliance fact",
            ["DevelopmentPlan.OwnerEmployeeId", "DevelopmentPlanAction.MentorEmployeeId", "DevelopmentPlanAction.OwnerEmployeeId", "EngagementAction.OwnerEmployeeId",
             "EngagementSurveyResponse.CohortManagerEmployeeId", "Feedback360.ReviewerEmployeeId", "SuccessionCriticalPosition.AccountableEmployeeId",
             "SuccessionNomination.NominatedByEmployeeId", "TalentAssessment.AssessorEmployeeId", "TalentAssessment.CalibratedByEmployeeId", "TalentPool.OwnerEmployeeId"]),
        ("a recommendation or proposal only: nothing is payable until it becomes a bonus, salary change or structure, which block",
            ["CompWorksheetLine", "PromotionRecommendation", "IncrementRecommendation", "BonusRecommendation"]),
        ("audit, history or access log about the person: the merge writes its own audit row and the history stays readable on the retained record",
            ["EmployeeHistory", "EmployeeStatusHistory", "EmployeeSelfServiceAuditLog", "EmployeePayslipAccessLog", "EmployeeAIQueryLog", "ComplianceAuditLog", "QiwaSyncLog",
             "EnterpriseIdentityProvisioningEvent", "NotificationDelivery", "OpeningBalanceOrigin", "LeaveBalanceTransaction", "PayrollRunConsumption"]),
        ("self-service preferences, notifications and device rows: no money or compliance fact; the login itself is closed by the merge",
            ["ESSDashboardPreference", "EmployeeActionItem", "EmployeeMobileDevice", "EmployeeNotification", "EmployeeNotificationCategoryPreference", "EmployeeNotificationPreference",
             "EmployeeUserAccount", "HRRequest", "HRRequestComment", "EmployeeDocumentRequest", "EmployeeProfileChangeRequest", "OnboardingTask"]),
        ("a pending request the merge cancels, or one that HR can raise again: no payment has been made against it",
            ["EmployeeChangeRequest", "EmployeeImportGap", "LeaveCancellationRequest", "LeaveModificationRequest", "CompOffUsage"]),
        ("the duplicate's OWN transfers: a pending one is withdrawn (compare-and-set) with the other pending approval work when the merge runs, a finished one is history. Other people's open transfers that name the duplicate as a manager block (transfer_routing)",
            ["EmployeeTransferRequest"]),
        ("derived at approval: created in the same SaveChanges as the OvertimePayrollImpact (OvertimeController), which blocks while unpaid",
            ["OvertimeCalculation"]),
        ("payroll run detail or diagnostics: the run line is covered by PayrollSlip/PayrollRunEmployee and the money by the adjustment, allowance and payment-record checks",
            ["PayrollDeduction", "PayrollEarning", "PayrollException", "PayrollRunEmployeeSelection", "PayrollValidationOverride", "PayrollValidationResult", "BenefitPayrollDeductionLink"]),
        ("monitoring derived from other records (renewals, reminders, Nitaqat weights); the renewal case and reminders hang off a contract, which blocks",
            ["ComplianceReminder", "ComplianceRenewal", "ContractRenewalCase", "NitaqatEmployeeWeightOverride"]),
        ("the other side of a delegation or reporting edge: the duplicate as the delegator or subordinate. The delegate / manager side blocks (LeaveDelegation.DelegateEmployeeId, ReportingLine.ManagerEmployeeId)",
            ["ReportingLine", "LeaveDelegation"]),
        ("pending approvals are handled by the merge itself: those assigned to the duplicate are rerouted to the HR Manager queue and those about the duplicate are cancelled (CancelPendingApprovalWorkAsync)",
            ["ApprovalRequest.CurrentApproverEmployeeId", "ApprovalRequest.RequestedForEmployeeId"]),
        ("an earlier duplicate that was already merged into this record keeps its own link; the chain stays readable through the merge audit rows",
            ["Employee.DuplicateOfEmployeeId"]),
        ("names a person on a record owned by someone else (a stand-in on a leave request, a manager on a hire draft, the requester of a headcount request): informational, HR edits it",
            ["LeaveRequest.DelegateEmployeeId", "EmployeeDraft.ManagerEmployeeId", "ManpowerRequisition.RequestedByEmployeeId"]),
    ];

    private static readonly Dictionary<string, MergeEntityRule> RuleMap = BuildRules();

    private static Dictionary<string, MergeEntityRule> BuildRules()
    {
        var map = new Dictionary<string, MergeEntityRule>(StringComparer.Ordinal);
        foreach (var entity in Counters.Keys) map[entity] = new MergeEntityRule(entity, TypeOf(entity) ?? throw new InvalidOperationException($"{entity} has a counter but no blocker type"), null);
        foreach (var (reason, entities) in IgnorableGroups)
            foreach (var entity in entities)
                map[entity] = new MergeEntityRule(entity, null, reason);
        return map;
    }

    /// <summary>Every classified entity: the single registry the model-walking test checks.</summary>
    public static IReadOnlyDictionary<string, MergeEntityRule> Rules => RuleMap;

    /// <summary>The entities (by CLR name) that are not in the registry: what a new employee-keyed table would show up as.</summary>
    public static IReadOnlyList<string> Unclassified(IEnumerable<string> entityNames) =>
        entityNames.Where(n => !RuleMap.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();

    public static IReadOnlyList<string> BlockerTypeKeys => Types.Select(t => t.Key).ToList();

    public static async Task<IReadOnlyList<MergeBlocker>> FindAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct)
    {
        var employee = await ScopedBypass.NullableTenantWide(db.Employees, tenantId, Why).AsNoTracking()
            .Where(e => e.Id == employeeId).Select(e => new { e.PublicId }).SingleAsync(ct);
        return await FindAsync(db, tenantId, employeeId, employee.PublicId, null, ct);
    }

    /// <summary>
    /// ALL the counts in ONE statement (a UNION ALL of per-table counts), so it is a single round trip. <paramref name="onlyTypes"/> narrows
    /// it to a subset (the re-check under the row locks); null counts everything.
    /// </summary>
    public static async Task<IReadOnlyList<MergeBlocker>> FindAsync(
        ZayraDbContext db, Guid tenantId, int employeeId, Guid publicId, IReadOnlySet<string>? onlyTypes, CancellationToken ct)
    {
        var target = new Target(tenantId, employeeId, publicId, DateOnly.FromDateTime(DateTime.UtcNow));
        IQueryable<CountRow>? union = null;
        foreach (var (key, counter) in Counters)
        {
            if (onlyTypes is not null && !onlyTypes.Contains(TypeOf(key)!)) continue;
            var rows = counter(db, target);
            union = union is null ? rows : union.Concat(rows);
        }
        if (union is null) return [];

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in await union.ToListAsync(ct))
            counts[row.Type] = counts.GetValueOrDefault(row.Type) + row.Count;

        return Types.Where(t => counts.ContainsKey(t.Key)).Select(t => new MergeBlocker(t.Key, t.Label, t.LabelAr, counts[t.Key])).ToList();
    }
}
