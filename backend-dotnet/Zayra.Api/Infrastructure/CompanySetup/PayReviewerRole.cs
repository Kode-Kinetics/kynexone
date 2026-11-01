using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Employees.Onboarding;

namespace Zayra.Api.Infrastructure.CompanySetup;

/// <summary>
/// The seeded role a setup-created pay reviewer receives: "Finance Approver" (AuthSeeder), which holds the pay approval
/// permission and no pay-entry permission. Tenants can rename or edit roles, so setup looks the role up by name and
/// confirms it still holds the permission; a workspace without it is told so rather than given a different role.
/// </summary>
internal static class PayReviewerRole
{
    public const string Name = "Finance Approver";

    /// <summary>The tenant's (or platform) active role of that name that still holds the approval permission, or null.</summary>
    public static async Task<Guid?> ResolveAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var normalized = AuthService.Normalize(Name);
        const string why = "Pay reviewer setup: look up the approver role the user writer can assign, in this tenant and then the platform defaults.";
        foreach (var owner in new Guid?[] { tenantId, null })
        {
            var id = await ScopedBypass.NullableTenantWide(db.Roles, owner, why).AsNoTracking()
                .Where(r => r.NormalizedName == normalized && r.IsActive && !r.IsDeleted
                    && r.RolePermissions.Any(p => p.Permission != null && p.Permission.Key == CompensationApprovalGate.ApprovePermission))
                .Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
            if (id is not null) return id;
        }
        return null;
    }
}
