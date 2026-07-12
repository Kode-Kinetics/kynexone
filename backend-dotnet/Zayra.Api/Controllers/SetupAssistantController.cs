using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Setup;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/setup-assistant")]
[Authorize(Roles = "Admin,HR Manager")]
public class SetupAssistantController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ISetupAssistantService _assistant;

    public SetupAssistantController(ZayraDbContext db, ISetupAssistantService assistant)
    {
        _db = db;
        _assistant = assistant;
    }

    /// <summary>Generate a proposed starter configuration — does NOT write anything.</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] CompanyProfile profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.CountryCode))
            return BadRequest(new { message = "Country is required." });
        var result = await _assistant.GenerateAsync(profile, ct);
        return Ok(result);
    }

    /// <summary>Persist an approved draft. Idempotent — existing codes are skipped, not duplicated.</summary>
    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] ApplySetupRequest req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var d = req.Draft;
        var counts = new Dictionary<string, int>();
        void Bump(string k, int n) => counts[k] = counts.GetValueOrDefault(k) + n;

        // ── Entity context: company → branch. Config rows are explicitly wired
        // to this legal entity/branch when available, instead of floating tenant-wide.
        Company? company = null;
        if (!string.IsNullOrWhiteSpace(req.LegalEntityName))
        {
            company = await _db.Companies.FirstOrDefaultAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.LegalNameEn == req.LegalEntityName.Trim(), ct);
            if (company is null)
            {
                company = new Company
                {
                    TenantId = tenantId,
                    LegalNameEn = req.LegalEntityName.Trim(),
                    TradeName = req.LegalEntityName.Trim(),
                    CountryCode = req.CountryCode,
                    Jurisdiction = $"{req.CountryCode}-default",
                    DefaultCurrency = string.IsNullOrWhiteSpace(req.CurrencyCode) ? "USD" : req.CurrencyCode,
                    IsActive = true,
                    ApprovalStatus = CompanyApprovalStatuses.Active,
                    CreatedBy = GetUserId()
                };
                _db.Companies.Add(company);
                Bump("companies", 1);
            }
        }
        company ??= await _db.Companies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted, ct);

        var branchByCode = await _db.Branches.Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .ToDictionaryAsync(x => x.Code.ToUpperInvariant(), ct);
        foreach (var b in d.Branches)
        {
            if (company is null || string.IsNullOrWhiteSpace(b.Code) || string.IsNullOrWhiteSpace(b.NameEn)) continue;
            if (branchByCode.TryGetValue(b.Code.ToUpperInvariant(), out var existing))
            {
                existing.NameEn = b.NameEn;
                existing.City = b.City;
                existing.CountryCode = req.CountryCode;
                existing.CompanyId = company.Id;
                existing.IsHeadOffice = b.IsHeadOffice;
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                var branch = new Branch
                {
                    TenantId = tenantId,
                    CompanyId = company.Id,
                    Code = b.Code,
                    NameEn = b.NameEn,
                    City = b.City,
                    CountryCode = req.CountryCode,
                    IsHeadOffice = b.IsHeadOffice,
                    IsActive = true,
                    CreatedBy = GetUserId()
                };
                _db.Branches.Add(branch);
                branchByCode[b.Code.ToUpperInvariant()] = branch;
                Bump("branches", 1);
            }
        }
        var defaultBranch = branchByCode.Values.FirstOrDefault(x => company is null || x.CompanyId == company.Id);

        // ── Org: departments → grades/pay scale → cost centers → designations ─
        var existingDept = await _db.Departments.Where(x => x.TenantId == tenantId)
            .ToDictionaryAsync(x => x.Code.ToUpper(), x => x.Id, ct);
        var deptEntities = await _db.Departments.Where(x => x.TenantId == tenantId)
            .ToDictionaryAsync(x => x.Code.ToUpper(), ct);
        foreach (var dep in d.Departments)
        {
            if (existingDept.ContainsKey(dep.Code.ToUpper())) continue;
            var entity = new Department { TenantId = tenantId, BranchId = defaultBranch?.Id, Code = dep.Code, NameEn = dep.NameEn, IsActive = true, CreatedBy = GetUserId() };
            _db.Departments.Add(entity);
            existingDept[dep.Code.ToUpper()] = entity.Id;
            deptEntities[dep.Code.ToUpper()] = entity;
            Bump("departments", 1);
        }

        var gradeByCode = await _db.Grades.Where(x => x.TenantId == tenantId)
            .ToDictionaryAsync(x => x.Code.ToUpperInvariant(), ct);
        foreach (var g in d.Grades)
        {
            if (gradeByCode.TryGetValue(g.Code.ToUpperInvariant(), out var existing))
            {
                existing.Name = g.Name;
                existing.Band = g.Band;
                existing.Level = g.Level;
                existing.MinSalary = g.MinSalary;
                existing.MidSalary = g.MidSalary;
                existing.MaxSalary = g.MaxSalary;
                existing.Currency = string.IsNullOrWhiteSpace(g.Currency) ? req.CurrencyCode : g.Currency;
                existing.IsActive = true;
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                var grade = new Grade
                {
                    TenantId = tenantId,
                    Code = g.Code,
                    Name = g.Name,
                    Band = g.Band,
                    Level = g.Level,
                    MinSalary = g.MinSalary,
                    MidSalary = g.MidSalary,
                    MaxSalary = g.MaxSalary,
                    Currency = string.IsNullOrWhiteSpace(g.Currency) ? req.CurrencyCode : g.Currency,
                    IsActive = true,
                    CreatedBy = GetUserId()
                };
                _db.Grades.Add(grade);
                gradeByCode[g.Code.ToUpperInvariant()] = grade;
                Bump("grades", 1);
            }
        }

        foreach (var component in d.GradePayComponents)
        {
            if (!gradeByCode.TryGetValue(component.GradeCode.ToUpperInvariant(), out var grade)) continue;
            var exists = await _db.GradePayScaleComponents.AnyAsync(x =>
                x.TenantId == tenantId && x.GradeId == grade.Id && x.ComponentCode == component.ComponentCode, ct);
            if (exists) continue;
            _db.GradePayScaleComponents.Add(new GradePayScaleComponent
            {
                TenantId = tenantId,
                GradeId = grade.Id,
                ComponentCode = component.ComponentCode,
                ComponentName = component.ComponentName,
                ComponentType = component.ComponentType,
                CalculationType = component.CalculationType,
                Amount = component.Amount,
                Percentage = component.Percentage,
                IsTaxable = component.IsTaxable,
                Frequency = component.Frequency,
                SortOrder = d.GradePayComponents.IndexOf(component) + 1,
                IsActive = true
            });
            Bump("gradePayComponents", 1);
        }

        var costCenterByCode = await _db.CostCenters.Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .ToDictionaryAsync(x => x.Code.ToUpperInvariant(), ct);
        foreach (var cc in d.CostCenters)
        {
            if (costCenterByCode.ContainsKey(cc.Code.ToUpperInvariant())) continue;
            var entity = new CostCenter { TenantId = tenantId, CompanyId = company?.Id, Code = cc.Code, Name = cc.Name, IsActive = true, CreatedBy = GetUserId() };
            _db.CostCenters.Add(entity);
            costCenterByCode[cc.Code.ToUpperInvariant()] = entity;
            if (!string.IsNullOrWhiteSpace(cc.DepartmentCode) && deptEntities.TryGetValue(cc.DepartmentCode.ToUpperInvariant(), out var dept))
                dept.CostCenterId = entity.Id;
            Bump("costCenters", 1);
        }

        var existingDesig = (await _db.Designations.Where(x => x.TenantId == tenantId)
            .Select(x => x.Code).ToListAsync(ct)).Select(c => c.ToUpper()).ToHashSet();
        foreach (var ds in d.Designations)
        {
            if (!existingDesig.Add(ds.Code.ToUpper())) continue;
            Guid? deptId = !string.IsNullOrWhiteSpace(ds.DepartmentCode) && existingDept.TryGetValue(ds.DepartmentCode.ToUpper(), out var id) ? id : null;
            Guid? gradeId = !string.IsNullOrWhiteSpace(ds.GradeCode) && gradeByCode.TryGetValue(ds.GradeCode.ToUpperInvariant(), out var g) ? g.Id : null;
            _db.Designations.Add(new Designation
            {
                TenantId = tenantId, Code = ds.Code, TitleEn = ds.TitleEn, DepartmentId = deptId,
                GradeId = gradeId, JobGrade = ds.GradeCode, JobLevel = ds.JobLevel, IsManagerRole = ds.IsManagerRole, LevelRank = ds.LevelRank, IsActive = true,
                CreatedBy = GetUserId(),
            });
            Bump("designations", 1);
        }

        // ── Leave types ──────────────────────────────────────────────────────
        var existingLeave = (await _db.LeaveTypes.Where(x => x.TenantId == tenantId)
            .Select(x => x.Code).ToListAsync(ct)).Select(c => c.ToUpper()).ToHashSet();
        foreach (var lt in d.LeaveTypes)
        {
            if (!existingLeave.Add(lt.Code.ToUpper())) continue;
            _db.LeaveTypes.Add(new LeaveType
            {
                TenantId = tenantId, Code = lt.Code, NameEn = lt.NameEn, Category = lt.Category, IsPaid = lt.IsPaid,
                MaxConsecutiveDays = lt.MaxConsecutiveDays, RequiresAttachment = lt.RequiresAttachment, ColorCode = lt.ColorCode, IsActive = true,
            });
            Bump("leaveTypes", 1);
        }

        // ── Shifts ───────────────────────────────────────────────────────────
        var existingShift = (await _db.ShiftDefinitions.Where(x => x.TenantId == tenantId)
            .Select(x => x.Code).ToListAsync(ct)).Select(c => c.ToUpper()).ToHashSet();
        foreach (var sh in d.Shifts)
        {
            if (!existingShift.Add(sh.Code.ToUpper())) continue;
            if (!TimeOnly.TryParse(sh.Start, out var start) || !TimeOnly.TryParse(sh.End, out var end)) continue;
            _db.ShiftDefinitions.Add(new ShiftDefinition
            {
                TenantId = tenantId, Code = sh.Code, Name = sh.Name, StartTime = start, EndTime = end,
                BreakMinutes = sh.BreakMinutes, Color = sh.Color, IsActive = true,
            });
            Bump("shifts", 1);
        }

        // ── Working week (localization upsert) ───────────────────────────────
        if (d.WorkingWeek is not null)
        {
            var loc = await _db.TenantLocalizationSettings.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
            if (loc is null) { loc = new TenantLocalizationSetting { TenantId = tenantId }; _db.TenantLocalizationSettings.Add(loc); }
            loc.WorkWeek = d.WorkingWeek.WorkWeek;
            loc.WeekStartDay = d.WorkingWeek.WeekStartDay;
            if (!string.IsNullOrWhiteSpace(req.CurrencyCode)) loc.CurrencyCode = req.CurrencyCode;
            Bump("workingWeek", 1);
        }

        // ── Pay components (under a default salary structure) ─────────────────
        if (d.PayComponents.Count > 0)
        {
            var structureCode = company is null ? "DEFAULT" : $"DEFAULT-{company.Id.ToString()[..8]}";
            var structure = await _db.SalaryStructures.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Code == structureCode, ct);
            if (structure is null)
            {
                structure = new SalaryStructure
                {
                    TenantId = tenantId, CompanyId = company?.Id, Code = structureCode, Name = company is null ? "Default Structure" : $"{company.LegalNameEn} Default Structure",
                    Currency = string.IsNullOrWhiteSpace(req.CurrencyCode) ? "AED" : req.CurrencyCode, IsActive = true,
                    CreatedBy = GetUserId()
                };
                _db.SalaryStructures.Add(structure);
            }
            var existingComp = (await _db.SalaryComponents.Where(x => x.TenantId == tenantId && x.SalaryStructureId == structure.Id)
                .Select(x => x.Code).ToListAsync(ct)).Select(c => c.ToUpper()).ToHashSet();
            foreach (var pc in d.PayComponents)
            {
                if (!existingComp.Add(pc.Code.ToUpper())) continue;
                _db.SalaryComponents.Add(new SalaryComponent
                {
                    TenantId = tenantId, SalaryStructureId = structure.Id, Code = pc.Code, Name = pc.Name,
                    ComponentType = pc.ComponentType, CalculationType = pc.CalculationType,
                    Amount = pc.Amount, Percentage = pc.Percentage, IsTaxable = pc.IsTaxable, IsActive = true,
                });
                Bump("payComponents", 1);
            }
        }

        // ── Governance / import behaviour / employee ID rule ─────────────────
        if (d.EmployeeIdRule is not null)
        {
            var companyId = company?.Id;
            var rule = await _db.EmployeeIdRules.FirstOrDefaultAsync(x => x.TenantId == tenantId && !x.IsDeleted && (x.CompanyId == companyId || x.CompanyId == null), ct);
            if (rule is null)
            {
                rule = new EmployeeIdRule { TenantId = tenantId, CompanyId = company?.Id, CreatedBy = GetUserId() };
                _db.EmployeeIdRules.Add(rule);
                Bump("employeeIdRules", 1);
            }
            rule.CompanyPrefix = d.EmployeeIdRule.CompanyPrefix;
            rule.UseCountryPrefix = d.EmployeeIdRule.UseCountryPrefix;
            rule.UseBranchPrefix = d.EmployeeIdRule.UseBranchPrefix;
            rule.UseDepartmentPrefix = d.EmployeeIdRule.UseDepartmentPrefix;
            rule.UseYear = d.EmployeeIdRule.UseYear;
            rule.PaddingLength = d.EmployeeIdRule.PaddingLength;
            rule.NextSequence = Math.Max(1, d.EmployeeIdRule.NextSequence);
            rule.AllowManualOverride = d.EmployeeIdRule.AllowManualOverride;
            rule.IsActive = true;
            rule.UpdatedAtUtc = DateTime.UtcNow;
            rule.UpdatedBy = GetUserId();
        }

        if (d.HrConfig is not null)
        {
            var config = await _db.TenantHrConfigs.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
            if (config is null)
            {
                config = new TenantHrConfig { TenantId = tenantId };
                _db.TenantHrConfigs.Add(config);
                Bump("hrConfig", 1);
            }
            config.UseDeptHeadApproval = d.HrConfig.UseDeptHeadApproval;
            config.UseHrFinalApproval = d.HrConfig.UseHrFinalApproval;
            config.UseSupervisorBeforeManager = d.HrConfig.UseSupervisorBeforeManager;
            config.AllowDottedLineApproval = d.HrConfig.AllowDottedLineApproval;
            config.AutoCreateDeptOnImport = d.HrConfig.AutoCreateDeptOnImport;
            config.AutoCreateDesignationOnImport = d.HrConfig.AutoCreateDesignationOnImport;
            config.RequireImportPreviewBeforeCommit = d.HrConfig.RequireImportPreviewBeforeCommit;
            config.AllowCrossDeptManager = d.HrConfig.AllowCrossDeptManager;
            config.AllowCrossLocationManager = d.HrConfig.AllowCrossLocationManager;
            config.RequireCostCenterForPayroll = d.HrConfig.RequireCostCenterForPayroll;
            config.RequireGradeForApprovalPolicy = d.HrConfig.RequireGradeForApprovalPolicy;
            config.UpdatedAtUtc = DateTime.UtcNow;
        }

        // ── Statutory rules ──────────────────────────────────────────────────
        if (d.StatutoryRules.Count > 0)
        {
            var country = (req.CountryCode ?? "").Trim().ToUpperInvariant();
            var existingRules = (await _db.StatutoryRules.Where(x => x.TenantId == tenantId)
                .Select(x => x.RuleKey).ToListAsync(ct)).Select(c => c.ToUpper()).ToHashSet();
            foreach (var r in d.StatutoryRules)
            {
                if (!existingRules.Add(r.RuleKey.ToUpper())) continue;
                _db.StatutoryRules.Add(new StatutoryRule
                {
                    TenantId = tenantId, CountryCode = country, Jurisdiction = $"{country}-default",
                    RuleKey = r.RuleKey, RuleValue = r.RuleValue, DataType = r.DataType, Description = r.Description,
                    EffectiveFrom = DateTime.UtcNow,
                });
                Bump("statutoryRules", 1);
            }
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { applied = counts, total = counts.Values.Sum() });
    }

    private Guid GetTenantId() => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid? GetUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id
        : Guid.TryParse(User.FindFirstValue("sub"), out id) ? id : null;
}

public record ApplySetupRequest(SetupDraft Draft, string CountryCode, string CurrencyCode, string? LegalEntityName = null);
