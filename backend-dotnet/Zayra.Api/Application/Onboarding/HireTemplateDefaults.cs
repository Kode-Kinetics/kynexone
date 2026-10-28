using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Application.Onboarding;

/// <summary>What a hire template pre-fills. Money is never here: the grade master stays the pay source.</summary>
public sealed record HireTemplateDefaultValues(
    Guid? DepartmentId, string? EmploymentType, string? ContractType, int? ProbationDays, string? WorkLocation, string ManagerRule);

public sealed record DefaultsParseResult(HireTemplateDefaultValues? Values, IReadOnlyList<string> Errors)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Reads and checks the <c>defaults</c> document of a hire template. Unknown keys are refused (a typo must not be stored and
/// silently ignored), every value is bounded, and every referenced id is checked against the template's own company and tenant
/// on save and again when the template is used, so a template can never inject another company's record into a hire.
/// </summary>
public static class HireTemplateDefaults
{
    public const string ManagerNone = "none";
    public const string ManagerDepartmentHead = "department-head";
    private static readonly string[] Allowed = { "departmentId", "employmentType", "contractType", "probationDays", "workLocation", "managerRule" };
    private const string Why = "Tenant-pinned existence check for a hire template default; the template's company is compared explicitly.";

    public static DefaultsParseResult Parse(string? json)
    {
        var errors = new List<string>();
        JsonNode? node;
        try { node = string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json); }
        catch (JsonException) { return new(null, new[] { "defaults_not_object" }); }
        if (node is not JsonObject obj) return new(null, new[] { "defaults_not_object" });
        foreach (var key in obj.Select(p => p.Key))
            if (!Allowed.Contains(key, StringComparer.Ordinal)) errors.Add("unknown_key:" + key);

        Guid? department = null;
        if (obj["departmentId"] is { } d && d.GetValueKind() != JsonValueKind.Null)
        {
            if (d.GetValueKind() == JsonValueKind.String && Guid.TryParse(d.GetValue<string>(), out var g) && g != Guid.Empty) department = g;
            else errors.Add("invalid:departmentId");
        }
        string? Text(string key, int max)
        {
            if (obj[key] is not { } n || n.GetValueKind() == JsonValueKind.Null) return null;
            if (n.GetValueKind() != JsonValueKind.String) { errors.Add("invalid:" + key); return null; }
            var s = n.GetValue<string>().Trim();
            if (s.Length > max) { errors.Add("too_long:" + key); return null; }
            return s.Length == 0 ? null : s;
        }
        var employmentType = Text("employmentType", 80);
        var contractType = Text("contractType", 80);
        var workLocation = Text("workLocation", 120);
        int? probation = null;
        if (obj["probationDays"] is { } p && p.GetValueKind() != JsonValueKind.Null)
        {
            if (p.GetValueKind() == JsonValueKind.Number && p.AsValue().TryGetValue<int>(out var days) && days is >= 0 and <= 180) probation = days;
            else errors.Add("invalid:probationDays");   // KSA Art. 53: a probation period is at most 180 days; the wizard enforces the rest.
        }
        var managerRule = Text("managerRule", 20) ?? ManagerNone;
        if (managerRule is not (ManagerNone or ManagerDepartmentHead)) errors.Add("invalid:managerRule");

        return errors.Count > 0
            ? new(null, errors)
            : new(new HireTemplateDefaultValues(department, employmentType, contractType, probation, workLocation, managerRule), errors);
    }

    /// <summary>Canonical JSON for storage: only the allowed keys, only the ones that carry a value.</summary>
    public static string ToJson(HireTemplateDefaultValues v)
    {
        var o = new JsonObject();
        if (v.DepartmentId is Guid d) o["departmentId"] = d.ToString();
        if (v.EmploymentType is not null) o["employmentType"] = v.EmploymentType;
        if (v.ContractType is not null) o["contractType"] = v.ContractType;
        if (v.ProbationDays is int p) o["probationDays"] = p;
        if (v.WorkLocation is not null) o["workLocation"] = v.WorkLocation;
        if (v.ManagerRule != ManagerNone) o["managerRule"] = v.ManagerRule;
        return o.ToJsonString();
    }

    /// <summary>The ids a template names must exist in the tenant, be live, and belong to the template's company (or be shared).</summary>
    public static async Task<IReadOnlyList<string>> ReferenceErrorsAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId, HireTemplateDefaultValues values,
        Guid? designationId, Guid? gradeId, Guid? branchId, Guid? checklistId, CancellationToken ct)
    {
        var errors = new List<string>();
        if (values.DepartmentId is Guid dep)
        {
            var row = await ScopedBypass.TenantWide(db.Departments, tenantId, Why).AsNoTracking()
                .Where(x => x.Id == dep && !x.IsDeleted && x.IsActive).Select(x => new { x.CompanyId }).FirstOrDefaultAsync(ct);
            if (row is null || (row.CompanyId is not null && row.CompanyId != companyId)) errors.Add("invalid_reference:departmentId");
        }
        if (designationId is Guid des)
        {
            var row = await ScopedBypass.TenantWide(db.Designations, tenantId, Why).AsNoTracking()
                .Where(x => x.Id == des && !x.IsDeleted && x.IsActive).Select(x => new { x.CompanyId }).FirstOrDefaultAsync(ct);
            if (row is null || (row.CompanyId is not null && row.CompanyId != companyId)) errors.Add("invalid_reference:matchDesignationId");
        }
        if (gradeId is Guid gr)
        {
            var row = await ScopedBypass.TenantWide(db.Grades, tenantId, Why).AsNoTracking()
                .Where(x => x.Id == gr && !x.IsDeleted && x.IsActive).Select(x => new { x.CompanyId }).FirstOrDefaultAsync(ct);
            if (row is null || (row.CompanyId is not null && row.CompanyId != companyId)) errors.Add("invalid_reference:matchGradeId");
        }
        if (branchId is Guid br)
        {
            var row = await ScopedBypass.TenantWide(db.Branches, tenantId, Why).AsNoTracking()
                .Where(x => x.Id == br && !x.IsDeleted && x.IsActive).Select(x => new { x.CompanyId }).FirstOrDefaultAsync(ct);
            if (row is null || row.CompanyId != companyId) errors.Add("invalid_reference:matchBranchId");
        }
        if (checklistId is Guid cl)
        {
            var row = await ScopedBypass.TenantWide(db.OnboardingChecklists, tenantId, Why).AsNoTracking()
                .Where(x => x.Id == cl && !x.IsDeleted && x.IsActive).Select(x => new { x.CompanyId }).FirstOrDefaultAsync(ct);
            if (row is null || (row.CompanyId is not null && row.CompanyId != companyId)) errors.Add("invalid_reference:checklistId");
        }
        return errors;
    }
}
