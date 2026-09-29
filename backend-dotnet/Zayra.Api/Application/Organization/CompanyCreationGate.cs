using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Application.Organization;

/// <summary>Which gate stopped a company from being created, or <see cref="Allowed"/>.</summary>
public enum CompanyCreationVerdict
{
    Allowed,
    TenantUnknown,
    SubscriptionLimitReached,
    PlatformControlled,
    SingleCompanyAccount,
}

/// <summary>
/// The outcome of the four checks that stand between a request and a new legal entity, in a form
/// both doors can render: the form turns it into an HTTP status, the importer turns it into a row
/// error naming the same reason.
/// </summary>
public sealed record CompanyCreationGateDecision(
    CompanyCreationVerdict Verdict,
    string ErrorCode,
    string Message,
    bool AsDraft = false,
    int CurrentCount = 0,
    int MaxAllowed = 0)
{
    public bool Allowed => Verdict == CompanyCreationVerdict.Allowed;
}

/// <summary>
/// Creating a legal entity is gated four ways, and the gates are commercial and contractual rather
/// than technical: the plan's company allowance, whether the platform (not the tenant) owns company
/// creation for this account, whether the account is licensed for more than one legal entity at
/// all, and whether new companies land as drafts awaiting platform approval.
///
/// <para>These lived inline in <c>CompaniesController.Create</c> and therefore applied to exactly
/// one door. The CSV importer wrote to the context directly and passed none of them, so a tenant on
/// a one-company plan could hold ten companies by uploading a spreadsheet, and a
/// PlatformControlled account could create its own. Both doors now evaluate this.</para>
/// </summary>
public static class CompanyCreationGate
{
    /// <param name="pendingCreates">
    /// Companies this batch has already decided to create but has not written yet. The commit path
    /// writes a row at a time and re-counts, so it passes 0; the preview path writes nothing, so it
    /// passes the running total and gets the same answer the commit will give.
    /// </param>
    public static async Task<CompanyCreationGateDecision> EvaluateAsync(
        ZayraDbContext db, Guid tenantId, CancellationToken cancellationToken, int pendingCreates = 0)
    {
        // ── Subscription limit (commercial) ───────────────────────────────────
        var sub = await db.TenantSubscriptions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        if (sub is not null && sub.MaxCompanies > 0)
        {
            var companyCount = await db.Companies.CountAsync(c => c.TenantId == tenantId, cancellationToken) + pendingCreates;
            if (companyCount >= sub.MaxCompanies)
                return new CompanyCreationGateDecision(
                    CompanyCreationVerdict.SubscriptionLimitReached,
                    "company_limit_reached",
                    $"Your plan allows up to {sub.MaxCompanies} legal compan{(sub.MaxCompanies == 1 ? "y" : "ies")}. Upgrade your plan to add more.",
                    CurrentCount: companyCount,
                    MaxAllowed: sub.MaxCompanies);
        }

        // ── Governance gates (product behaviour) ──────────────────────────────
        var tenant = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.AccountType, t.CompanyCreationMode })
            .FirstOrDefaultAsync(cancellationToken);
        if (tenant is null)
            return new CompanyCreationGateDecision(
                CompanyCreationVerdict.TenantUnknown,
                "tenant_unknown",
                "Tenant not found.");

        // PlatformControlled: only platform admins create companies for this tenant.
        if (tenant.CompanyCreationMode == CompanyCreationModes.PlatformControlled)
            return new CompanyCreationGateDecision(
                CompanyCreationVerdict.PlatformControlled,
                "company_creation_platform_controlled",
                "Company creation for this account is managed by the platform. Contact your account manager.");

        // Account type: only Group tenants operate multiple active legal entities.
        var existingCount = await db.Companies.CountAsync(c => c.TenantId == tenantId, cancellationToken) + pendingCreates;
        if (existingCount >= 1 && tenant.AccountType != TenantAccountTypes.Group)
            return new CompanyCreationGateDecision(
                CompanyCreationVerdict.SingleCompanyAccount,
                "account_type_single_company",
                "This account is configured as a single-company account. Ask your platform administrator to enable the Group account type to manage multiple legal entities.",
                CurrentCount: existingCount);

        // Draft-approval mode: group admins submit drafts; a platform admin activates.
        return new CompanyCreationGateDecision(
            CompanyCreationVerdict.Allowed,
            string.Empty,
            string.Empty,
            AsDraft: tenant.CompanyCreationMode == CompanyCreationModes.GroupDraftPlatformApproval);
    }
}
