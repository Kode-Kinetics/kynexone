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
    public ICollection<User> Users { get; set; } = new List<User>();
}
