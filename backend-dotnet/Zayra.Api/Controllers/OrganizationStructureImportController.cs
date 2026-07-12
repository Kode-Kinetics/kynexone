using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/setup/organization-structure-import")]
[Authorize(Roles = "Admin,HR Manager")]
public class OrganizationStructureImportController : ControllerBase
{
    private readonly ZayraDbContext _db;

    private static readonly string[] CompaniesHeaders = { "LegalNameEn", "TradeName", "CountryCode", "Jurisdiction", "RegistrationNumber", "TaxNumber", "DefaultCurrency", "IsActive" };
    private static readonly string[] BranchesHeaders = { "CompanyLegalName", "Code", "NameEn", "CountryCode", "City", "IsHeadOffice", "IsActive" };
    private static readonly string[] CostCentersHeaders = { "CompanyLegalName", "Code", "Name", "IsActive" };
    private static readonly string[] DepartmentsHeaders = { "BranchCode", "Code", "NameEn", "ParentDepartmentCode", "CostCenterCode", "ApprovedHeadcount", "MonthlyBudgetAmount", "IsActive" };
    private static readonly string[] GradesHeaders = { "Code", "Name", "Band", "Level", "MinSalary", "MidSalary", "MaxSalary", "Currency", "IsActive" };
    private static readonly string[] GradePayHeaders = { "GradeCode", "ComponentCode", "ComponentName", "ComponentType", "CalculationType", "Amount", "Percentage", "Frequency", "IsTaxable", "IsActive" };
    private static readonly string[] DesignationsHeaders = { "Code", "TitleEn", "DepartmentCode", "GradeCode", "JobLevel", "IsManagerRole", "LevelRank", "IsActive" };

    public OrganizationStructureImportController(ZayraDbContext db) => _db = db;

    [HttpGet("template")]
    public IActionResult Template()
    {
        var sb = new StringBuilder();
        void Section(string name, string[] headers, string sample)
        {
            sb.AppendLine($"# {name}");
            sb.AppendLine(string.Join(",", headers.Select(Csv.Escape)));
            sb.AppendLine(sample);
            sb.AppendLine();
        }

        Section("companies", CompaniesHeaders, "Zayra Demo LLC,Zayra,SA,SA-default,CR-001,TAX-001,SAR,true");
        Section("branches", BranchesHeaders, "Zayra Demo LLC,HQ,Head Office,SA,Riyadh,true,true");
        Section("costCenters", CostCentersHeaders, "Zayra Demo LLC,CC-HR,Human Resources,true");
        Section("departments", DepartmentsHeaders, "HQ,HR,Human Resources,,CC-HR,10,120000,true");
        Section("grades", GradesHeaders, "G3,Grade 3,Professional,3,10000,13500,17000,SAR,true");
        Section("gradePayComponents", GradePayHeaders, "G3,BASIC,Basic Salary,Earning,Fixed,8100,0,Monthly,false,true");
        Section("designations", DesignationsHeaders, "HR_OFF,HR Officer,HR,G3,Staff,false,5,true");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/plain", "organization_structure_import_package.txt");
    }

    [HttpPost("preview")]
    public async Task<ActionResult<OrganizationStructureImportResult>> Preview([FromBody] OrganizationStructureImportRequest req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var parsed = ParsePackage(req);
        return Ok(await ValidateAsync(tenantId, parsed, ct));
    }

