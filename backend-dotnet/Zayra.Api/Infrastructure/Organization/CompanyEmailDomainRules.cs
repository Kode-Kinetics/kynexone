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
