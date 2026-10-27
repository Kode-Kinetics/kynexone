using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>Why a cost-centre reference was refused. Not-found covers a missing, deleted or other-tenant id alike, so a caller cannot probe another tenant.</summary>
public static class CostCentreRefCodes
{
    public const string NotFound = "cost_centre_not_found";
    public const string OtherCompany = "cost_centre_other_company";
    public const string Inactive = "cost_centre_inactive";
}

public sealed record CostCentreRefResult(bool IsValid, string? Code, string? Message)
{
    public static readonly CostCentreRefResult Ok = new(true, null, null);
}

/// <summary>An <see cref="InvalidOperationException"/> so every door that already maps that to a 4xx keeps working, with the machine code beside the text.</summary>
public sealed class CostCentreRefException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Contract 2.9: the ONE check behind every door that writes a CostCenterId (department link, position, employee, timesheet,
/// planning, imports, company setup, setup assistant). A second door with a weaker rule is how a cost centre of another
/// company reaches a journal, so a new writer calls this rather than re-implementing it.
/// </summary>
public interface ICostCentreRefValidator
{
    /// <param name="ownerCompanyId">The company of the record being written (null for a legacy unassigned row).</param>
    /// <param name="forNewAssignment">True when the value is being newly set or changed; false when an existing value is merely
    /// being carried through a save, so an unrelated edit is never blocked by a cost centre deactivated since.</param>
    Task<CostCentreRefResult> ValidateAsync(Guid tenantId, Guid? ownerCompanyId, Guid costCenterId, bool forNewAssignment, CancellationToken ct = default);
}

public sealed class CostCentreRefValidator(ZayraDbContext db) : ICostCentreRefValidator
{
    public async Task<CostCentreRefResult> ValidateAsync(Guid tenantId, Guid? ownerCompanyId, Guid costCenterId, bool forNewAssignment, CancellationToken ct = default)
    {
        // The normal query filters stay on (soft delete, company scope): a row the caller cannot see is, for this purpose, not found.
        var row = await db.CostCenters.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Id == costCenterId)
            .Select(c => new { c.CompanyId, c.IsActive, c.IsDeleted })
            .FirstOrDefaultAsync(ct);
        if (row is null || row.IsDeleted)
            return new(false, CostCentreRefCodes.NotFound, "The cost centre was not found in this tenant.");
        if (row.CompanyId != ownerCompanyId)
            return new(false, CostCentreRefCodes.OtherCompany, "The cost centre belongs to another company. Choose one of this company's cost centres.");
        if (forNewAssignment && !row.IsActive)
            return new(false, CostCentreRefCodes.Inactive, "The cost centre is inactive and cannot be assigned.");
        return CostCentreRefResult.Ok;
    }
}

public static class CostCentreRefValidatorExtensions
{
    /// <summary>Throws <see cref="CostCentreRefException"/> when the reference is refused.</summary>
    public static async Task EnsureAsync(this ICostCentreRefValidator validator, Guid tenantId, Guid? ownerCompanyId, Guid costCenterId, bool forNewAssignment, CancellationToken ct = default)
    {
        var result = await validator.ValidateAsync(tenantId, ownerCompanyId, costCenterId, forNewAssignment, ct);
        if (!result.IsValid) throw new CostCentreRefException(result.Code!, result.Message!);
    }
}