    [HttpPost("commit")]
    public async Task<ActionResult<OrganizationStructureImportResult>> Commit([FromBody] OrganizationStructureImportRequest req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var parsed = ParsePackage(req);
        var validation = await ValidateAsync(tenantId, parsed, ct);
        if (validation.HasBlockingErrors)
            return UnprocessableEntity(validation);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var counts = new Dictionary<string, int>();
        void Bump(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

        var companies = await _db.Companies.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToDictionaryAsync(x => x.LegalNameEn.ToUpperInvariant(), ct);
        foreach (var row in parsed.Companies)
        {
            var name = Val(row, "LegalNameEn");
            var active = Bool(row, "IsActive", true);
            if (companies.TryGetValue(name.ToUpperInvariant(), out var company))
            {
                company.TradeName = Val(row, "TradeName");
                company.CountryCode = Val(row, "CountryCode");
                company.Jurisdiction = Val(row, "Jurisdiction");
                company.RegistrationNumber = Val(row, "RegistrationNumber");
                company.TaxNumber = Val(row, "TaxNumber");
                company.DefaultCurrency = Val(row, "DefaultCurrency", "USD");
                company.IsActive = active;
                company.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                company = new Company
                {
                    TenantId = tenantId,
                    LegalNameEn = name,
                    TradeName = Val(row, "TradeName"),
                    CountryCode = Val(row, "CountryCode"),
                    Jurisdiction = Val(row, "Jurisdiction"),
                    RegistrationNumber = Val(row, "RegistrationNumber"),
                    TaxNumber = Val(row, "TaxNumber"),
                    DefaultCurrency = Val(row, "DefaultCurrency", "USD"),
                    IsActive = active,
                    CreatedBy = GetUserId()
                };
                _db.Companies.Add(company);
                companies[name.ToUpperInvariant()] = company;
                Bump("companies");
            }
        }

        var branches = await _db.Branches.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToDictionaryAsync(x => x.Code.ToUpperInvariant(), ct);
        foreach (var row in parsed.Branches)
        {
            var code = Val(row, "Code");
            var company = companies[Val(row, "CompanyLegalName").ToUpperInvariant()];
            if (branches.TryGetValue(code.ToUpperInvariant(), out var branch))
            {
                branch.CompanyId = company.Id;
                branch.NameEn = Val(row, "NameEn");
                branch.CountryCode = Val(row, "CountryCode", company.CountryCode);
                branch.City = Val(row, "City");
                branch.IsHeadOffice = Bool(row, "IsHeadOffice", false);
                branch.IsActive = Bool(row, "IsActive", true);
                branch.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                branch = new Branch
                {
                    TenantId = tenantId,
                    CompanyId = company.Id,
                    Code = code,
                    NameEn = Val(row, "NameEn"),
                    CountryCode = Val(row, "CountryCode", company.CountryCode),
                    City = Val(row, "City"),
                    IsHeadOffice = Bool(row, "IsHeadOffice", false),
                    IsActive = Bool(row, "IsActive", true),
                    CreatedBy = GetUserId()
                };
                _db.Branches.Add(branch);
                branches[code.ToUpperInvariant()] = branch;
                Bump("branches");
            }
        }

        var costCenters = await _db.CostCenters.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToDictionaryAsync(x => x.Code.ToUpperInvariant(), ct);
        foreach (var row in parsed.CostCenters)
        {
            var code = Val(row, "Code");
            var company = companies[Val(row, "CompanyLegalName").ToUpperInvariant()];
            if (costCenters.TryGetValue(code.ToUpperInvariant(), out var cc))
            {
                cc.CompanyId = company.Id;
                cc.Name = Val(row, "Name");
                cc.IsActive = Bool(row, "IsActive", true);
                cc.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                cc = new CostCenter { TenantId = tenantId, CompanyId = company.Id, Code = code, Name = Val(row, "Name"), IsActive = Bool(row, "IsActive", true), CreatedBy = GetUserId() };
                _db.CostCenters.Add(cc);
                costCenters[code.ToUpperInvariant()] = cc;
                Bump("costCenters");
            }
        }

        var grades = await _db.Grades.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToDictionaryAsync(x => x.Code.ToUpperInvariant(), ct);
        foreach (var row in parsed.Grades)
        {
            var code = Val(row, "Code");
            if (!grades.TryGetValue(code.ToUpperInvariant(), out var grade))
            {
                grade = new Grade { TenantId = tenantId, Code = code, CreatedBy = GetUserId() };
                _db.Grades.Add(grade);
                grades[code.ToUpperInvariant()] = grade;
                Bump("grades");
            }
            grade.Name = Val(row, "Name");
            grade.Band = Val(row, "Band");
            grade.Level = Int(row, "Level");
            grade.MinSalary = Dec(row, "MinSalary");
            grade.MidSalary = Dec(row, "MidSalary");
            grade.MaxSalary = Dec(row, "MaxSalary");
            grade.Currency = Val(row, "Currency", "USD");
            grade.IsActive = Bool(row, "IsActive", true);
            grade.UpdatedAtUtc = DateTime.UtcNow;
        }

        var departments = await _db.Departments.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToDictionaryAsync(x => x.Code.ToUpperInvariant(), ct);
        foreach (var row in parsed.Departments)
        {
            var code = Val(row, "Code");
            if (!departments.TryGetValue(code.ToUpperInvariant(), out var department))
            {
                department = new Department { TenantId = tenantId, Code = code, CreatedBy = GetUserId() };
                _db.Departments.Add(department);
                departments[code.ToUpperInvariant()] = department;
                Bump("departments");
            }
            department.NameEn = Val(row, "NameEn");
            department.BranchId = string.IsNullOrWhiteSpace(Val(row, "BranchCode")) ? null : branches[Val(row, "BranchCode").ToUpperInvariant()].Id;
            department.CostCenterId = string.IsNullOrWhiteSpace(Val(row, "CostCenterCode")) ? null : costCenters[Val(row, "CostCenterCode").ToUpperInvariant()].Id;
            department.ApprovedHeadcount = Int(row, "ApprovedHeadcount");
            department.MonthlyBudgetAmount = Dec(row, "MonthlyBudgetAmount");
            department.IsActive = Bool(row, "IsActive", true);
            department.UpdatedAtUtc = DateTime.UtcNow;
        }
        foreach (var row in parsed.Departments)
        {
            var parentCode = Val(row, "ParentDepartmentCode");
            if (!string.IsNullOrWhiteSpace(parentCode))
                departments[Val(row, "Code").ToUpperInvariant()].ParentDepartmentId = departments[parentCode.ToUpperInvariant()].Id;
        }

        await _db.SaveChangesAsync(ct);

        foreach (var row in parsed.GradePayComponents)
        {
            var grade = grades[Val(row, "GradeCode").ToUpperInvariant()];
            var code = Val(row, "ComponentCode");
            var exists = await _db.GradePayScaleComponents.AnyAsync(x => x.TenantId == tenantId && x.GradeId == grade.Id && x.ComponentCode == code, ct);
            if (exists) continue;
            _db.GradePayScaleComponents.Add(new GradePayScaleComponent
            {
                TenantId = tenantId,
                GradeId = grade.Id,
                ComponentCode = code,
                ComponentName = Val(row, "ComponentName"),
                ComponentType = Val(row, "ComponentType", "Earning"),
                CalculationType = Val(row, "CalculationType", "Fixed"),
                Amount = Dec(row, "Amount"),
                Percentage = Dec(row, "Percentage"),
                Frequency = Val(row, "Frequency", "Monthly"),
                IsTaxable = Bool(row, "IsTaxable", false),
                IsActive = Bool(row, "IsActive", true)
            });
            Bump("gradePayComponents");
        }

        var designations = await _db.Designations.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToDictionaryAsync(x => x.Code.ToUpperInvariant(), ct);
        foreach (var row in parsed.Designations)
        {
            var code = Val(row, "Code");
            if (!designations.TryGetValue(code.ToUpperInvariant(), out var designation))
            {
                designation = new Designation { TenantId = tenantId, Code = code, CreatedBy = GetUserId() };
                _db.Designations.Add(designation);
                designations[code.ToUpperInvariant()] = designation;
                Bump("designations");
            }
            designation.TitleEn = Val(row, "TitleEn");
            designation.DepartmentId = string.IsNullOrWhiteSpace(Val(row, "DepartmentCode")) ? null : departments[Val(row, "DepartmentCode").ToUpperInvariant()].Id;
            designation.GradeId = string.IsNullOrWhiteSpace(Val(row, "GradeCode")) ? null : grades[Val(row, "GradeCode").ToUpperInvariant()].Id;
            designation.JobGrade = Val(row, "GradeCode");
            designation.JobLevel = Val(row, "JobLevel");
            designation.IsManagerRole = Bool(row, "IsManagerRole", false);
            designation.LevelRank = Int(row, "LevelRank", 1);
            designation.IsActive = Bool(row, "IsActive", true);
            designation.UpdatedAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Ok(validation with { Applied = counts, Committed = true });
    }

    private async Task<OrganizationStructureImportResult> ValidateAsync(Guid tenantId, ParsedOrgPackage parsed, CancellationToken ct)
    {
        var rows = new List<ImportRowResult>();
        var companyNames = (await _db.Companies.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).Select(x => x.LegalNameEn).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var branchCodes = (await _db.Branches.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).Select(x => x.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var costCenterCodes = (await _db.CostCenters.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).Select(x => x.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var departmentCodes = (await _db.Departments.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).Select(x => x.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gradeCodes = (await _db.Grades.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).Select(x => x.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        AddRows("companies", parsed.Companies, "LegalNameEn", "LegalNameEn", required: ["LegalNameEn", "CountryCode", "DefaultCurrency"], known: companyNames, rows);
        Merge(companyNames, parsed.Companies.Select(x => Val(x, "LegalNameEn")));
        AddRows("branches", parsed.Branches, "Code", "NameEn", required: ["CompanyLegalName", "Code", "NameEn"], known: branchCodes, rows,
            refs: [("CompanyLegalName", companyNames, "Company")]);
        Merge(branchCodes, parsed.Branches.Select(x => Val(x, "Code")));
        AddRows("costCenters", parsed.CostCenters, "Code", "Name", required: ["CompanyLegalName", "Code", "Name"], known: costCenterCodes, rows,
            refs: [("CompanyLegalName", companyNames, "Company")]);
        Merge(costCenterCodes, parsed.CostCenters.Select(x => Val(x, "Code")));
        AddRows("grades", parsed.Grades, "Code", "Name", required: ["Code", "Name"], known: gradeCodes, rows);
        AddGradeSanityRows(parsed.Grades, rows);
        Merge(gradeCodes, parsed.Grades.Select(x => Val(x, "Code")));
        AddRows("departments", parsed.Departments, "Code", "NameEn", required: ["Code", "NameEn"], known: departmentCodes, rows,
            refs: [("BranchCode", branchCodes, "Branch"), ("CostCenterCode", costCenterCodes, "Cost center"), ("ParentDepartmentCode", departmentCodes, "Parent department")]);
        Merge(departmentCodes, parsed.Departments.Select(x => Val(x, "Code")));
        AddRows("gradePayComponents", parsed.GradePayComponents, "ComponentCode", "ComponentName", required: ["GradeCode", "ComponentCode", "ComponentName"], known: new HashSet<string>(StringComparer.OrdinalIgnoreCase), rows,
            refs: [("GradeCode", gradeCodes, "Grade")]);
        AddGradePayComponentSanityRows(parsed.GradePayComponents, rows);
        AddRows("designations", parsed.Designations, "Code", "TitleEn", required: ["Code", "TitleEn"], known: new HashSet<string>(StringComparer.OrdinalIgnoreCase), rows,
            refs: [("DepartmentCode", departmentCodes, "Department"), ("GradeCode", gradeCodes, "Grade")]);

        var parentMap = parsed.Departments
            .Where(x => !string.IsNullOrWhiteSpace(Val(x, "Code")) && !string.IsNullOrWhiteSpace(Val(x, "ParentDepartmentCode")))
            .ToDictionary(x => Val(x, "Code"), x => Val(x, "ParentDepartmentCode"), StringComparer.OrdinalIgnoreCase);
        foreach (var start in parentMap.Keys)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
            var cursor = parentMap[start];
            while (!string.IsNullOrWhiteSpace(cursor) && parentMap.TryGetValue(cursor, out var next))
            {
                if (!seen.Add(cursor))
                {
                    rows.Add(new ImportRowResult(0, start, "Department hierarchy", ImportRowStatus.Error, [$"ParentDepartmentCode creates a cycle at '{cursor}'"], []));
                    break;
                }
                cursor = next;
            }
        }

        var errors = rows.Count(x => x.Status == ImportRowStatus.Error);
        return new OrganizationStructureImportResult(
            Received: parsed.TotalRows,
            Errors: errors,
            Warnings: rows.Count(x => x.Status == ImportRowStatus.Warning),
            Rows: rows,
            HasBlockingErrors: errors > 0,
            Committed: false,
            Applied: new Dictionary<string, int>());
    }

    private static void AddRows(string section, IReadOnlyList<Dictionary<string, string>> source, string codeKey, string nameKey, string[] required, HashSet<string> known, List<ImportRowResult> rows, (string Key, HashSet<string> Known, string Label)[]? refs = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < source.Count; i++)
        {
            var row = source[i];
            var code = Val(row, codeKey);
            var errors = required.Where(key => string.IsNullOrWhiteSpace(Val(row, key))).Select(key => $"{key} is required").ToList();
            var warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(code) && !seen.Add(code)) errors.Add($"Duplicate {codeKey} '{code}' in {section}");
            if (!string.IsNullOrWhiteSpace(code) && known.Contains(code))
                warnings.Add($"{section} record '{code}' already exists and will be updated");
            foreach (var (key, knownRefs, label) in refs ?? [])
            {
                var value = Val(row, key);
                if (!string.IsNullOrWhiteSpace(value) && !knownRefs.Contains(value))
                    errors.Add($"{label} reference '{value}' not found");
            }
            var status = errors.Count > 0 ? ImportRowStatus.Error : warnings.Count > 0 ? ImportRowStatus.Warning : ImportRowStatus.Ok;
            rows.Add(new ImportRowResult(i + 2, $"{section}:{code}", Val(row, nameKey), status, errors, warnings));
        }
    }

    private static void AddGradeSanityRows(IReadOnlyList<Dictionary<string, string>> source, List<ImportRowResult> rows)
    {
        for (var i = 0; i < source.Count; i++)
        {
            var row = source[i];
            var code = Val(row, "Code");
            var min = Dec(row, "MinSalary");
            var mid = Dec(row, "MidSalary");
            var max = Dec(row, "MaxSalary");
            var errors = new List<string>();
            var warnings = new List<string>();
            if (min < 0 || mid < 0 || max < 0) errors.Add("Salary range cannot contain negative values");
            if (max > 0 && min > max) errors.Add("MinSalary cannot exceed MaxSalary");
            if (mid > 0 && min > 0 && mid < min) warnings.Add("MidSalary is below MinSalary");
            if (mid > 0 && max > 0 && mid > max) warnings.Add("MidSalary is above MaxSalary");
            if (min == 0 && max == 0) warnings.Add("Grade has no salary range; employee salary eligibility cannot be enforced");
            if (errors.Count == 0 && warnings.Count == 0) continue;
            rows.Add(new ImportRowResult(i + 2, $"grades:{code}", Val(row, "Name"), errors.Count > 0 ? ImportRowStatus.Error : ImportRowStatus.Warning, errors, warnings));
        }
    }

    private static void AddGradePayComponentSanityRows(IReadOnlyList<Dictionary<string, string>> source, List<ImportRowResult> rows)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < source.Count; i++)
        {
            var row = source[i];
            var gradeCode = Val(row, "GradeCode");
            var code = Val(row, "ComponentCode");
            var calculationType = Val(row, "CalculationType");
            var amount = Dec(row, "Amount");
            var percentage = Dec(row, "Percentage");
            var errors = new List<string>();
            var warnings = new List<string>();
            if (string.Equals(calculationType, "PercentOfBasic", StringComparison.OrdinalIgnoreCase) && percentage <= 0)
                errors.Add("PercentOfBasic components require a positive Percentage");
            if (string.Equals(calculationType, "Fixed", StringComparison.OrdinalIgnoreCase) && amount <= 0)
                warnings.Add("Fixed pay component has no positive Amount");
            if (percentage < 0 || percentage > 100) errors.Add("Percentage must be between 0 and 100");
            if (string.Equals(calculationType, "PercentOfBasic", StringComparison.OrdinalIgnoreCase))
                totals[gradeCode] = totals.GetValueOrDefault(gradeCode) + percentage;
            if (errors.Count == 0 && warnings.Count == 0) continue;
            rows.Add(new ImportRowResult(i + 2, $"gradePayComponents:{gradeCode}/{code}", Val(row, "ComponentName"), errors.Count > 0 ? ImportRowStatus.Error : ImportRowStatus.Warning, errors, warnings));
        }

        foreach (var (gradeCode, total) in totals.Where(x => x.Value > 100m))
        {
            rows.Add(new ImportRowResult(0, $"gradePayComponents:{gradeCode}", "Component percentage total", ImportRowStatus.Warning, [],
                [$"PercentOfBasic components total {total:0.##}% for grade {gradeCode}; confirm this is intentional"]));
        }
    }

    private static void Merge(HashSet<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) target.Add(value);
    }

