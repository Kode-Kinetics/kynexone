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

    private delegate Task<int> Counter(ZayraDbContext db, Target t, CancellationToken ct);

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
        new("approval_authority", "Approval authority and delegations", "صلاحيات الاعتماد والتفويضات"),
        new("direct_reports", "People who report to this employee", "أشخاص يتبعون هذا الموظف"),
        new("reporting_lines", "Reporting lines managed by this employee", "خطوط التقارير التي يديرها هذا الموظف"),
        new("department_manager", "Departments managed by this employee", "أقسام يديرها هذا الموظف"),
        new("position_incumbent", "Positions held by this employee", "وظائف يشغلها هذا الموظف"),
    ];

    private static IQueryable<T> Q<T>(ZayraDbContext db, Target t) where T : class, ITenantOwned => ScopedBypass.TenantWide(db.Set<T>(), t.TenantId, Why);

    private static readonly string[] DeadStates = ["Cancelled", "Rejected", "Voided", "Void", "Withdrawn", "Draft"];
    private static readonly string[] SettledStates = ["Cancelled", "Rejected", "Voided", "Void", "Withdrawn", "Draft", "Paid", "Processed", "Applied", "Settled", "Completed", "Used", "Expired", "Closed"];

    // ── Counters: entity name -> how many rows of it carry something ───────────────────────────────
    private static readonly Dictionary<string, Counter> Counters = new()
    {
        [nameof(EmployeeLoan)] = (db, t, ct) => Q<EmployeeLoan>(db, t).CountAsync(x => !x.IsDeleted && x.Status != "Settled" && x.Status != "Cancelled" && x.Status != "Rejected"
            && (x.EmployeeIntId == t.Id || (x.EmployeeIntId == null && x.EmployeeId == t.PublicId)), ct),
        [nameof(SalaryAdvance)] = (db, t, ct) => Q<SalaryAdvance>(db, t).CountAsync(x => !x.IsDeleted && x.Status != "Settled" && x.Status != "Rejected"
            && (x.EmployeeIntId == t.Id || (x.EmployeeIntId == null && x.EmployeeId == t.PublicId)), ct),
        [nameof(EmployeeBonus)] = (db, t, ct) => Q<EmployeeBonus>(db, t).CountAsync(x => !x.IsDeleted && (x.Status == "Draft" || x.Status == "Approved")
            && (x.EmployeeIntId == t.Id || (x.EmployeeIntId == null && x.EmployeeId == t.PublicId)), ct),
        [nameof(EmployeeSalaryStructure)] = (db, t, ct) => Q<EmployeeSalaryStructure>(db, t).CountAsync(x => x.EmployeeId == t.Id, ct),
        [nameof(EmployeePayrollProfile)] = (db, t, ct) => Q<EmployeePayrollProfile>(db, t).CountAsync(x => x.EmployeeId == t.Id && !x.IsDeleted
            && (x.Iban != "" || x.AccountNumber != "" || x.BankName != "" || x.SocialInsuranceReference != "" || x.MolId != ""), ct),
        [nameof(PayrollSlip)] = (db, t, ct) => Q<PayrollSlip>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.Status != "Voided", ct),
        [nameof(Payslip)] = (db, t, ct) => Q<Payslip>(db, t).CountAsync(x => x.EmployeeId == t.Id, ct),
        [nameof(PayrollRunEmployee)] = (db, t, ct) => Q<PayrollRunEmployee>(db, t).CountAsync(x => x.EmployeeId == t.Id && (x.GrossEarnings != 0 || x.NetPay != 0 || x.TotalDeductions != 0), ct),
        [nameof(EmployeeLeaveBalance)] = (db, t, ct) => Q<EmployeeLeaveBalance>(db, t).CountAsync(x => x.EmployeeId == t.Id
            && (x.Entitled != 0 || x.Accrued != 0 || x.Used != 0 || x.Pending != 0 || x.CarriedForward != 0 || x.Encashed != 0 || x.ManualAdjustment != 0), ct),
        [nameof(LeaveRequest)] = (db, t, ct) => Q<LeaveRequest>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.Status != "Draft" && x.Status != "Rejected" && x.Status != "Cancelled", ct),
        [nameof(LeaveBalanceAdjustmentRequest)] = (db, t, ct) => Q<LeaveBalanceAdjustmentRequest>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.Amount != 0 && !DeadStates.Contains(x.Status) && !SettledStates.Contains(x.Status), ct),
        [nameof(BenefitEnrollment)] = (db, t, ct) => Q<BenefitEnrollment>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.Status == "Active", ct),
        [nameof(EOSBCalculation)] = (db, t, ct) => Q<EOSBCalculation>(db, t).CountAsync(x => x.EmployeeId == t.Id, ct),
        [nameof(EmployeeDocument)] = (db, t, ct) => Q<EmployeeDocument>(db, t).CountAsync(x => x.EmployeeId == t.Id && !x.IsDeleted, ct),
        [nameof(IssuedLetter)] = (db, t, ct) => Q<IssuedLetter>(db, t).CountAsync(x => x.EmployeeId == t.Id && !x.IsDeleted, ct),
        [nameof(EmployeeComplianceRecord)] = (db, t, ct) => Q<EmployeeComplianceRecord>(db, t).CountAsync(x => x.EmployeeId == t.Id && !x.IsDeleted
            && (x.FieldValue != "" || x.IssueDate != null || x.ExpiryDate != null), ct),
        [nameof(PayrollEmployeeReceivable)] = (db, t, ct) => Q<PayrollEmployeeReceivable>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.Status != PayrollReceivableStatuses.Recovered && x.Amount > x.RecoveredAmount, ct),
        [nameof(PayrollArrearsLine)] = (db, t, ct) => Q<PayrollArrearsLine>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.Status != PayrollArrearsStatuses.Settled && x.Status != PayrollArrearsStatuses.Voided && x.Amount != 0, ct),
        [nameof(EmployeeFinalSettlement)] = (db, t, ct) => Q<EmployeeFinalSettlement>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.Status != FinalSettlementStatuses.Cancelled, ct),
        [nameof(PayrollOpeningBalance)] = (db, t, ct) => Q<PayrollOpeningBalance>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.Amount != 0, ct),
        [nameof(EmployeeEosbOpeningBalance)] = (db, t, ct) => Q<EmployeeEosbOpeningBalance>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.AccruedAmount != 0, ct),
        [nameof(EosbProvisionSnapshot)] = (db, t, ct) => Q<EosbProvisionSnapshot>(db, t).CountAsync(x => x.EmployeeId == t.Id, ct),
        [nameof(PayrollPaymentRecord)] = (db, t, ct) => Q<PayrollPaymentRecord>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.Amount != 0, ct),
        [nameof(BankPaymentConfirmation)] = (db, t, ct) => Q<BankPaymentConfirmation>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.ConfirmedAmount != 0, ct),
        [nameof(SIFFileRecord)] = (db, t, ct) => Q<SIFFileRecord>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.NetPay != 0, ct),
        [nameof(PayrollAdjustment)] = (db, t, ct) => Q<PayrollAdjustment>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.Amount != 0 && !DeadStates.Contains(x.Status), ct),
        [nameof(PayrollAllowance)] = (db, t, ct) => Q<PayrollAllowance>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.Amount != 0, ct),
        [nameof(BenefitContribution)] = (db, t, ct) => Q<BenefitContribution>(db, t).CountAsync(x => x.EmployeeId == t.Id && (x.EmployeeAmount != 0 || x.EmployerAmount != 0), ct),
        [nameof(OvertimeRequest)] = (db, t, ct) => Q<OvertimeRequest>(db, t).CountAsync(x => x.EmployeeId == t.Id && !SettledStates.Contains(x.Status), ct),
        [nameof(OvertimePayrollImpact)] = (db, t, ct) => Q<OvertimePayrollImpact>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.Amount != 0 && !SettledStates.Contains(x.Status), ct),
        [nameof(LeaveEncashmentRequest)] = (db, t, ct) => Q<LeaveEncashmentRequest>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.Status != LeaveEncashmentStatuses.Rejected && x.Status != LeaveEncashmentStatuses.Voided && x.Status != LeaveEncashmentStatuses.Processed && x.Status != "Cancelled", ct),
        [nameof(CompOffCredit)] = (db, t, ct) => Q<CompOffCredit>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.DaysEarned != 0 && !SettledStates.Contains(x.Status), ct),
        [nameof(SalaryChangeRequest)] = (db, t, ct) => Q<SalaryChangeRequest>(db, t).CountAsync(x => x.EmployeeId == t.Id && !SettledStates.Contains(x.Status) && !DeadStates.Contains(x.Status) && x.Status != "Approved", ct),
        [nameof(EmployeeOffboarding)] = (db, t, ct) => Q<EmployeeOffboarding>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.Status != "Cancelled" && x.Status != "Completed", ct),
        [nameof(AttendancePayrollImpact)] = (db, t, ct) => Q<AttendancePayrollImpact>(db, t).CountAsync(x => x.EmployeeId == t.Id && !SettledStates.Contains(x.Status), ct),
        [nameof(LeavePayrollImpact)] = (db, t, ct) => Q<LeavePayrollImpact>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.Amount != 0 && !SettledStates.Contains(x.Status), ct),
        [nameof(VisaRecord)] = (db, t, ct) => Q<VisaRecord>(db, t).CountAsync(x => x.EmployeeId == t.PublicId && !x.IsDeleted, ct),
        [nameof(PassportRecord)] = (db, t, ct) => Q<PassportRecord>(db, t).CountAsync(x => x.EmployeeId == t.PublicId && !x.IsDeleted, ct),
        [nameof(WorkPermitRecord)] = (db, t, ct) => Q<WorkPermitRecord>(db, t).CountAsync(x => x.EmployeeId == t.PublicId && !x.IsDeleted, ct),
        [nameof(EmployeeContract)] = (db, t, ct) => Q<EmployeeContract>(db, t).CountAsync(x => x.EmployeeId == t.PublicId && !x.IsDeleted
            && x.Status != "Expired" && x.Status != "Terminated" && x.Status != "Superseded", ct),
        [nameof(EmployeeDependent)] = (db, t, ct) => Q<EmployeeDependent>(db, t).CountAsync(x => x.EmployeeId == t.Id && !x.IsDeleted, ct),
        [nameof(ApprovalAuthority)] = (db, t, ct) => Q<ApprovalAuthority>(db, t).CountAsync(x => x.EmployeeId == t.Id && x.IsActive, ct),
    };

    /// <summary>Relationship blockers: other live records that point AT the duplicate as their manager, approver or incumbent.</summary>
    private static readonly Dictionary<string, Counter> Relationships = new()
    {
        ["direct_reports"] = (db, t, ct) => ScopedBypass.NullableTenantWide(db.Employees, t.TenantId, Why).CountAsync(x => !x.IsDeleted && x.Id != t.Id
            && (x.ManagerEmployeeId == t.Id || x.SecondLevelManagerEmployeeId == t.Id || x.SupervisorEmployeeId == t.Id || x.HRBusinessPartnerEmployeeId == t.Id), ct),
        ["reporting_lines"] = (db, t, ct) => Q<ReportingLine>(db, t).CountAsync(x => x.ManagerEmployeeId == t.Id && x.IsActive && x.EmployeeId != t.Id, ct),
        ["department_manager"] = (db, t, ct) => Q<Department>(db, t).CountAsync(x => x.ManagerEmployeeId == t.Id && !x.IsDeleted, ct),
        ["position_incumbent"] = (db, t, ct) => Q<Position>(db, t).CountAsync(x => x.IncumbentEmployeeId == t.Id && !x.IsDeleted, ct),
        ["approval_delegations"] = (db, t, ct) => Q<ApprovalDelegation>(db, t).CountAsync(x => (x.FromEmployeeId == t.Id || x.ToEmployeeId == t.Id) && x.Status == "Active" && x.EndDate >= t.Today, ct),
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
        nameof(OvertimeRequest) or nameof(OvertimePayrollImpact) => "unpaid_overtime",
        nameof(LeaveEncashmentRequest) => "leave_encashments",
        nameof(CompOffCredit) => "comp_off_credits",
        nameof(SalaryChangeRequest) => "salary_change_requests",
        nameof(EmployeeOffboarding) => "live_separations",
        nameof(AttendancePayrollImpact) or nameof(LeavePayrollImpact) => "pending_payroll_impacts",
        nameof(VisaRecord) or nameof(PassportRecord) or nameof(WorkPermitRecord) or nameof(EmployeeContract) => "contracts_and_permits",
        nameof(EmployeeDependent) => "dependents",
        nameof(ApprovalAuthority) => "approval_authority",
        _ => null,
    };

    private static readonly (string Reason, string[] Entities)[] IgnorableGroups =
    [
        ("AI/analytics output about the person, derived and re-creatable, carries no money or compliance fact",
            ["AIHRQueryCache", "AIHRQueryLog", "AIInsight", "AIRecommendation", "AttendanceAIInsight", "BurnoutRiskSignal", "EmployeeChurnPrediction", "EmployeeRiskScore",
             "PayrollAIValidationResult", "ComplianceAIInsight"]),
        ("attendance/time history; the pay effect is carried by AttendancePayrollImpact and OvertimePayrollImpact, which block",
            ["AttendanceDailyRecord", "AttendanceEvidence", "AttendanceException", "AttendanceRawEvent", "AttendanceRecord", "AttendanceRegularizationRequest", "AbsenceRecord",
             "AbsenceRegularizationRequest", "Timesheet", "TimesheetDayReconciliation", "TimesheetEntry", "ShiftAssignment", "BiometricConsent"]),
        ("performance, learning and talent records: HR development history, no payment or legal record is held here",
            ["AppraisalAppeal", "AppraisalReview", "ContinuousFeedback", "DevelopmentPlan", "EmployeeGoal", "PerformanceCycleEmployee", "PerformanceImprovementPlan", "ProbationReview",
             "SuccessionNomination", "TalentAssessment", "LearningAssignment", "LearningAttempt",
             "LearningCredential", "LearningEnrollment", "LearningLaunchSession", "EngagementSurveyInvitation", "EmployeeSentimentPulse"]),
        ("a recommendation or proposal only: nothing is payable until it becomes a bonus, salary change or structure, which block",
            ["CompWorksheetLine", "PromotionRecommendation", "IncrementRecommendation", "BonusRecommendation"]),
        ("audit, history or access log about the person: the merge writes its own audit row and the history stays readable on the retained record",
            ["EmployeeHistory", "EmployeeStatusHistory", "EmployeeSelfServiceAuditLog", "EmployeePayslipAccessLog", "EmployeeAIQueryLog", "ComplianceAuditLog", "QiwaSyncLog",
             "EnterpriseIdentityProvisioningEvent", "NotificationDelivery", "OpeningBalanceOrigin", "LeaveBalanceTransaction", "PayrollRunConsumption"]),
        ("self-service preferences, notifications and device rows: no money or compliance fact; the login itself is closed by the merge",
            ["ESSDashboardPreference", "EmployeeActionItem", "EmployeeMobileDevice", "EmployeeNotification", "EmployeeNotificationCategoryPreference", "EmployeeNotificationPreference",
             "EmployeeUserAccount", "HRRequest", "HRRequestComment", "EmployeeDocumentRequest", "EmployeeProfileChangeRequest", "EmployeePolicyAcknowledgement", "OnboardingTask"]),
        ("a pending request the merge cancels or that HR can raise again: no payment has been made against it",
            ["EmployeeChangeRequest", "EmployeeTransferRequest", "EmployeeImportGap", "LeaveCancellationRequest", "LeaveModificationRequest", "LeaveDelegation", "CompOffUsage",
             "OvertimeAdjustment", "OvertimeCalculation", "OvertimeCompOffConversion"]),
        ("payroll run detail or diagnostics: the run line is covered by PayrollSlip/PayrollRunEmployee and the money by the adjustment, allowance and payment-record checks",
            ["PayrollDeduction", "PayrollEarning", "PayrollException", "PayrollRunEmployeeSelection", "PayrollValidationOverride", "PayrollValidationResult", "BenefitPayrollDeductionLink"]),
        ("reporting/compliance monitoring derived from other records (renewals, reminders, Nitaqat weights, entitlement copies)",
            ["ComplianceReminder", "ComplianceRenewal", "ContractRenewalCase", "NitaqatEmployeeWeightOverride", "EmployeeEntitlement"]),
        ("org-chart edge where the duplicate is the SUBORDINATE; the manager side blocks through reporting_lines",
            ["ReportingLine"]),
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
        var target = new Target(tenantId, employeeId, employee.PublicId, DateOnly.FromDateTime(DateTime.UtcNow));

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (entity, counter) in Counters)
        {
            var n = await counter(db, target, ct);
            if (n > 0) counts[TypeOf(entity)!] = counts.GetValueOrDefault(TypeOf(entity)!) + n;
        }
        foreach (var (type, counter) in Relationships)
        {
            var n = await counter(db, target, ct);
            if (n > 0) counts[RelationshipType(type)] = counts.GetValueOrDefault(RelationshipType(type)) + n;
        }

        return Types.Where(t => counts.ContainsKey(t.Key)).Select(t => new MergeBlocker(t.Key, t.Label, t.LabelAr, counts[t.Key])).ToList();
    }

    private static string RelationshipType(string key) => key == "approval_delegations" ? "approval_authority" : key;
}
