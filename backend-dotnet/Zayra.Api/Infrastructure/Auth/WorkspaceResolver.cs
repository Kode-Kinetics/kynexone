using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// Sign-in without a workspace (contract §4, Amendment 3 F5): the email's domain routes to a workspace ONLY when exactly
/// one active tenant has an active company claiming that domain. Zero or several matches answer
/// <c>workspace_required</c> after a dummy PBKDF2, so the answer's timing says nothing either. It never looks at whether
/// the email exists — routing is by domain alone.
/// </summary>
public static class WorkspaceResolver
{
    public const string WorkspaceRequiredCode = "workspace_required";

    /// <summary>The workspace slug for <paramref name="email"/>'s domain, or null.</summary>
    public static async Task<string?> ResolveSlugAsync(ZayraDbContext db, string? email, CancellationToken ct)
    {
        var domain = DomainOf(email);
        if (domain is null) return null;
        // Anonymous request: system scope, every tenant's companies are visible by design (routing only). Bounded.
        var tenants = await db.Companies.AsNoTracking()
            .Where(c => c.IsActive && !c.IsDeleted && c.EmailDomain != null && c.EmailDomain.ToLower() == domain
                && db.Tenants.Any(t => t.Id == c.TenantId && t.IsActive))
            .Select(c => c.TenantId)
            .Distinct()
            .Take(2)
            .ToListAsync(ct);
        if (tenants.Count != 1) return null;
        var tenantId = tenants[0];
        return await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Slug).FirstOrDefaultAsync(ct);
    }

    /// <summary>Lower-cased domain after the last '@', or null.</summary>
    public static string? DomainOf(string? email)
    {
        var value = (email ?? string.Empty).Trim();
        var at = value.LastIndexOf('@');
        if (at <= 0 || at == value.Length - 1) return null;
        return value[(at + 1)..].Trim().ToLowerInvariant();
    }

    /// <summary>Spends one PBKDF2 verification so a refusal costs what a real check costs.</summary>
    public static Task SpendDummyAsync(IPasswordHasher hasher, PasswordVerificationGate? gate, CancellationToken ct) =>
        PasswordVerificationGate.RunAsync(gate, () => hasher.Verify("workspace-routing-miss", Pbkdf2PasswordHasher.DummyHash), ct);
}

/// <summary>Sign-in or forgot-password could not choose a workspace from the email: the client shows the Company ID field.</summary>
public sealed class WorkspaceRequiredException : Exception
{
    public WorkspaceRequiredException() : base("Enter your Company ID.") { }
}