    private static ParsedOrgPackage ParsePackage(OrganizationStructureImportRequest req) => new(
        Csv.Parse(req.CompaniesCsv ?? string.Empty),
        Csv.Parse(req.BranchesCsv ?? string.Empty),
        Csv.Parse(req.CostCentersCsv ?? string.Empty),
        Csv.Parse(req.DepartmentsCsv ?? string.Empty),
        Csv.Parse(req.GradesCsv ?? string.Empty),
        Csv.Parse(req.GradePayComponentsCsv ?? string.Empty),
        Csv.Parse(req.DesignationsCsv ?? string.Empty));

    private static string Val(Dictionary<string, string> row, string key, string fallback = "") => row.GetValueOrDefault(key, fallback).Trim();
    private static bool Bool(Dictionary<string, string> row, string key, bool fallback) => !row.TryGetValue(key, out var v) ? fallback : !string.Equals(v.Trim(), "false", StringComparison.OrdinalIgnoreCase);
    private static int Int(Dictionary<string, string> row, string key, int fallback = 0) => int.TryParse(Val(row, key), out var v) ? v : fallback;
    private static decimal Dec(Dictionary<string, string> row, string key) => decimal.TryParse(Val(row, key), out var v) ? v : 0m;
    private Guid GetTenantId() => Guid.Parse(User.FindFirst("tenant_id")!.Value);
    private Guid? GetUserId() => Guid.TryParse(User.FindFirst("sub")?.Value, out var id) ? id : null;
}

public record OrganizationStructureImportRequest(
    string? CompaniesCsv,
    string? BranchesCsv,
    string? CostCentersCsv,
    string? DepartmentsCsv,
    string? GradesCsv,
    string? GradePayComponentsCsv,
    string? DesignationsCsv);

public record OrganizationStructureImportResult(
    int Received,
    int Errors,
    int Warnings,
    IReadOnlyList<ImportRowResult> Rows,
    bool HasBlockingErrors,
    bool Committed,
    IReadOnlyDictionary<string, int> Applied);

internal record ParsedOrgPackage(
    List<Dictionary<string, string>> Companies,
    List<Dictionary<string, string>> Branches,
    List<Dictionary<string, string>> CostCenters,
    List<Dictionary<string, string>> Departments,
    List<Dictionary<string, string>> Grades,
    List<Dictionary<string, string>> GradePayComponents,
    List<Dictionary<string, string>> Designations)
{
    public int TotalRows => Companies.Count + Branches.Count + CostCenters.Count + Departments.Count + Grades.Count + GradePayComponents.Count + Designations.Count;
}
