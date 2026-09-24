using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// One frozen payslip per employee per run, holding its own identity, proration, GOSI and year-to-date witnesses so it can be reprinted and reconciled years later without joining a single live row. @tier:C @owner:Finance @retention:84-months-from-RecordDate-then-Keep
/// </summary>
public partial class PayrollSlip
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid RunId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid? PayslipFileId { get; set; }

    public Guid? TemplateId { get; set; }

    public string? PayslipNumber { get; set; }

    public string InclusionStatus { get; set; } = null!;

    public DateOnly? PaidFrom { get; set; }

    public DateOnly? PaidTo { get; set; }

    public decimal? PaidDays { get; set; }

    public decimal? PeriodDays { get; set; }

    public decimal? ProrationDenominatorDays { get; set; }

    public string? ProrationBasis { get; set; }

    public decimal? ProrationFactor { get; set; }

    /// <summary>
    /// CACHE of the signed SUM of payroll_slip_lines of the matching kinds, reconciled by the deferred trg_slip_totals (§11.2). Lines are authoritative.
    /// </summary>
    public decimal Gross { get; set; }

    public decimal Deductions { get; set; }

    public decimal Net { get; set; }

    public decimal EmployeeStatutoryTotal { get; set; }

    public decimal EmployerStatutoryTotal { get; set; }

    public decimal LoanDeductions { get; set; }

    public decimal ArrearsAmount { get; set; }

    public bool IsFinalWageMonth { get; set; }

    /// <summary>
    /// Year-to-date set. §19.4 H7 turns the YTD query into a single-row read of the prior slip; the Opening run seeds these as the go-live carry-forward (§2.F).
    /// </summary>
    public decimal YtdGross { get; set; }

    public decimal YtdDeductions { get; set; }

    public decimal YtdNet { get; set; }

    public decimal YtdEmployeeStatutory { get; set; }

    public decimal YtdEmployerStatutory { get; set; }

    public decimal YtdContributoryWage { get; set; }

    public string? EmployeeNumber { get; set; }

    /// <summary>
    /// Identity snapshot. §2.F names these columns &quot;name, department, designation&quot;; spelled _name here so they cannot be mistaken for live joins.
    /// </summary>
    public string? EmployeeName { get; set; }

    public string? DepartmentName { get; set; }

    public string? DesignationName { get; set; }

    public string? NationalityClass { get; set; }

    public string? GosiCohort { get; set; }

    /// <summary>
    /// Frozen snapshot, and the only copy of the registration number not bound by FK; companies.gosi_registration_no is the single owner (§11.5). It is what traces a slip to the return that carried it.
    /// </summary>
    public string? EmployerGosiRegistrationNo { get; set; }

    public string? Iban { get; set; }

    public string? BankCode { get; set; }

    public string? GosiBasePolicy { get; set; }

    public decimal? FullBasic { get; set; }

    public decimal? FullHousing { get; set; }

    public decimal? FullTransport { get; set; }

    /// <summary>
    /// The PERIOD&apos;s computed contributory wage. Deliberately a different fact from payroll_slip_lines.applied_contributory_wage (per GOSI branch) and from employee_gosi_registrations.registered_contributory_wage (what GOSI holds) — §11.4.
    /// </summary>
    public decimal? ContributoryWage { get; set; }

    public string? PayslipSha256 { get; set; }

    public int? TemplateVersion { get; set; }

    public string? Language { get; set; }

    public DateTime? PublishedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ICollection<PayrollSlipLine> PayrollSlipLines { get; set; } = new List<PayrollSlipLine>();
}
