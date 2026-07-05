using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class EmployeeIdRule : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public string Name { get; set; } = "Default employee ID rule";
    public string CompanyPrefix { get; set; } = "ZAY";
    public bool UseCountryPrefix { get; set; } = true;
    public bool UseBranchPrefix { get; set; }
    public bool UseDepartmentPrefix { get; set; } = true;
    public bool UseYear { get; set; } = true;
    public int PaddingLength { get; set; } = 4;
    public int NextSequence { get; set; } = 1;
    public bool AllowManualOverride { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
}
