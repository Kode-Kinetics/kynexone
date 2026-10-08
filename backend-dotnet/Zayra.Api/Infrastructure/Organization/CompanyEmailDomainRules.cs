using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Organization;

/// <summary>
/// Amendment 3 F5. A company's <c>EmailDomain</c> routes sign-in without a workspace and decides whose work emails may
/// become logins, so it is a security setting:
/// <list type="bullet">
///   <item>only a <c>security.manage</c> holder sets or changes it (enforced by the callers that have a principal);</item>
///   <item>a free-mail / public domain is refused (anyone can hold a mailbox there);</item>
///   <item>a domain an ACTIVE company of ANOTHER tenant already holds is refused with 409 <c>email_domain_claimed</c>;</item>
///   <item>every change is audited (<c>organization.company_email_domain_changed</c>).</item>
/// </list>
/// </summary>
public static class CompanyEmailDomainRules
{
    public const string SecurityPermission = "security.manage";
    public const string ChangedAction = "organization.company_email_domain_changed";

    public static readonly IReadOnlySet<string> PublicDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "outlook.sa", "hotmail.com", "hotmail.co.uk", "live.com", "msn.com",
        "yahoo.com", "yahoo.co.uk", "ymail.com", "rocketmail.com", "icloud.com", "me.com", "mac.com", "aol.com",
        "proton.me", "protonmail.com", "pm.me", "gmx.com", "gmx.net", "mail.com", "zoho.com", "yandex.com", "yandex.ru",
        "tutanota.com", "tuta.io", "fastmail.com", "hey.com", "qq.com", "163.com", "126.com",
    };

    public static string Normalize(string? domain) => (domain ?? string.Empty).Trim().ToLowerInvariant();

    public const string NeedsSecurityAdminCode = "email_domain_needs_security_admin";
    public const string NeedsSecurityAdminMessage =
        "Only a security administrator can set or change a company's email domain, because it decides who can sign in.";

    /// <summary>
    /// THE gate, enforced inside the writer (OrganizationSetupService) so no controller can skip it: setting or changing
    /// a company's EmailDomain needs <c>security.manage</c>, read from the database for <paramref name="actorUserId"/>.
    /// <paramref name="current"/> null = a new company. A context with no user is a system path (seeding) and passes;
    /// an unknown or inactive user fails closed.
    /// </summary>
    public static async Task EnsureCallerMaySetAsync(ZayraDbContext db, Guid tenantId, Guid? actorUserId, string? current, string? requested, CancellationToken ct)
    {
        var next = Normalize(requested);
        var changes = current is null ? next.Length > 0 : !string.Equals(Normalize(current), next, StringComparison.Ordinal);
        if (!changes || actorUserId is not Guid actor) return;
        var caller = await Zayra.Api.Infrastructure.Auth.PrivilegeCeilingGraph.TryLoadCallerAsync(db, tenantId, actor, ct);
        if (caller is null || !caller.Held.Contains(SecurityPermission))
            throw new EmailDomainRefusedException(NeedsSecurityAdminCode, 403, NeedsSecurityAdminMessage);
    }

    /// <summary>Throws <see cref="EmailDomainRefusedException"/> when <paramref name="domain"/> may not be claimed by <paramref name="tenantId"/>.</summary>
    public static async Task EnsureClaimableAsync(ZayraDbContext db, Guid tenantId, string? domain, CancellationToken ct)
    {
        var value = Normalize(domain);
        if (value.Length == 0) return;
        if (PublicDomains.Contains(value))
            throw new EmailDomainRefusedException("email_domain_public", 422,
                $"'{value}' is a public email service. Use your company's own email domain.");
        // An existence check across tenants: no row leaves this method, only the yes/no. Bounded to one row.
        var claimed = await ScopedBypass.SystemWide(db.Companies, 1,
                "Company email-domain uniqueness: is the domain held by an active company of ANOTHER tenant (routing must stay unambiguous). Existence only; nothing is returned.",
                c => c.TenantId != tenantId && c.IsActive && !c.IsDeleted && c.EmailDomain == value,
                c => c.Id)
            .AsNoTracking().AnyAsync(ct);
        if (claimed)
            throw new EmailDomainRefusedException("email_domain_claimed", 409,
                $"Another organisation already uses '{value}' in KynexOne. Contact support if this is your domain.");
    }
}

public sealed class EmailDomainRefusedException(string code, int status, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
