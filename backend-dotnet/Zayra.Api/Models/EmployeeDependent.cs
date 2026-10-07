using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class EmployeeDependent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Relationship { get; set; } = string.Empty;
    public string NationalId { get; set; } = string.Empty;
    public DateOnly? DateOfBirth { get; set; }
    public DateOnly? VisaExpiryDate { get; set; }

    // Release A R2 (review round 2): HR removes a dependant by soft delete, so the package history that counted them stays
    // explainable. Expand-only migration 20261008000200_ReleaseAR2DependantsSoftDelete; every reader filters IsDeleted.
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
}
