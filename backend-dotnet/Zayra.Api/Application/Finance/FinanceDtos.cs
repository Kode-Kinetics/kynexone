using Zayra.Api.Models;

namespace Zayra.Api.Application.Finance;

// ── Loans ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Flat projection of EmployeeLoan. Omits TenantId, IsDeleted, and EF-internal fields
/// (EmployeeIntId bridge, UpdatedBy/CreatedBy Guids) that must never reach the client.
/// All financial amounts are included: access is already owner-scoped in ListLoans /
/// GetLoan (scope filter + explicit tenantId WHERE clause). No per-field masking needed.
/// </summary>
public record EmployeeLoanDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    Guid LoanTypeId,
    string LoanTypeName,
    string LoanNumber,
    decimal RequestedAmount,
    decimal ApprovedAmount,
    int RequestedInstallments,
    int ApprovedInstallments,
    decimal InstallmentAmount,
    string RepaymentFrequency,
    DateOnly? DisbursementDate,
    DateOnly? RepaymentStartDate,
    decimal TotalRepaid,
    decimal OutstandingBalance,
    string Status,
    string? RejectionReason,
    string Notes,
    bool IsLockedByPayroll,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc)
{
    public static EmployeeLoanDto Project(EmployeeLoan e) => new(
        e.Id, e.EmployeeId, e.EmployeeName,
        e.LoanTypeId, e.LoanTypeName, e.LoanNumber,
        e.RequestedAmount, e.ApprovedAmount,
        e.RequestedInstallments, e.ApprovedInstallments,
        e.InstallmentAmount, e.RepaymentFrequency,
        e.DisbursementDate, e.RepaymentStartDate,
        e.TotalRepaid, e.OutstandingBalance,
        e.Status, e.RejectionReason, e.Notes,
        e.IsLockedByPayroll,
        e.CreatedAtUtc, e.UpdatedAtUtc);
}

// ── Advances ──────────────────────────────────────────────────────────────────

/// <summary>
/// Flat projection of SalaryAdvance. Same rationale as EmployeeLoanDto.
/// </summary>
public record SalaryAdvanceDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    string AdvanceNumber,
    decimal RequestedAmount,
    decimal ApprovedAmount,
    string RepaymentType,
    int Installments,
    decimal InstallmentAmount,
    DateOnly? RepaymentStartDate,
    decimal TotalRepaid,
    decimal OutstandingBalance,
    string Reason,
    string Status,
    string? RejectionReason,
    bool IsLockedByPayroll,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc)
{
    public static SalaryAdvanceDto Project(SalaryAdvance e) => new(
        e.Id, e.EmployeeId, e.EmployeeName,
        e.AdvanceNumber,
        e.RequestedAmount, e.ApprovedAmount,
        e.RepaymentType, e.Installments, e.InstallmentAmount,
        e.RepaymentStartDate,
        e.TotalRepaid, e.OutstandingBalance,
        e.Reason, e.Status, e.RejectionReason,
        e.IsLockedByPayroll,
        e.CreatedAtUtc, e.UpdatedAtUtc);
}

// ── Bonuses ───────────────────────────────────────────────────────────────────

/// <summary>
/// Flat projection of EmployeeBonus for payroll-pending and detail views.
/// BasicSalary is gated: it is the employee's base salary used for GOSI calculation,
/// visible only to Finance/HR roles (CanViewFinanceSensitive). Non-privileged callers
/// receive null for that field.
/// </summary>
public record EmployeeBonusDto(
    Guid Id,
    Guid BonusBatchId,
    Guid EmployeeId,
    string EmployeeName,
    string Department,
    Guid BonusTypeId,
    string BonusTypeName,
    decimal? BasicSalary,
    string CalculationMethod,
    decimal CalculationValue,
    decimal GrossBonusAmount,
    decimal TaxWithheld,
    decimal BonusAmount,
    string TaxRegion,
    string PaymentPeriod,
    string Status,
    string Notes,
    Guid? PayrollRunId,
    DateTime CreatedAtUtc)
{
    public static EmployeeBonusDto Project(EmployeeBonus e, bool includeSensitive) => new(
        e.Id, e.BonusBatchId,
        e.EmployeeId, e.EmployeeName, e.Department,
        e.BonusTypeId, e.BonusTypeName,
        includeSensitive ? e.BasicSalary : null,
        e.CalculationMethod, e.CalculationValue,
        e.GrossBonusAmount, e.TaxWithheld, e.BonusAmount,
        e.TaxRegion, e.PaymentPeriod, e.Status, e.Notes,
        e.PayrollRunId, e.CreatedAtUtc);
}

// ── Payroll Slips ─────────────────────────────────────────────────────────────

