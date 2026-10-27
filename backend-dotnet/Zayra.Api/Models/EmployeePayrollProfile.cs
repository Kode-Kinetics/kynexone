using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class EmployeePayrollProfile : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string BankName { get; set; } = string.Empty;
    public string Iban { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = "BankTransfer";
    /// <summary>Empty until saved: <see cref="Zayra.Api.Infrastructure.Payroll.CompanyCurrencyDefaults"/> fills it with the employing company's currency.</summary>
    public string SalaryCurrency { get; set; } = string.Empty;
    public string PayrollGroup { get; set; } = string.Empty;
    public string SalaryStructureReference { get; set; } = string.Empty;
    public bool WpsEligible { get; set; } = true;
    public bool EosbEligible { get; set; } = true;
    public string SocialInsuranceReference { get; set; } = string.Empty;
    /// <summary>Ministry of Labour employee registration / national ID used in WPS SIF exports.</summary>
    public string MolId { get; set; } = string.Empty;
    /// <summary>Bank branch routing / sort code required by UAE CBUAE WPS v2 and Saudi Mudad.</summary>
    public string BankRoutingCode { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
}
