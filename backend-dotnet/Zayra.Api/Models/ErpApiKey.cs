using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// A tenant's machine credential for its ERP: lets the ERP pull GL journal exports and, when granted,
/// confirm or reject them. Off by default — a tenant has none until an MFA-enrolled Admin creates one.
///
/// <para>WHY A TABLE. No existing table holds a machine credential with an owner, scopes, an expiry,
/// an address allow-list and a revocation record. SCIM's single token lives on
/// <c>TenantIdentityProviderSetting</c> (one per tenant, no scopes, no expiry) and device keys on
/// <c>AttendanceDevice</c> (attendance ingest only). Finance owns it; rows are kept for as long as
/// the GL exports they confirmed (the confirmation stamps carry this key's id as the actor).</para>
///
/// <para>SECRET HANDLING. The plaintext (<c>kx_live_…</c>) is returned once at creation and never
/// stored: <see cref="KeyHash"/> is its SHA-256 (the same hashing as SCIM tokens and device keys) and
/// <see cref="Prefix"/> is a short, non-secret head of the key so people can tell keys apart.</para>
///
/// <para>SCOPING. <see cref="CompanyId"/> is the company the key GRANTS (null = every company in the
/// tenant), not an ownership scope — the same shape as <c>UserEntityAccess</c>, and allow-listed in
/// <c>CompanyScopeBootAssertion</c> for the same reason. Management is tenant-filtered and Admin-only.</para>
/// </summary>
public class ErpApiKey : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Company the key may act for. Null = every company of the tenant (group key).</summary>
    public Guid? CompanyId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>First characters of the key (e.g. <c>kx_live_AbCd1234</c>). Not secret; identification only.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>SHA-256 of the full key, upper-case hex. The plaintext is never stored.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>Space-separated subset of <c>ErpApiKeyScopes.All</c>.</summary>
    public string Scopes { get; set; } = string.Empty;

    /// <summary>Newline-separated CIDRs; empty = any address.</summary>
    public string IpAllowList { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = string.Empty;

    public DateTime? LastUsedAtUtc { get; set; }
    public string? LastUsedIp { get; set; }

    public DateTime? RevokedAtUtc { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public string? RevokedByName { get; set; }
}