/// <summary>
/// Flat projection of PayrollSlip. Salary fields (basic, gross, net, deductions, YTD)
/// are gated by CanViewFinanceSensitive() — the caller must have Finance / Payroll Officer
/// / HR Manager / Admin role. Employees viewing their own slip via ESS see all fields
/// (the ESS endpoint uses a separate path that passes includeSensitive = true because
/// the scope filter already constrains to their own EmployeeId).
/// </summary>
public record PayrollDeductionLineDto(
    string Code,
    string Name,
    decimal Amount,
    string Source);

public record PayrollSlipDto(
    Guid Id,
    Guid RunId,
    int EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string Department,
    decimal? BasicSalary,
    decimal? HousingAllowance,
    decimal? TransportAllowance,
    decimal? OtherAllowances,
    decimal? GrossSalary,
    decimal? Deductions,
    decimal? LoanDeductions,
    decimal? NetSalary,
    decimal? YtdGross,
    decimal? YtdDeductions,
    decimal? YtdNet,
    string Status,
    // Statutory deduction totals (GOSI/GPSSA/GRSIA employee-side and employer-side).
    // EmployeeStatutoryTotal is included in Deductions. EmployerStatutoryTotal is NOT.
    decimal? EmployeeStatutoryTotal,
    decimal? EmployerStatutoryTotal,
    IReadOnlyList<PayrollDeductionLineDto>? DeductionLines)
{
    public static PayrollSlipDto Project(
        PayrollSlip s, bool includeSensitive,
        IReadOnlyList<PayrollDeductionLineDto>? lines = null) => new(
        s.Id, s.RunId, s.EmployeeId,
        s.EmployeeCode, s.EmployeeName, s.Department,
        includeSensitive ? s.BasicSalary              : null,
        includeSensitive ? s.HousingAllowance         : null,
        includeSensitive ? s.TransportAllowance       : null,
        includeSensitive ? s.OtherAllowances          : null,
        includeSensitive ? s.GrossSalary              : null,
        includeSensitive ? s.Deductions               : null,
        includeSensitive ? s.LoanDeductions           : null,
        includeSensitive ? s.NetSalary                : null,
        includeSensitive ? s.YtdGross                 : null,
        includeSensitive ? s.YtdDeductions            : null,
        includeSensitive ? s.YtdNet                   : null,
        s.Status,
        includeSensitive ? s.EmployeeStatutoryTotal   : null,
        includeSensitive ? s.EmployerStatutoryTotal   : null,
        includeSensitive ? lines                      : null);
}

// ── EOSB Calculations ─────────────────────────────────────────────────────────

/// <summary>
/// Flat projection of EOSBCalculation. EligibleSalary and CalculatedAmount are
/// per-employee figures gated to Finance/HR roles (class-level auth enforces this).
/// </summary>
public record EosbCalculationDto(
    Guid Id,
    int EmployeeId,
    DateOnly CalculationDate,
    decimal EligibleSalary,
    decimal CalculatedAmount,
    string Status)
{
    public static EosbCalculationDto Project(EOSBCalculation e) => new(
        e.Id, e.EmployeeId, e.CalculationDate,
        e.EligibleSalary, e.CalculatedAmount, e.Status);
}

// ── Salary Structure Assignments ──────────────────────────────────────────────

/// <summary>
/// Flat projection of EmployeeSalaryStructure. All salary components are gated —
/// a non-privileged user (e.g. a manager who can see headcount but not salaries)
/// must not receive any figure from this endpoint.
/// </summary>
public record SalaryStructureAssignmentDto(
    Guid Id,
    int EmployeeId,
    Guid SalaryStructureId,
    decimal? BasicSalary,
    decimal? HousingAllowance,
    decimal? TransportAllowance,
    decimal? FoodAllowance,
    decimal? MobileAllowance,
    decimal? OtherAllowance,
    decimal? FixedDeduction,
    DateOnly EffectiveDate,
    string Currency,
    bool IsActive,
    DateTime CreatedAtUtc)
{
    public static SalaryStructureAssignmentDto Project(EmployeeSalaryStructure e, bool includeSensitive) => new(
        e.Id, e.EmployeeId, e.SalaryStructureId,
        includeSensitive ? e.BasicSalary        : null,
        includeSensitive ? e.HousingAllowance   : null,
        includeSensitive ? e.TransportAllowance : null,
        includeSensitive ? e.FoodAllowance      : null,
        includeSensitive ? e.MobileAllowance    : null,
        includeSensitive ? e.OtherAllowance     : null,
        includeSensitive ? e.FixedDeduction     : null,
        e.EffectiveDate, e.Currency, e.IsActive, e.CreatedAtUtc);
}
