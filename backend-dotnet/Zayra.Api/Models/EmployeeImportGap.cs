using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// A single typed gap recorded when the accept-never-block bulk importer landed a person as an
/// incomplete Draft (or linked them only partially). Every gap is a People-list filter + deep-link:
/// it names the org-skeleton reference that could not be resolved (unknown company/branch/department/
/// grade/position, unresolved manager/supervisor) or the payroll hold (salary held / marked for
/// review). Gaps are ADVISORY — they push a row to NeedsAttention and lower completeness but never
/// block activation. A gap self-heals: <see cref="ResolvedAtUtc"/> is stamped once the underlying
/// reference is completed on the employee (see the readiness stamp paths).
/// </summary>
public class EmployeeImportGap : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    // Denormalized from the employee so the People-list gap deep-link is company-scoped exactly like the
    // employee it describes (a company-scoped HR user sees only their company's gaps).
    public Guid? CompanyId { get; set; }
    public Guid ImportBatchId { get; set; }
    public int EmployeeId { get; set; }
    public int RowNumber { get; set; }

    /// <summary>Typed gap key, e.g. "org:department", "org:grade", "link:manager", "pay:salaryHeld".</summary>
    public string GapType { get; set; } = string.Empty;

    /// <summary>Coarse bucket: org | link | pay | readiness.</summary>
    public string GapCategory { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    /// <summary>The raw free-text value from the CSV that failed to resolve (e.g. the unknown grade code).</summary>
    public string? RawValue { get; set; }

    /// <summary>Stamped when the gap self-heals (the reference is later completed). Null = still open.</summary>
    public DateTime? ResolvedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
