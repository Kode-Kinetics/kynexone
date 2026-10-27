using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// A hire template: what the Add Employee wizard pre-fills for a company's recurring kinds of hire, and which onboarding
/// checklist the hire gets. It holds no money (the grade master stays the pay source) and no personal data. Per company,
/// copied (never shared) to a sister company, and removable by the owner of the setup.
/// </summary>
public class HireTemplate : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Lower-cased, trimmed name: what the per-company unique index compares (no expression index in the model).</summary>
    public string NameKey { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public Guid? MatchDesignationId { get; set; }
    public Guid? MatchGradeId { get; set; }
    public Guid? MatchBranchId { get; set; }
    /// <summary>"designation|grade|branch" with an all-zero guid for an absent key, so the unique index never meets a NULL.
    /// <see cref="HireTemplate.NoMatchKey"/> = a template that matches nothing and is only ever picked by hand.</summary>
    public string MatchKey { get; set; } = NoMatchKey;
    public Guid? ChecklistId { get; set; }
    public string? EssCompletionSections { get; set; }
    public string Defaults { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    public static readonly string NoMatchKey = Key(null, null, null);

    public static string Key(Guid? designation, Guid? grade, Guid? branch) =>
        $"{designation ?? Guid.Empty}|{grade ?? Guid.Empty}|{branch ?? Guid.Empty}";
}
