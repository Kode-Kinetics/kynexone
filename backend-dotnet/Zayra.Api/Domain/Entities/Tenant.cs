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
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<User> Users { get; set; } = new List<User>();
}
