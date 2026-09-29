namespace Zayra.Api.Domain.Entities;

/// <summary>
/// Product-behavior account types. Distinct from TenantSubscription.MaxCompanies, which
/// stays the commercial limit: AccountType drives what the product does (company switcher,
/// group admin surfaces, multi-company provisioning), MaxCompanies drives how much of it
/// the customer paid for.
/// </summary>
public static class TenantAccountTypes
{
    public const string SingleCompany = "SingleCompany";
    public const string Group = "Group";

    public static bool IsValid(string value) => value is SingleCompany or Group;
}

/// <summary>
/// Who may create companies inside a Group tenant, and how they activate.
/// </summary>
public static class CompanyCreationModes
{
    /// <summary>Only platform admins create companies for this tenant.</summary>
    public const string PlatformControlled = "PlatformControlled";
    /// <summary>Group admins create active companies themselves, within MaxCompanies.</summary>
    public const string GroupSelfServiceWithinLimit = "GroupSelfServiceWithinLimit";
    /// <summary>Group admins create Draft companies; a platform admin approves activation.</summary>
    public const string GroupDraftPlatformApproval = "GroupDraftPlatformApproval";

    public static bool IsValid(string value) =>
        value is PlatformControlled or GroupSelfServiceWithinLimit or GroupDraftPlatformApproval;
}

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    // SingleCompany | Group — see TenantAccountTypes. Existing tenants default to
    // SingleCompany; CompanyScopeBackfill promotes tenants that already operate
    // multiple active companies to Group.
    public string AccountType { get; set; } = TenantAccountTypes.SingleCompany;
    // PlatformControlled | GroupSelfServiceWithinLimit | GroupDraftPlatformApproval.
    // Self-service is the default (matches pre-existing behavior for Group tenants).
    public string CompanyCreationMode { get; set; } = CompanyCreationModes.GroupSelfServiceWithinLimit;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// D3 — when this tenant was SOFT-deleted, i.e. when its retention clock started.
    ///
    /// <para>WHY IT HAD TO EXIST. <c>PlatformController.DeleteTenant</c> soft-deletes by renaming the
    /// slug to <c>{slug}__deleted_{id8}</c> and clearing <see cref="IsActive"/>, and records the moment
    /// nowhere. Production holds 48 soft-deleted tenants and not one <c>TenantDeleted</c> audit row, so
    /// there is no date to read anywhere — which means a retention window over them had no start and no
    /// honest way to be enforced.</para>
    ///
    /// <para>NULL MEANS "NOT KNOWN", NEVER "LONG AGO". Every legacy soft-deleted tenant is null, and
    /// <c>SoftDeletedTenantRule</c> RETAINS on null rather than guessing. The first sweep that runs with
    /// applying enabled stamps NOW here, which starts the clock — it can only ever delay erasure.</para>
    ///
    /// <para>FOLLOW-UP OWED: <c>PlatformController.DeleteTenant</c> should set this at the moment of
    /// deletion so a newly deleted tenant has a true date rather than a first-observed one. That file is
    /// being changed concurrently by another workstream, so the one-line write is deliberately not made
    /// here; see scratchpad/data-retention.md.</para>
    /// </summary>
    public DateTime? SoftDeletedAtUtc { get; set; }

    /// <summary>
    /// D3 — set when the tenant's data has been erased and only this shell remains. The shell is kept on
    /// purpose: it anchors the retained audit trail and the erasure's own evidence rows, and it stops a
    /// later sweep re-processing a tenant that has nothing left to erase.
    /// </summary>
    public DateTime? PurgedAtUtc { get; set; }

    public ICollection<User> Users { get; set; } = new List<User>();
}
