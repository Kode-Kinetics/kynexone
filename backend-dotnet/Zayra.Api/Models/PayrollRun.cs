using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class PayrollRun : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public string Status { get; set; } = "Draft";
    public decimal TotalGrossSalary { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal TotalNetSalary { get; set; }
    // Employer statutory cost (GOSI/GPSSA/GRSIA employer side) — not deducted from employee net.
    public decimal TotalEmployerStatutoryCost { get; set; }
    public int EmployeeCount { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public Guid? ProcessedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
    public DateTime? LockedAtUtc { get; set; }
    // Void tracking — populated only when Status == "Voided".
    public string? VoidReason { get; set; }
    public DateTime? VoidedAtUtc { get; set; }
    public Guid? VoidedByUserId { get; set; }
    public string? VoidedByName { get; set; }
}

public class PayrollSlip : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid RunId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public decimal BasicSalary { get; set; }
    public decimal HousingAllowance { get; set; }
    public decimal TransportAllowance { get; set; }
    public decimal OtherAllowances { get; set; }
    public decimal GrossSalary { get; set; }
    public decimal Deductions { get; set; }
    public decimal NetSalary { get; set; }
    public string Status { get; set; } = "Draft";
    // Statutory deduction totals — split for reporting without re-querying PayrollDeductions.
    // EmployeeStatutoryTotal reduces employee net pay; EmployerStatutoryTotal does NOT.
    public decimal EmployeeStatutoryTotal { get; set; }
    public decimal EmployerStatutoryTotal { get; set; }
    // Compliance: YTD accumulators (populated during Process, from all prior locked runs in same year)
    public decimal YtdGross { get; set; }
    public decimal YtdDeductions { get; set; }
    public decimal YtdNet { get; set; }
    // Compliance: loan/advance deductions this period (for payslip line-item)
    public decimal LoanDeductions { get; set; }
}
