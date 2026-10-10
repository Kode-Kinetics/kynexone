using System.Security.Claims;
using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Setup;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Application.Organization;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Setup;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/setup-assistant")]
[Authorize(Roles = "Admin,HR Manager")]
public class SetupAssistantController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ISetupAssistantService _assistant;
    private readonly IAuditService _audit;

    public SetupAssistantController(ZayraDbContext db, ISetupAssistantService assistant, IAuditService audit)
    {
        _db = db;
        _assistant = assistant;
        _audit = audit;
    }

    /// <summary>Generate a proposed starter configuration — does NOT write anything.</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] CompanyProfile profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.CountryCode))
            return BadRequest(new { message = "Country is required." });
        // Preview calls a model, and a model call with no tenant behind it cannot be recorded
        // against anyone. Refuse rather than spend tokens on an unattributable request.
        if (!Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId))
            return Unauthorized(new { message = "Tenant context is missing." });
        if (profile.CompanyId.HasValue)
        {
            if (!this.GetEntityScope().CanAccessCompany(profile.CompanyId.Value)) return Forbid();
            var target = await ScopedBypass.TenantWide(_db.Companies, tenantId,
                    "Resolve the authorised Setup Studio target across the tenant company catalog.")
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == profile.CompanyId.Value && !c.IsDeleted, ct);
            if (target is null) return RefuseConfiguration(["The selected company no longer exists in this workspace."]);
            if (!target.IsActive || target.ApprovalStatus != CompanyApprovalStatuses.Active)
                return RefuseConfiguration([$"Company '{target.LegalNameEn}' is not active. Finish its approval before opening Setup Studio."]);
            if (!string.Equals(target.LegalNameEn.Trim(), profile.LegalEntityName?.Trim(), StringComparison.OrdinalIgnoreCase)
                || CountryCodeStandard.NormalizeToIso2(target.CountryCode) != CountryCodeStandard.NormalizeToIso2(profile.CountryCode)
                || !string.Equals(target.DefaultCurrency, profile.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                return RefuseConfiguration(["The reviewed identity, country or currency does not match the selected company. Refresh Setup Studio before generating a draft."]);
        }
        var inputProblems = SetupAssistantService.ValidateConfiguration(profile);
        if (inputProblems.Count > 0) return RefuseConfiguration(inputProblems);
        if (profile.Configuration?.PolicyDocumentId is { } policyId)
        {
            var sourceProblem = await ValidatePolicySourceAsync(tenantId,
                new(policyId, profile.Configuration.PolicySourceHash ?? ""), ct);
            if (sourceProblem is not null) return RefuseConfiguration([sourceProblem]);
        }
        var fieldSourceProblems = await ValidateFieldSourcesAsync(tenantId, profile.Configuration?.PolicyFieldSources, ct);
        if (fieldSourceProblems.Count > 0) return RefuseConfiguration(fieldSourceProblems);
        var result = await _assistant.GenerateAsync(new SetupRequester(tenantId, GetUserId(), CallerRole()), profile, ct);
        if (profile.Configuration is not null)
        {
            var request = new ApplySetupRequest(result.Draft, profile.CountryCode, profile.CurrencyCode, profile.LegalEntityName, profile.CompanyId);
            var referenceProblems = await ValidatePolicyReferencesAsync(tenantId, request, null, preview: true, ct);
            if (referenceProblems.Count > 0) return RefuseConfiguration(referenceProblems);
        }
        if (result.Draft.AttendancePolicy is not null || result.Draft.OvertimePolicy is not null)
        {
            var branch = await ResolveSetupBranchAsync(tenantId, new(result.Draft, profile.CountryCode, profile.CurrencyCode, profile.LegalEntityName, profile.CompanyId), null, ct);
            if (branch.Problem is not null) return RefuseConfiguration([branch.Problem]);
            result = result with { Notes = result.Notes.Append($"Attendance and overtime policies are assigned to branch '{branch.Code}' of the reviewed legal entity.").ToList() };
        }
        return Ok(result);
    }

    /// <summary>Persist an approved draft. Idempotent — existing codes are skipped, not duplicated.</summary>
    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] ApplySetupRequest req, CancellationToken ct)
    {
        if (!HasPermission("organization.setup.apply")) return Forbid();
        if (!_db.Database.IsRelational()) return await ApplyCore(req, ct);
        // Setup policies have composite scopes rather than a single natural unique key. Serialize
        // setup writers for this tenant, then re-read all gates INSIDE the transaction. A retry
        // revalidates committed rows and cannot create a competing active leave policy.
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            if (_db.Database.IsNpgsql())
            {
                var key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"SETUP:{GetTenantId():N}"));
                var lockId = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(key.AsSpan(0, 8));
                await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockId})", ct);
            }
            var result = await ApplyCore(req, ct);
            if (result is OkObjectResult) await tx.CommitAsync(ct);
            else { await tx.RollbackAsync(ct); _db.ChangeTracker.Clear(); }
            return result;
        });
    }

    private async Task<IActionResult> ApplyCore(ApplySetupRequest req, CancellationToken ct)
    {
        if (!HasPermission("organization.setup.apply")) return Forbid();
        if (req.Draft is null) return BadRequest(new { message = "A reviewed draft is required." });
        // One spelling of the country everywhere it is written (company, branches, leave policies, calendars, rules):
        // the forms store ISO codes upper-case, and "sa" would otherwise sit beside "SA".
        req = req with { CountryCode = CountryCodeStandard.NormalizeToIso2(req.CountryCode) ?? (req.CountryCode ?? string.Empty).Trim().ToUpperInvariant() };
        var tenantId = GetTenantId();
        var d = req.Draft;
        if (d.PolicySource is { } source)
        {
            var sourceProblem = await ValidatePolicySourceAsync(tenantId, source, ct);
            if (sourceProblem is not null) return RefuseConfiguration([sourceProblem]);
        }
        var reviewedDraftBytes = JsonSerializer.SerializeToUtf8Bytes(d);
        if (reviewedDraftBytes.Length > 131072)
            return RefuseConfiguration(["The reviewed draft exceeds the 128 KiB setup audit limit. Apply smaller sections."]);
        var values = SetupAssistantService.ValidateDraftValues(d, req.CurrencyCode);
        if (values.Count > 0) return RefuseConfiguration(values);
        if (d.LeavePolicies.Count > 0 && !HasPermission("leave.policy_manage")
            || d.OvertimePolicy is not null && !HasPermission("overtime.policy_manage")
            || (d.HrConfig is not null || d.Grades.Count > 0 || d.GradePayComponents.Count > 0) && !HasPermission("organization.write")
            || d.PayComponents.Count > 0 && !HasPermission("payroll.structure_manage")
            || d.StatutoryRules.Count > 0 && !HasPermission("payroll.rates.manage")
            || d.BenefitPlans is { Count: > 0 } && !HasPermission("employees.approve")) return Forbid();
        foreach (var rule in d.StatutoryRules)
        {
            if (GosiStatutoryValues.TenantWriteRefusal(rule.RuleKey) is { } statutoryRefusal)
                return UnprocessableEntity(statutoryRefusal);
            if (StatutoryValueUnits.Validate(rule.RuleKey, rule.DataType, rule.RuleValue) is { } unitError)
                return BadRequest(StatutoryValueUnits.Refusal(unitError));
            if (rule.RuleKey is "employment.probation_months" or "employment.notice_period_days" or "payroll.pay_cycle" or "leave.year_basis")
                return RefuseConfiguration([ $"Rule '{rule.RuleKey}' has no runtime consumer and cannot be applied. Configure supported policy fields instead." ]);
        }
        // Before anything is written: a reviewed draft can still have been edited below the Saudi
        // statutory floor, and applying half of it first would leave the tenant half-configured.
        if (await RefuseBelowStatutoryLeaveFloorAsync(tenantId, req, ct) is { } floorRefusal)
            return floorRefusal;
        // The Setup forms' gates, before anything is written (P1, the same class as the org-structure import).
        var gate = await EvaluateOrgGatesAsync(tenantId, req, ct);
        gate.Problems.AddRange(await ValidateFieldSourcesAsync(tenantId, d.PolicyFieldSources, ct));
        if (d.PolicyFieldSources is { Count: > 0 })
        {
            var ids = d.PolicyFieldSources.Where(s => s is not null).Select(s => s.DocumentId).Distinct().ToArray();
            if (await _db.PolicyDocuments.AnyAsync(p => p.TenantId == tenantId && ids.Contains(p.Id)
                && p.CompanyId != null && p.CompanyId != (gate.Company == null ? null : gate.Company.Id), ct))
                gate.Problems.Add("An extracted field references a policy belonging to another company.");
        }
        if (d.PolicySource is { } sourceReference)
        {
            var sourceCompanyId = await _db.PolicyDocuments.Where(p => p.TenantId == tenantId && p.Id == sourceReference.DocumentId)
                .Select(p => p.CompanyId).SingleAsync(ct);
            if (sourceCompanyId.HasValue && sourceCompanyId != gate.Company?.Id)
                gate.Problems.Add("The source policy belongs to a different company. Select a policy for the reviewed legal entity.");
        }
        gate.Problems.AddRange(await ValidatePolicyReferencesAsync(tenantId, req, gate.Company, preview: false, ct));
        if (gate.Problems.Count > 0)
            return UnprocessableEntity(new
            {
                error = "setup_apply_refused",
                message = "Nothing was applied. " + string.Join(" ", gate.Problems),
                problems = gate.Problems,
            });
        var counts = new Dictionary<string, int>();
        void Bump(string k, int n) => counts[k] = counts.GetValueOrDefault(k) + n;
        // One audit row per organisation entity, with the Setup forms' action names, saved with the data.
        var audited = new List<(string Action, string Entity, Guid Id, string Key)>();
        var auditChanges = new Dictionary<Guid, object>();
        void Audit(string action, string entity, Guid id, string key) => audited.Add((action, entity, id, key));

        // ── Entity context: company → branch. Config rows are explicitly wired
        // to this legal entity/branch when available, instead of floating tenant-wide.
        Company? company = gate.Company;
        if (!string.IsNullOrWhiteSpace(req.LegalEntityName))
        {
            if (company is null)
            {
                company = new Company
                {
                    TenantId = tenantId,
                    LegalNameEn = req.LegalEntityName.Trim(),
                    TradeName = req.LegalEntityName.Trim(),
                    CountryCode = req.CountryCode,
                    Jurisdiction = $"{req.CountryCode}-default",
                    // No "USD" fallback: an unstated currency is left for the tenant to state
                    // rather than silently booked as dollars on the legal entity.
                    DefaultCurrency = req.CurrencyCode?.Trim().ToUpperInvariant() ?? string.Empty,
                    // Draft-approval tenants get an inactive Draft awaiting platform approval, exactly as the form does.
                    IsActive = !gate.CreateAsDraft,
                    ApprovalStatus = gate.CreateAsDraft ? CompanyApprovalStatuses.Draft : CompanyApprovalStatuses.Active,
                    CreatedBy = GetUserId()
                };
                _db.Companies.Add(company);
                Audit("organization.company_created", nameof(Company), company.Id, company.LegalNameEn);
                Bump("companies", 1);
            }
        }
        company ??= await _db.Companies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted, ct);

        var branchByCode = company is null
            ? new Dictionary<string, Branch>()
            : await _db.Branches.Where(x => x.TenantId == tenantId && x.CompanyId == company.Id && !x.IsDeleted)
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
                Audit("organization.branch_updated", nameof(Branch), existing.Id, b.Code);
            }
            else
            {
                var branch = new Branch
                {
                    TenantId = tenantId,
                    CompanyId = company.Id,
                    Code = OrgCodes.Normalize(b.Code),
                    NameEn = b.NameEn,
                    City = b.City,
                    CountryCode = req.CountryCode,
                    IsHeadOffice = b.IsHeadOffice,
                    IsActive = true,
                    CreatedBy = GetUserId()
                };
                _db.Branches.Add(branch);
                Audit("organization.branch_created", nameof(Branch), branch.Id, b.Code);
                branchByCode[b.Code.ToUpperInvariant()] = branch;
                Bump("branches", 1);
            }
        }
        var defaultBranch = gate.DefaultBranchCode is { } defaultCode && branchByCode.TryGetValue(defaultCode.ToUpperInvariant(), out var chosenBranch)
            ? chosenBranch : null;

        // ── Org: departments → grades/pay scale → cost centers → designations ─
        var existingDept = await _db.Departments.Where(x => x.TenantId == tenantId)
            .ToDictionaryAsync(x => x.Code.ToUpper(), x => x.Id, ct);
        var deptEntities = await _db.Departments.Where(x => x.TenantId == tenantId)
            .ToDictionaryAsync(x => x.Code.ToUpper(), ct);
        foreach (var dep in d.Departments)
        {
            if (existingDept.ContainsKey(dep.Code.ToUpper())) continue;
            var entity = new Department { TenantId = tenantId, BranchId = defaultBranch?.Id, Code = OrgCodes.Normalize(dep.Code), NameEn = dep.NameEn, IsActive = true, CreatedBy = GetUserId() };
            _db.Departments.Add(entity);
            Audit("organization.department_created", nameof(Department), entity.Id, dep.Code);
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
                var before = new DraftGrade(existing.Code, existing.Name, existing.Band, existing.Level,
                    existing.MinSalary, existing.MidSalary, existing.MaxSalary, existing.Currency);
                existing.Name = g.Name;
                existing.Band = g.Band;
                existing.Level = g.Level;
                existing.MinSalary = g.MinSalary;
                existing.MidSalary = g.MidSalary;
                existing.MaxSalary = g.MaxSalary;
                // The workspace's currency, not the draft's. Same reason as the preview path.
                existing.Currency = string.IsNullOrWhiteSpace(req.CurrencyCode) ? g.Currency : req.CurrencyCode;
                existing.IsActive = true;
                existing.UpdatedAtUtc = DateTime.UtcNow;
                Audit("organization.grade_updated", nameof(Grade), existing.Id, g.Code);
                auditChanges[existing.Id] = new { before, after = new DraftGrade(existing.Code, existing.Name,
                    existing.Band, existing.Level, existing.MinSalary, existing.MidSalary, existing.MaxSalary, existing.Currency) };
            }
            else
            {
                var grade = new Grade
                {
                    TenantId = tenantId,
                    Code = OrgCodes.Normalize(g.Code),
                    Name = g.Name,
                    Band = g.Band,
                    Level = g.Level,
                    MinSalary = g.MinSalary,
                    MidSalary = g.MidSalary,
                    MaxSalary = g.MaxSalary,
                    Currency = string.IsNullOrWhiteSpace(req.CurrencyCode) ? g.Currency : req.CurrencyCode,
                    IsActive = true,
                    CreatedBy = GetUserId()
                };
                _db.Grades.Add(grade);
                Audit("organization.grade_created", nameof(Grade), grade.Id, g.Code);
                gradeByCode[g.Code.ToUpperInvariant()] = grade;
                Bump("grades", 1);
            }
        }

        // Release A (R1): grade benefits live in one place, Benefits by grade. For a release_a tenant the legacy pay-scale
        // lines are frozen, so the draft's grade pay lines are not written — and the response says so, rather than
        // reporting them as applied or dropping them silently. Tenants without the flag are unchanged.
        var skipped = new Dictionary<string, object>();
        var gradePayComponents = d.GradePayComponents;
        if (gradePayComponents.Count > 0 && await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId, ct))
        {
            skipped["gradePayComponents"] = new
            {
                count = gradePayComponents.Count,
                reasonCode = "moved_to_benefits_by_grade",
                reason = "Grade allowances and benefits are set in Benefits by grade for this workspace, so the draft's grade pay lines were not saved. Set them there.",
            };
            gradePayComponents = [];
        }
        foreach (var component in gradePayComponents)
        {
            if (!gradeByCode.TryGetValue(component.GradeCode.ToUpperInvariant(), out var grade)) continue;
            var exists = await _db.GradePayScaleComponents.AnyAsync(x =>
                x.TenantId == tenantId && x.GradeId == grade.Id && x.ComponentCode.ToUpper() == component.ComponentCode.ToUpper(), ct);
            if (exists) continue;
            var payComponent = new GradePayScaleComponent
            {
                TenantId = tenantId,
                GradeId = grade.Id,
                ComponentCode = component.ComponentCode.ToUpperInvariant(),
                ComponentName = component.ComponentName,
                ComponentType = component.ComponentType,
                CalculationType = component.CalculationType,
                Amount = component.Amount,
                Percentage = component.Percentage,
                IsTaxable = component.IsTaxable,
                Frequency = component.Frequency,
                SortOrder = d.GradePayComponents.IndexOf(component) + 1,
                IsActive = true
            };
            _db.GradePayScaleComponents.Add(payComponent);
            Audit("organization.grade_pay_component_created", nameof(GradePayScaleComponent), payComponent.Id, $"{grade.Code}/{component.ComponentCode}");
            Bump("gradePayComponents", 1);
        }

        var costCenterByCode = await _db.CostCenters.Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .ToDictionaryAsync(x => x.Code.ToUpperInvariant(), ct);
        foreach (var cc in d.CostCenters)
        {
            if (costCenterByCode.ContainsKey(cc.Code.ToUpperInvariant())) continue;
            var entity = new CostCenter { TenantId = tenantId, CompanyId = company?.Id, Code = OrgCodes.Normalize(cc.Code), Name = cc.Name, IsActive = true, CreatedBy = GetUserId() };
            _db.CostCenters.Add(entity);
            Audit("organization.cost_center_created", nameof(CostCenter), entity.Id, cc.Code);
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
            var designation = new Designation
            {
                TenantId = tenantId, Code = OrgCodes.Normalize(ds.Code), TitleEn = ds.TitleEn, DepartmentId = deptId,
                GradeId = gradeId, JobGrade = ds.GradeCode, JobLevel = ds.JobLevel, IsManagerRole = ds.IsManagerRole, LevelRank = ds.LevelRank, IsActive = true,
                CreatedBy = GetUserId(),
            };
            _db.Designations.Add(designation);
            Audit("organization.designation_created", nameof(Designation), designation.Id, ds.Code);
            Bump("designations", 1);
        }

        // ── Leave types ──────────────────────────────────────────────────────
        // Keyed by code and kept, not discarded: the entitlement policies below attach to a leave
        // type by id, and a type added in this same unit of work has no id in the database yet.
        var leaveByCode = await _db.LeaveTypes.Where(x => x.TenantId == tenantId)
            .ToDictionaryAsync(x => x.Code.ToUpperInvariant(), ct);
        foreach (var lt in d.LeaveTypes)
        {
            if (leaveByCode.ContainsKey(lt.Code.ToUpperInvariant())) continue;
            var leaveType = new LeaveType
            {
                TenantId = tenantId, Code = lt.Code, NameEn = lt.NameEn, Category = lt.Category, IsPaid = lt.IsPaid,
                MaxConsecutiveDays = lt.MaxConsecutiveDays, RequiresAttachment = lt.RequiresAttachment, ColorCode = lt.ColorCode, IsActive = true,
            };
            _db.LeaveTypes.Add(leaveType);
            leaveByCode[lt.Code.ToUpperInvariant()] = leaveType;
            Bump("leaveTypes", 1);
        }

        // ── Leave entitlement ────────────────────────────────────────────────
        // The days themselves. Without these the leave types above exist and grant nobody
        // anything, because LeaveType carries no entitlement — LeavePolicy does.
        var policyCompanyId = company?.Id;
        var savedPolicies = await _db.LeavePolicies.Where(x => x.TenantId == tenantId && x.CompanyId == policyCompanyId).ToListAsync(ct);
        var savedEligibility = await _db.LeavePolicyEligibilities.Where(x => x.TenantId == tenantId && x.IsActive).ToListAsync(ct);
        foreach (var lp in d.LeavePolicies)
        {
            var leaveType = leaveByCode[lp.LeaveTypeCode.ToUpperInvariant()];
            var policyName = string.IsNullOrWhiteSpace(lp.Name) ? $"{leaveType.NameEn} Policy" : lp.Name.Trim();
            Guid? gradeId = string.IsNullOrWhiteSpace(lp.GradeCode) ? null : gradeByCode[lp.GradeCode.ToUpperInvariant()].Id;
            Guid? departmentId = string.IsNullOrWhiteSpace(lp.DepartmentCode) ? null : existingDept[lp.DepartmentCode.ToUpperInvariant()];
            var employmentType = (lp.EmploymentType ?? string.Empty).Trim();
            var existingPolicy = savedPolicies.FirstOrDefault(p => p.LeaveTypeId == leaveType.Id
                && string.Equals(p.Name, policyName, StringComparison.OrdinalIgnoreCase)
                && SameLeaveScope(p, savedEligibility, gradeId, departmentId, employmentType, req.CountryCode ?? string.Empty, company?.Id));
            if (existingPolicy is not null) continue;
            var policy = new LeavePolicy
            {
                TenantId = tenantId,
                Name = policyName,
                LeaveTypeId = leaveType.Id,
                CountryCode = req.CountryCode ?? string.Empty,
                CompanyId = company?.Id,
                AppliesOnProbation = lp.AppliesOnProbation,
                AnnualEntitlementDays = Math.Clamp(lp.AnnualEntitlementDays, 0m, 365m),
                AccrualMethod = string.Equals(lp.AccrualMethod, "Monthly", StringComparison.OrdinalIgnoreCase) ? "Monthly" : "Yearly",
                ProratePartialMonths = lp.ProratePartialMonths,
                EmploymentType = employmentType,
                // Both fixed at zero, not taken from the draft. LeavePoliciesController refuses any
                // non-zero cap or expiry because this build has no year-end rollover to consult one;
                // writing one here would be a way round that refusal, not a feature.
                CarryForwardMax = 0m,
                CarryForwardExpiry = 0,
                EncashmentAllowed = lp.EncashmentAllowed,
                EncashmentMaxDays = Math.Clamp(lp.EncashmentMaxDays, 0m, 365m),
                MinimumDaysPerRequest = lp.MinimumDaysPerRequest > 0 ? lp.MinimumDaysPerRequest : 1m,
                MaximumDaysPerRequest = Math.Clamp(lp.MaximumDaysPerRequest, 0m, 365m),
                NoticeRequiredDays = Math.Clamp(lp.NoticeRequiredDays, 0, 365),
                WeekendsIncluded = lp.WeekendsIncluded,
                PublicHolidaysIncluded = lp.PublicHolidaysIncluded,
                PayrollImpact = string.Equals(lp.PayrollImpact, "Unpaid", StringComparison.OrdinalIgnoreCase) ? "Unpaid" : "Full",
                // The draft was reviewed item by item and approved by someone with the apply
                // permission, which is the whole of this screen's job. Leaving it Draft would mean
                // nobody's leave worked until they opened another screen and said yes again.
                Status = "Active",
            };
            _db.LeavePolicies.Add(policy);
            savedPolicies.Add(policy);
            if (gradeId.HasValue || departmentId.HasValue)
            {
                var eligibility = new LeavePolicyEligibility
                {
                    TenantId = tenantId, LeavePolicyId = policy.Id, CompanyId = company?.Id,
                    CountryCode = req.CountryCode ?? string.Empty, GradeId = gradeId, DepartmentId = departmentId,
                    EmploymentType = employmentType, IsActive = true,
                };
                _db.LeavePolicyEligibilities.Add(eligibility);
                savedEligibility.Add(eligibility);
            }
            Audit("leave.policy_created", nameof(LeavePolicy), policy.Id, policy.Name);
            Bump("leavePolicies", 1);
        }

        // ── Company benefit plans and grade eligibility ──────────────────────
        // All dates, canonical references, permissions and release-A gates were checked before
        // tracking any write. Reapplying an identical plan skips it; changed plans are refused.
        var existingBenefitCodes = (await _db.BenefitPlans.Where(b => b.TenantId == tenantId && b.CompanyId == policyCompanyId)
            .Select(b => b.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var benefit in d.BenefitPlans ?? [])
        {
            if (!existingBenefitCodes.Add(benefit.Code)) continue;
            var plan = new BenefitPlan
            {
                TenantId = tenantId, CompanyId = company!.Id, Code = benefit.Code.ToUpperInvariant(),
                Name = benefit.Name.Trim(), PlanType = benefit.PlanType.Trim(), Currency = benefit.Currency.ToUpperInvariant(),
                EffectiveFrom = DateOnly.ParseExact(benefit.EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                EffectiveTo = string.IsNullOrEmpty(benefit.EffectiveTo) ? null : DateOnly.ParseExact(benefit.EffectiveTo, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                RequiresEnrollment = benefit.RequiresEnrollment, IsActive = true, CreatedBy = GetUserId(),
            };
            _db.BenefitPlans.Add(plan);
            foreach (var gradeCode in benefit.GradeCodes ?? [])
                _db.BenefitEligibilityRules.Add(new BenefitEligibilityRule
                {
                    TenantId = tenantId, BenefitPlanId = plan.Id, CompanyId = company.Id,
                    GradeId = gradeByCode[gradeCode.ToUpperInvariant()].Id,
                    EffectiveFrom = plan.EffectiveFrom, EffectiveTo = plan.EffectiveTo,
                    IsActive = true, CreatedBy = GetUserId(),
                });
            Audit("benefits.plan_created", nameof(BenefitPlan), plan.Id, plan.Code);
            Bump("benefitPlans", 1);
            Bump("benefitEligibilityRules", benefit.GradeCodes?.Count ?? 0);
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

        // ── Working week + localization (one upsert on one row) ──────────────
        // TenantLocalizationSetting is constructed with America/New_York, MM/DD/YYYY and a Monday
        // week start. Until the localization draft existed, this block wrote the work week and the
        // currency over that and left the rest, so every Gulf tenant configured by this assistant
        // kept a New York clock and a US date format on every timestamp in the product.
        if (d.WorkingWeek is not null || d.Localization is not null)
        {
            var loc = await _db.TenantLocalizationSettings.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
            if (loc is null) { loc = new TenantLocalizationSetting { TenantId = tenantId }; _db.TenantLocalizationSettings.Add(loc); }
            if (d.WorkingWeek is not null)
            {
                loc.WorkWeek = d.WorkingWeek.WorkWeek;
                loc.WeekStartDay = d.WorkingWeek.WeekStartDay;
                Bump("workingWeek", 1);
            }
            if (d.Localization is not null)
            {
                loc.DefaultLanguage = string.Equals(d.Localization.DefaultLanguage, "ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en";
                loc.RtlEnabled = d.Localization.RtlEnabled;
                loc.CalendarSystem = string.Equals(d.Localization.CalendarSystem, "Hijri", StringComparison.OrdinalIgnoreCase) ? "Hijri" : "Gregorian";
                if (!string.IsNullOrWhiteSpace(d.Localization.DefaultTimezone)) loc.DefaultTimezone = d.Localization.DefaultTimezone.Trim();
                if (!string.IsNullOrWhiteSpace(d.Localization.DateFormat)) loc.DateFormat = d.Localization.DateFormat.Trim();
                loc.HijriDatesEnabled = d.Localization.HijriDatesEnabled;
                Bump("localization", 1);
            }
            if (!string.IsNullOrWhiteSpace(req.CountryCode)) loc.CountryCode = req.CountryCode.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(req.CurrencyCode)) loc.CurrencyCode = req.CurrencyCode;
            loc.UpdatedAtUtc = DateTime.UtcNow;
        }

        // ── Public holidays ──────────────────────────────────────────────────
        // Calendars are owned by the reviewed company (or explicitly tenant-wide when there is no
        // company target). Re-applying tops up only that exact calendar; it must never discover a
        // sibling or shared calendar merely because country and year match.
        if (d.HolidayCalendar is not null && d.HolidayCalendar.Holidays.Count > 0)
        {
            var year = d.HolidayCalendar.CalendarYear;
            var countryCode = (req.CountryCode ?? string.Empty).Trim().ToUpperInvariant();
            var calendarCompanyId = company?.Id;
            var calendar = await _db.PublicHolidayCalendars.FirstOrDefaultAsync(
                x => x.TenantId == tenantId
                    && x.CountryCode == countryCode
                    && x.CalendarYear == year
                    && x.CompanyId == calendarCompanyId,
                ct);
            if (calendar is null)
            {
                calendar = new PublicHolidayCalendar
                {
                    TenantId = tenantId,
                    Name = d.HolidayCalendar.Name,
                    CountryCode = countryCode,
                    CompanyId = company?.Id,
                    BranchId = defaultBranch?.Id,
                    CalendarYear = year,
                    IsActive = true,
                };
                _db.PublicHolidayCalendars.Add(calendar);
                Bump("holidayCalendars", 1);
            }

            var existingDates = (await _db.PublicHolidays
                .Where(x => x.TenantId == tenantId && x.CalendarId == calendar.Id)
                .Select(x => x.Date).ToListAsync(ct)).ToHashSet();
            foreach (var h in d.HolidayCalendar.Holidays)
            {
                if (!DateOnly.TryParse(h.Date, out var date)) continue;
                if (!existingDates.Add(date)) continue;
                _db.PublicHolidays.Add(new PublicHoliday
                {
                    TenantId = tenantId,
                    CalendarId = calendar.Id,
                    NameEn = h.NameEn,
                    NameAr = h.NameAr ?? string.Empty,
                    Date = date,
                    IsRecurring = h.IsRecurring,
                    IsOptional = h.IsOptional,
                    HolidayType = string.IsNullOrWhiteSpace(h.HolidayType) ? "National" : h.HolidayType,
                    Notes = h.Notes ?? string.Empty,
                });
                Bump("publicHolidays", 1);
            }
        }

        // ── Attendance policy ────────────────────────────────────────────────
        if (d.AttendancePolicy is not null && !string.IsNullOrWhiteSpace(d.AttendancePolicy.Code))
        {
            var ap = d.AttendancePolicy;
            var existingAttendance = await _db.AttendancePolicies
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Code.ToUpper() == ap.Code.ToUpper(), ct);
            if (existingAttendance is null)
            {
                var policy = new AttendancePolicy
                {
                    TenantId = tenantId,
                    Code = ap.Code.ToUpperInvariant(),
                    Name = ap.Name,
                    BranchId = defaultBranch?.Id,
                    GraceMinutes = Math.Clamp(ap.GraceMinutes, 0, 120),
                    LateThresholdMinutes = Math.Clamp(ap.LateThresholdMinutes, 0, 480),
                    EarlyExitThresholdMinutes = Math.Clamp(ap.EarlyExitThresholdMinutes, 0, 480),
                    HalfDayThresholdMinutes = Math.Clamp(ap.HalfDayThresholdMinutes, 0, 960),
                    AbsentThresholdMinutes = Math.Clamp(ap.AbsentThresholdMinutes, 0, 960),
                    StandardWorkMinutes = Math.Clamp(ap.StandardWorkMinutes, 60, 960),
                    BreakMinutes = Math.Clamp(ap.BreakMinutes, 0, 240),
                    RoundingRule = RoundingOrDefault(ap.RoundingRule, "NearestMinute"),
                    RequiresOvertimeApproval = ap.RequiresOvertimeApproval,
                    AllowAbsenceToLeaveConversion = ap.AllowAbsenceToLeaveConversion,
                    IsActive = true,
                };
                _db.AttendancePolicies.Add(policy);
                Audit("attendance.policy_created", nameof(AttendancePolicy), policy.Id, policy.Code);
                Bump("attendancePolicies", 1);
            }
        }

        // ── Overtime policy + its multipliers ────────────────────────────────
        if (d.OvertimePolicy is not null && !string.IsNullOrWhiteSpace(d.OvertimePolicy.Code))
        {
            var op = d.OvertimePolicy;
            var overtimePolicy = await _db.OvertimePolicies
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.Code.ToUpper() == op.Code.ToUpper(), ct);
            if (overtimePolicy is null)
            {
                overtimePolicy = new OvertimePolicy
                {
                    TenantId = tenantId,
                    Code = op.Code.ToUpperInvariant(),
                    Name = op.Name,
                    BranchId = defaultBranch?.Id,
                    HourlyRateBasis = string.Equals(op.HourlyRateBasis, "GrossSalary", StringComparison.OrdinalIgnoreCase) ? "GrossSalary" : "BasicSalary",
                    StandardMonthlyHours = Math.Clamp(op.StandardMonthlyHours, 1, 400),
                    MinimumMinutes = Math.Clamp(op.MinimumMinutes, 0, 480),
                    MaximumMinutesPerDay = Math.Clamp(op.MaximumMinutesPerDay, 0, 960),
                    MonthlyCapMinutes = Math.Clamp(op.MonthlyCapMinutes, 0, 30000),
                    RoundingRule = RoundingOrDefault(op.RoundingRule, "Nearest15"),
                    RequiresApproval = op.RequiresApproval,
                    AllowCompOffConversion = op.AllowCompOffConversion,
                    IsActive = true,
                    CreatedBy = GetUserId(),
                };
                _db.OvertimePolicies.Add(overtimePolicy);
                Audit("overtime.policy_created", nameof(OvertimePolicy), overtimePolicy.Id, overtimePolicy.Code);
                Bump("overtimePolicies", 1);
            }

            var existingCategories = (await _db.OvertimeMultipliers
                .Where(x => x.TenantId == tenantId && x.OvertimePolicyId == overtimePolicy.Id)
                .Select(x => x.DayCategory).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var m in op.Multipliers)
            {
                if (string.IsNullOrWhiteSpace(m.DayCategory)) continue;
                // A multiplier under 1 pays an overtime hour less than an ordinary one; the payroll
                // run floors it at the statutory rate anyway, so storing one only misleads the
                // screen that displays it.
                if (m.Multiplier < 1m || m.Multiplier > 5m) continue;
                if (!existingCategories.Add(m.DayCategory)) continue;
                _db.OvertimeMultipliers.Add(new OvertimeMultiplier
                {
                    TenantId = tenantId,
                    OvertimePolicyId = overtimePolicy.Id,
                    DayCategory = string.Equals(m.DayCategory, "Weekend", StringComparison.OrdinalIgnoreCase) ? "Weekend"
                        : string.Equals(m.DayCategory, "PublicHoliday", StringComparison.OrdinalIgnoreCase) ? "PublicHoliday" : "RegularDay",
                    Multiplier = m.Multiplier,
                    IsActive = true,
                });
                Bump("overtimeMultipliers", 1);
            }
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
            var rule = await _db.EmployeeIdRules.FirstOrDefaultAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.CompanyId == companyId, ct);
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
            var before = config is null ? null : HrConfigSnapshot(config);
            if (config is null)
            {
                // Onboarding tenants default to ADVISORY establishment enforcement (budget warns, never
                // blocks) so a fresh org skeleton can be populated without headcount budgets fighting the
                // import. Existing tenants and the TenantHrConfig PUT path are untouched.
                config = new TenantHrConfig
                {
                    TenantId = tenantId,
                    EstablishmentEnforcementMode = Zayra.Api.Infrastructure.Organization.EstablishmentGuardService.ModeAdvisory,
                };
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
            Audit("organization.hr_config_updated", nameof(TenantHrConfig), config.Id, "governance");
            auditChanges[config.Id] = new { before, after = HrConfigSnapshot(config) };
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
                // GOSI rates and the contributory-wage ceiling are STATUTORY: payroll reads the platform row
                // only, so a tenant value here would be saved and never applied. Refused with a code.
                // See Infrastructure/Payroll/GosiStatutoryValues.cs.
                if (GosiStatutoryValues.TenantWriteRefusal(r.RuleKey) is { } gosiRefusal)
                    return UnprocessableEntity(gosiRefusal);
                // UNIT GATE — the wizard writes statutory rates too, so it is held to the same
                // rule as the admin surfaces: a rate is a decimal FRACTION (0.09 = 9%).
                // See Infrastructure/Payroll/StatutoryValueUnits.cs.
                if (StatutoryValueUnits.Validate(r.RuleKey, r.DataType, r.RuleValue) is { } unitError)
                    return BadRequest(StatutoryValueUnits.Refusal(unitError));
                _db.StatutoryRules.Add(new StatutoryRule
                {
                    TenantId = tenantId, CountryCode = country, Jurisdiction = $"{country}-default",
                    RuleKey = r.RuleKey, RuleValue = r.RuleValue, DataType = r.DataType, Description = r.Description,
                    EffectiveFrom = DateTime.UtcNow,
                });
                Bump("statutoryRules", 1);
            }
        }

        // The per-entity audit rows and the bulk marker are saved WITH the data, in this one save: there is no
        // applied draft without its trail (the marker used to be a second save after the first).
        var context = Context(tenantId);
        var at = DateTime.UtcNow;
        foreach (var (action, entity, id, key) in audited.DistinctBy(a => (a.Action, a.Id)))
        {
            var row = AuthAuditEntry.Create(Guid.NewGuid(), at, action, entity, id.ToString(), context,
                JsonSerializer.Serialize(new { source = "setup_assistant", key, change = auditChanges.GetValueOrDefault(id) }));
            if (entity is not nameof(Grade) and not nameof(GradePayScaleComponent) and not nameof(TenantHrConfig)) row.CompanyId = company?.Id;
            _db.AuditLogs.Add(row);
        }
        var appliedAudit = AuthAuditEntry.Create(Guid.NewGuid(), at, "setup.assistant_applied", "SetupDraft", "bulk", context,
            JsonSerializer.Serialize(new
            {
                countryCode = req.CountryCode,
                currencyCode = req.CurrencyCode,
                legalEntityName = req.LegalEntityName,
                applied = counts,
                total = counts.Values.Sum(),
                entities = audited.Count,
                policyContractVersion = 1,
                reviewedDraftSha256 = Convert.ToHexString(SHA256.HashData(reviewedDraftBytes)),
                // Bounded typed DTO: no employee data, source document, or AI prompt field exists here.
                reviewedDraft = d,
                skipped,
            }));
        appliedAudit.CompanyId = company?.Id;
        _db.AuditLogs.Add(appliedAudit);
        // Recheck creation limits inside the apply transaction immediately before the single save.
        var creatingCompany = company is not null && _db.Entry(company).State == EntityState.Added;
        if (creatingCompany && _db.Database.IsRelational())
        {
            var creation = await CompanyCreationGate.EvaluateAsync(_db, tenantId, ct);
            if (!creation.Allowed) return RefuseConfiguration([creation.Message]);
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { applied = counts, total = counts.Values.Sum(), skipped });
    }

    private sealed record OrgGateResult(List<string> Problems, Company? Company, bool CreateAsDraft, string? DefaultBranchCode = null);
    private sealed record BranchSelection(string? Code, string? Problem = null);

    private async Task<BranchSelection> ResolveSetupBranchAsync(Guid tenantId, ApplySetupRequest request, Company? company, CancellationToken ct)
    {
        if (company is null && request.CompanyId.HasValue)
            company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c =>
                c.TenantId == tenantId && c.Id == request.CompanyId.Value && !c.IsDeleted, ct);
        if (company is null && !string.IsNullOrWhiteSpace(request.LegalEntityName))
        {
            var name = request.LegalEntityName.Trim().ToUpperInvariant();
            company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && !c.IsDeleted && c.LegalNameEn.ToUpper() == name, ct);
        }
        List<Branch> existing = company is null ? [] : await _db.Branches.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.CompanyId == company.Id && !b.IsDeleted && b.IsActive).ToListAsync(ct);
        var choices = existing.ToDictionary(b => b.Code, b => b.IsHeadOffice, StringComparer.OrdinalIgnoreCase);
        foreach (var branch in request.Draft.Branches.Where(b => !string.IsNullOrWhiteSpace(b.Code) && !string.IsNullOrWhiteSpace(b.NameEn)))
            choices[branch.Code] = branch.IsHeadOffice;
        var requestedHeadOffices = request.Draft.Branches.Where(b => b.IsHeadOffice && !string.IsNullOrWhiteSpace(b.Code)).ToArray();
        if (requestedHeadOffices.Length == 1) return new(requestedHeadOffices[0].Code);
        var headOffices = choices.Where(b => b.Value).Select(b => b.Key).ToArray();
        if (headOffices.Length == 1) return new(headOffices[0]);
        if (choices.Count == 1) return new(choices.Keys.Single());
        if (choices.Count == 0) return new(null, request.Draft.AttendancePolicy is not null || request.Draft.OvertimePolicy is not null
            ? "Add a branch for the reviewed company before configuring attendance or overtime. Setup cannot create an implicit workspace-wide policy." : null);
        return new(null, "Attendance and overtime need an unambiguous branch. Mark exactly one reviewed branch as head office, or configure branch policies separately.");
    }

    private static DraftHrConfig HrConfigSnapshot(TenantHrConfig c) => new(
        c.UseDeptHeadApproval, c.UseHrFinalApproval, c.UseSupervisorBeforeManager, c.AllowDottedLineApproval,
        c.AutoCreateDeptOnImport, c.AutoCreateDesignationOnImport, c.RequireImportPreviewBeforeCommit,
        c.AllowCrossDeptManager, c.AllowCrossLocationManager, c.RequireCostCenterForPayroll, c.RequireGradeForApprovalPolicy);

    private static bool SameLeaveScope(LeavePolicy policy, List<LeavePolicyEligibility> eligibility,
        Guid? gradeId, Guid? departmentId, string employmentType, string country, Guid? companyId)
    {
        // Legacy filters represent narrower populations. They are not interchangeable with
        // a company-wide or canonical-ID rule even when its displayed name is identical.
        if (policy.BranchId.HasValue || !string.IsNullOrEmpty(policy.DepartmentName) || !string.IsNullOrEmpty(policy.Grade)
            || !string.IsNullOrEmpty(policy.ContractType) || !string.IsNullOrEmpty(policy.Gender)
            || !string.IsNullOrEmpty(policy.CountryCode) && CountryCodeStandard.NormalizeToIso2(policy.CountryCode) != CountryCodeStandard.NormalizeToIso2(country)
            || !string.Equals(policy.EmploymentType, employmentType, StringComparison.OrdinalIgnoreCase)) return false;
        var rows = eligibility.Where(e => e.LeavePolicyId == policy.Id && e.IsActive).ToArray();
        if (!gradeId.HasValue && !departmentId.HasValue) return rows.Length == 0;
        return rows.Length == 1 && rows[0].GradeId == gradeId && rows[0].DepartmentId == departmentId
            && (rows[0].CompanyId is null || rows[0].CompanyId == companyId) && rows[0].BranchId is null
            && (string.IsNullOrEmpty(rows[0].CountryCode) || CountryCodeStandard.NormalizeToIso2(rows[0].CountryCode) == CountryCodeStandard.NormalizeToIso2(country))
            && (string.IsNullOrEmpty(rows[0].EmploymentType) || string.Equals(rows[0].EmploymentType, employmentType, StringComparison.OrdinalIgnoreCase))
            && string.IsNullOrEmpty(rows[0].ContractType);
    }

    private async Task<List<string>> ValidateFieldSourcesAsync(Guid tenantId, List<SetupPolicyFieldSource>? sources, CancellationToken ct)
    {
        var errors = new List<string>();
        if (sources is null) return errors;
        if (sources.Count > 30 || sources.Any(s => s is null)) return ["Extracted source references are invalid."];
        if (sources.Select(s => s.Target + "." + s.Field).Distinct().Count() != sources.Count)
            return ["Each extracted field must have one reviewed source reference."];
        foreach (var group in sources.GroupBy(s => new { s.DocumentId, s.ContentSha256 }))
        {
            var problem = await ValidatePolicySourceAsync(tenantId, new(group.Key.DocumentId, group.Key.ContentSha256), ct);
            if (problem is not null) { errors.Add(problem); continue; }
            var length = await _db.DocumentChunks.Where(c => c.TenantId == tenantId && c.DocumentId == group.Key.DocumentId)
                .SumAsync(c => c.Content.Length, ct);
            foreach (var field in group)
                if (field.Target is null || field.Field is null || !PolicyExtractionService.IsSupportedField(field.Target, field.Field)
                    || field.SourceStart < 0 || field.SourceLength is < 1 or > 4000 || (long)field.SourceStart + field.SourceLength > length)
                    errors.Add("An extracted field source location is invalid. Extract and review the policy again.");
        }
        return errors;
    }

    private async Task<string?> ValidatePolicySourceAsync(Guid tenantId, SetupPolicySourceReference source, CancellationToken ct)
    {
        if (source.ContentSha256 is null || !System.Text.RegularExpressions.Regex.IsMatch(source.ContentSha256, "^[A-Fa-f0-9]{64}$"))
            return "The source policy fingerprint is missing or invalid. Extract and review the policy again.";
        var doc = await _db.PolicyDocuments.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId
            && p.Id == source.DocumentId && !p.IsDeleted && p.Status == "Ready", ct);
        var scope = this.GetRequestScope();
        var allowed = doc is not null && (doc.CompanyId.HasValue
            ? scope.IsGroupLevel || scope.AuthorizedCompanyIds.Contains(doc.CompanyId.Value)
            : User.IsInRole("Admin") || doc.UploadedByUserId.HasValue && doc.UploadedByUserId == GetUserId());
        if (!allowed) return "The source policy is no longer accessible. Select an available policy and review again.";
        if (!string.Equals(doc!.ContentSha256, source.ContentSha256, StringComparison.OrdinalIgnoreCase))
            return "The source policy has changed. Extract and review the current version before applying.";
        return null;
    }

    private UnprocessableEntityObjectResult RefuseConfiguration(IEnumerable<string> problems)
    {
        var distinct = problems.Distinct().ToArray();
        return UnprocessableEntity(new
        {
            error = "setup_configuration_invalid",
            message = "Nothing was applied. " + string.Join(" ", distinct),
            problems = distinct,
        });
    }

    private async Task<List<string>> ValidatePolicyReferencesAsync(
        Guid tenantId, ApplySetupRequest req, Company? company, bool preview, CancellationToken ct)
    {
        var errors = new List<string>();
        var d = req.Draft;
        if (d.LeavePolicies.Count > 0 && string.IsNullOrWhiteSpace(req.LegalEntityName))
            errors.Add("Leave policies require an explicit reviewed legal entity.");
        if (company is null && req.CompanyId.HasValue)
            company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c =>
                c.TenantId == tenantId && c.Id == req.CompanyId.Value && !c.IsDeleted, ct);
        if (company is null && !string.IsNullOrWhiteSpace(req.LegalEntityName))
        {
            var legalName = req.LegalEntityName.Trim().ToUpperInvariant();
            company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c =>
                c.TenantId == tenantId && !c.IsDeleted && c.LegalNameEn.ToUpper() == legalName, ct);
        }
        if (company is not null && (d.LeavePolicies.Count > 0 || d.BenefitPlans is { Count: > 0 }))
        {
            if (CountryCodeStandard.NormalizeToIso2(company.CountryCode) != CountryCodeStandard.NormalizeToIso2(req.CountryCode)
                || !string.Equals(company.DefaultCurrency, req.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                errors.Add("The reviewed country and currency must match the selected legal entity before applying its policies.");
        }
        var grades = (await _db.Grades.AsNoTracking().Where(g => g.TenantId == tenantId && g.IsActive)
            .Select(g => g.Code).ToListAsync(ct)).Concat(d.Grades.Select(g => g.Code)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var departments = await _db.Departments.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive)
            .Select(x => new { x.Code, x.BranchId }).ToListAsync(ct);
        var departmentCodes = departments.Select(x => x.Code).Concat(d.Departments.Select(x => x.Code)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var types = (await _db.LeaveTypes.AsNoTracking().Where(t => t.TenantId == tenantId && t.IsActive)
            .Select(t => t.Code).ToListAsync(ct)).Concat(d.LeaveTypes.Select(t => t.Code)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var savedBranches = await _db.Branches.AsNoTracking().Where(b => b.TenantId == tenantId && !b.IsDeleted)
            .Select(b => new { b.Id, b.CompanyId, b.Code }).ToListAsync(ct);
        var branchCompanies = savedBranches.ToDictionary(b => b.Id, b => b.CompanyId);
        var selectedBranch = await ResolveSetupBranchAsync(tenantId, req, company, ct);
        bool BranchMatches(Guid? id) => id is null ? selectedBranch.Code is null
            : savedBranches.Any(b => b.Id == id && string.Equals(b.Code, selectedBranch.Code, StringComparison.OrdinalIgnoreCase));
        foreach (var policy in d.LeavePolicies)
        {
            if (!types.Contains(policy.LeaveTypeCode))
                errors.Add($"Leave policy '{policy.Name}' references unknown leave type '{policy.LeaveTypeCode}'. Include that leave type or use an existing code.");
            if (!string.IsNullOrWhiteSpace(policy.GradeCode) && !grades.Contains(policy.GradeCode))
                errors.Add($"Leave policy '{policy.Name}' references unknown or inactive grade '{policy.GradeCode}'.");
            if (!string.IsNullOrWhiteSpace(policy.DepartmentCode))
            {
                if (!departmentCodes.Contains(policy.DepartmentCode))
                    errors.Add($"Leave policy '{policy.Name}' references unknown or inactive department '{policy.DepartmentCode}'.");
                var department = departments.FirstOrDefault(x => string.Equals(x.Code, policy.DepartmentCode, StringComparison.OrdinalIgnoreCase));
                if (department?.BranchId is { } branch && branchCompanies.TryGetValue(branch, out var owner)
                    && (company is null || owner != company.Id))
                    errors.Add($"Leave policy '{policy.Name}' references a department outside the reviewed company.");
            }
        }
        if (!preview && company is not null && d.LeavePolicies.Count > 0)
        {
            var saved = await _db.LeavePolicies.AsNoTracking().Where(p => p.TenantId == tenantId && p.CompanyId == company.Id).ToListAsync(ct);
            var eligibility = await _db.LeavePolicyEligibilities.AsNoTracking().Where(e => e.TenantId == tenantId && e.IsActive).ToListAsync(ct);
            var typeIds = await _db.LeaveTypes.AsNoTracking().Where(t => t.TenantId == tenantId).ToDictionaryAsync(t => t.Code.ToUpper(), t => t.Id, ct);
            var gradeIds = await _db.Grades.AsNoTracking().Where(g => g.TenantId == tenantId).ToDictionaryAsync(g => g.Code.ToUpper(), g => g.Id, ct);
            var departmentIds = await _db.Departments.AsNoTracking().Where(g => g.TenantId == tenantId).ToDictionaryAsync(g => g.Code.ToUpper(), g => g.Id, ct);
            foreach (var policy in d.LeavePolicies)
            {
                if (!typeIds.TryGetValue(policy.LeaveTypeCode.ToUpperInvariant(), out var typeId)) continue;
                Guid? gradeId = !string.IsNullOrEmpty(policy.GradeCode) && gradeIds.TryGetValue(policy.GradeCode.ToUpperInvariant(), out var g) ? g : null;
                Guid? departmentId = !string.IsNullOrEmpty(policy.DepartmentCode) && departmentIds.TryGetValue(policy.DepartmentCode.ToUpperInvariant(), out var dep) ? dep : null;
                // A new grade/department cannot match an existing unscoped policy.
                var newScopeReference = !string.IsNullOrEmpty(policy.GradeCode) && gradeId is null || !string.IsNullOrEmpty(policy.DepartmentCode) && departmentId is null;
                var existing = newScopeReference ? null : saved.FirstOrDefault(p => p.LeaveTypeId == typeId
                    && SameLeaveScope(p, eligibility, gradeId, departmentId, (policy.EmploymentType ?? "").Trim(), req.CountryCode, company.Id));
                if (existing is not null && (!string.Equals(existing.Name, policy.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                    || existing.AnnualEntitlementDays != policy.AnnualEntitlementDays
                    || !string.Equals(existing.AccrualMethod, policy.AccrualMethod, StringComparison.OrdinalIgnoreCase)
                    || existing.ProratePartialMonths != policy.ProratePartialMonths || existing.EncashmentAllowed != policy.EncashmentAllowed
                    || existing.EncashmentMaxDays != policy.EncashmentMaxDays || existing.MinimumDaysPerRequest != policy.MinimumDaysPerRequest
                    || existing.MaximumDaysPerRequest != policy.MaximumDaysPerRequest || existing.NoticeRequiredDays != policy.NoticeRequiredDays
                    || existing.WeekendsIncluded != policy.WeekendsIncluded || existing.PublicHolidaysIncluded != policy.PublicHolidaysIncluded
                    || existing.AppliesOnProbation != policy.AppliesOnProbation || !string.Equals(existing.PayrollImpact, policy.PayrollImpact, StringComparison.OrdinalIgnoreCase)
                    || existing.Status != "Active"))
                    errors.Add($"Leave policy '{policy.Name}' already exists with different settings. Change it in Leave Policies; setup will not rewrite an existing entitlement.");
                foreach (var savedPolicy in saved.Where(p => p.LeaveTypeId == typeId && p.Status == "Active"
                    && p.BranchId is null && string.IsNullOrEmpty(p.DepartmentName) && string.IsNullOrEmpty(p.Grade)
                    && string.IsNullOrEmpty(p.ContractType) && string.IsNullOrEmpty(p.Gender)))
                {
                    var rows = eligibility.Where(e => e.LeavePolicyId == savedPolicy.Id).ToList();
                    if (rows.Count == 0)
                    {
                        if (SetupAssistantService.LeaveScopesConflict(policy.GradeCode, policy.DepartmentCode, policy.EmploymentType,
                            null, null, savedPolicy.EmploymentType))
                            errors.Add($"Leave policy '{policy.Name}' overlaps existing policy '{savedPolicy.Name}' without a clear broader/narrower scope.");
                        continue;
                    }
                    foreach (var row in rows)
                    {
                        var savedGrade = row.GradeId.HasValue ? gradeIds.FirstOrDefault(g => g.Value == row.GradeId).Key : null;
                        var savedDepartment = row.DepartmentId.HasValue ? departmentIds.FirstOrDefault(g => g.Value == row.DepartmentId).Key : null;
                        var savedEmployment = string.IsNullOrWhiteSpace(row.EmploymentType) ? savedPolicy.EmploymentType : row.EmploymentType;
                        if (SetupAssistantService.LeaveScopesConflict(policy.GradeCode, policy.DepartmentCode, policy.EmploymentType,
                            savedGrade, savedDepartment, savedEmployment))
                            errors.Add($"Leave policy '{policy.Name}' overlaps existing policy '{savedPolicy.Name}' without a clear broader/narrower scope. Resolve the policy populations in Leave Policies first.");
                    }
                }
            }
        }
        foreach (var component in d.GradePayComponents)
            if (!grades.Contains(component.GradeCode)) errors.Add($"Salary component '{component.ComponentCode}' references unknown grade '{component.GradeCode}'.");
        if (!preview && d.GradePayComponents.Count > 0 && !await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId, ct))
        {
            var savedComponents = await _db.GradePayScaleComponents.AsNoTracking().Where(c => c.TenantId == tenantId)
                .Join(_db.Grades.Where(g => g.TenantId == tenantId), c => c.GradeId, g => g.Id, (c, g) => new { Component = c, GradeCode = g.Code }).ToListAsync(ct);
            foreach (var component in d.GradePayComponents)
            {
                var existing = savedComponents.FirstOrDefault(c => string.Equals(c.GradeCode, component.GradeCode, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(c.Component.ComponentCode, component.ComponentCode, StringComparison.OrdinalIgnoreCase))?.Component;
                if (existing is not null && (existing.ComponentName != component.ComponentName || existing.ComponentType != component.ComponentType
                    || existing.CalculationType != component.CalculationType || existing.Amount != component.Amount
                    || existing.Percentage != component.Percentage || existing.IsTaxable != component.IsTaxable || existing.Frequency != component.Frequency || !existing.IsActive))
                    errors.Add($"Salary component '{component.GradeCode}/{component.ComponentCode}' already exists with different settings. Review changes in grade compensation; setup will not overwrite it.");
            }
        }
        if (!preview && d.PayComponents.Count > 0)
        {
            var structureCode = company is null ? "DEFAULT" : $"DEFAULT-{company.Id.ToString()[..8]}";
            var structure = await _db.SalaryStructures.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Code == structureCode, ct);
            if (structure is not null)
            {
                var savedComponents = await _db.SalaryComponents.AsNoTracking().Where(c => c.TenantId == tenantId && c.SalaryStructureId == structure.Id).ToListAsync(ct);
                foreach (var component in d.PayComponents)
                {
                    var existing = savedComponents.FirstOrDefault(c => string.Equals(c.Code, component.Code, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null && (existing.Name != component.Name || existing.ComponentType != component.ComponentType
                        || existing.CalculationType != component.CalculationType || existing.Amount != component.Amount
                        || existing.Percentage != component.Percentage || existing.IsTaxable != component.IsTaxable || !existing.IsActive))
                        errors.Add($"Salary component '{component.Code}' already exists with different settings. Review changes in salary structures; setup will not overwrite it.");
                }
            }
        }
        foreach (var designation in d.Designations)
        {
            if (!string.IsNullOrWhiteSpace(designation.GradeCode) && !grades.Contains(designation.GradeCode))
                errors.Add($"Designation '{designation.Code}' references unknown grade '{designation.GradeCode}'.");
            if (!string.IsNullOrWhiteSpace(designation.DepartmentCode) && !departmentCodes.Contains(designation.DepartmentCode))
                errors.Add($"Designation '{designation.Code}' references unknown department '{designation.DepartmentCode}'.");
        }
        if (!preview && d.AttendancePolicy is { } attendance)
        {
            var code = attendance.Code.ToUpperInvariant();
            var saved = await _db.AttendancePolicies.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Code.ToUpper() == code, ct);
            if (saved is not null && (saved.Name != attendance.Name || saved.GraceMinutes != attendance.GraceMinutes
                || saved.LateThresholdMinutes != attendance.LateThresholdMinutes || saved.EarlyExitThresholdMinutes != attendance.EarlyExitThresholdMinutes
                || saved.HalfDayThresholdMinutes != attendance.HalfDayThresholdMinutes || saved.AbsentThresholdMinutes != attendance.AbsentThresholdMinutes
                || saved.StandardWorkMinutes != attendance.StandardWorkMinutes || saved.BreakMinutes != attendance.BreakMinutes
                || !string.Equals(saved.RoundingRule, attendance.RoundingRule, StringComparison.OrdinalIgnoreCase)
                || saved.RequiresOvertimeApproval != attendance.RequiresOvertimeApproval || saved.AllowAbsenceToLeaveConversion != attendance.AllowAbsenceToLeaveConversion
                || !saved.IsActive || !BranchMatches(saved.BranchId) || saved.DepartmentId.HasValue || saved.GradeId.HasValue
                || saved.BranchId is { } branch && branchCompanies.TryGetValue(branch, out var owner) && owner != company?.Id))
                errors.Add($"Attendance policy '{attendance.Code}' already exists with different settings or company scope. Choose a new policy code or manage the existing policy.");
        }
        if (!preview && d.OvertimePolicy is { } overtime)
        {
            var code = overtime.Code.ToUpperInvariant();
            var saved = await _db.OvertimePolicies.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId && !p.IsDeleted && p.Code.ToUpper() == code, ct);
            if (saved is not null)
            {
                var multipliers = await _db.OvertimeMultipliers.AsNoTracking().Where(m => m.TenantId == tenantId && m.OvertimePolicyId == saved.Id && m.IsActive).ToListAsync(ct);
                if (saved.Name != overtime.Name || !string.Equals(saved.HourlyRateBasis, overtime.HourlyRateBasis, StringComparison.OrdinalIgnoreCase)
                    || saved.StandardMonthlyHours != overtime.StandardMonthlyHours || saved.MinimumMinutes != overtime.MinimumMinutes
                    || saved.MaximumMinutesPerDay != overtime.MaximumMinutesPerDay || saved.MonthlyCapMinutes != overtime.MonthlyCapMinutes
                    || !string.Equals(saved.RoundingRule, overtime.RoundingRule, StringComparison.OrdinalIgnoreCase)
                    || saved.RequiresApproval != overtime.RequiresApproval || saved.AllowCompOffConversion != overtime.AllowCompOffConversion
                    || !saved.IsActive || !BranchMatches(saved.BranchId) || saved.DepartmentId.HasValue || saved.GradeId.HasValue
                    || saved.BranchId is { } branch && branchCompanies.TryGetValue(branch, out var owner) && owner != company?.Id
                    || multipliers.Count != overtime.Multipliers.Count
                    || overtime.Multipliers.Any(m => !multipliers.Any(s => string.Equals(s.DayCategory, m.DayCategory, StringComparison.OrdinalIgnoreCase) && s.Multiplier == m.Multiplier)))
                    errors.Add($"Overtime policy '{overtime.Code}' already exists with different settings or company scope. Choose a new policy code or manage the existing policy.");
            }
        }
        if (d.BenefitPlans is { Count: > 0 } benefits)
        {
            if (string.IsNullOrWhiteSpace(req.LegalEntityName))
                errors.Add("Benefit plans require an explicit reviewed legal entity.");
            if (company is not null && !this.GetEntityScope().CanAccessCompany(company.Id))
                errors.Add("The benefit plan company is outside your company scope.");
            if (benefits.Any(b => b.GradeCodes is { Count: > 0 })
                && await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId, ct))
                errors.Add("Grade eligibility is managed in Benefits by grade for this workspace (moved_to_benefits_by_grade). Remove grade eligibility from this draft and configure it there.");
            foreach (var benefit in benefits)
            {
                foreach (var grade in benefit.GradeCodes ?? [])
                    if (!grades.Contains(grade)) errors.Add($"Benefit plan '{benefit.Code}' references unknown or inactive grade '{grade}'.");
                if (preview || company is null) continue;
                var code = benefit.Code.ToUpperInvariant();
                var existing = await _db.BenefitPlans.AsNoTracking().FirstOrDefaultAsync(b =>
                    b.TenantId == tenantId && b.CompanyId == company.Id && b.Code.ToUpper() == code, ct);
                if (existing is null) continue;
                var start = DateOnly.ParseExact(benefit.EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                DateOnly? end = string.IsNullOrEmpty(benefit.EffectiveTo) ? null : DateOnly.ParseExact(benefit.EffectiveTo, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var savedGrades = await _db.BenefitEligibilityRules.AsNoTracking()
                    .Where(r => r.TenantId == tenantId && r.BenefitPlanId == existing.Id && r.IsActive && r.GradeId.HasValue)
                    .Join(_db.Grades.Where(g => g.TenantId == tenantId), r => r.GradeId, g => (Guid?)g.Id, (r, g) => g.Code)
                    .ToListAsync(ct);
                var savedRules = await _db.BenefitEligibilityRules.AsNoTracking()
                    .Where(r => r.TenantId == tenantId && r.BenefitPlanId == existing.Id && r.IsActive).ToListAsync(ct);
                if (existing.IsDeleted || !existing.IsActive || existing.Name != benefit.Name.Trim() || existing.PlanType != benefit.PlanType.Trim()
                    || !string.Equals(existing.Currency, benefit.Currency, StringComparison.OrdinalIgnoreCase)
                    || existing.EffectiveFrom != start || existing.EffectiveTo != end || existing.RequiresEnrollment != benefit.RequiresEnrollment
                    || !savedGrades.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(benefit.GradeCodes ?? [])
                    || savedRules.Count != (benefit.GradeCodes?.Count ?? 0)
                    || savedRules.Any(r => r.CompanyId != company.Id || r.EffectiveFrom != start || r.EffectiveTo != end || !r.GradeId.HasValue))
                    errors.Add($"Benefit plan '{benefit.Code}' already exists with different settings. Review and change it in Benefits Administration; setup will not overwrite an existing plan.");
            }
        }
        return errors;
    }

    /// <summary>
    /// THE SETUP FORMS' GATES for the assistant's Apply (P1, the class #199 closed for the org-structure import).
    /// Apply used to write its legal entity and branches straight to the database: a single-company account could add
    /// a second legal entity, a platform-controlled tenant create one itself, the plan's company limit was never
    /// counted, a draft-approval tenant got an ACTIVE company, "Saudi" was stored as a country code, a branch could be
    /// moved to another company, and a company-scoped HR Manager could create a legal entity or write into a company
    /// outside their scope. The rules are the forms' own: <see cref="CompanyCreationGate"/>,
    /// <see cref="OrganizationSetupService.CountryCodeProblem"/>, the Branches importer's no-move rule, the entity scope
    /// the org-structure import applies, and the grade form's band order. (The assistant never sets a company
    /// registration number, so the form's registration-number uniqueness check has nothing to compare.)
    /// </summary>
    private async Task<OrgGateResult> EvaluateOrgGatesAsync(Guid tenantId, ApplySetupRequest req, CancellationToken ct)
    {
        var problems = new List<string>();
        var scope = this.GetEntityScope();
        var d = req.Draft;
        if (OrganizationSetupService.CountryCodeProblem(req.CountryCode) is { } countryProblem) problems.Add(countryProblem);
        if (!scope.IsGroupLevel)
        {
            var sharedAreas = new List<string>();
            if (d.Departments.Count > 0 || d.Designations.Count > 0) sharedAreas.Add("organization catalogs");
            if (d.Grades.Count > 0 || d.GradePayComponents.Count > 0) sharedAreas.Add("grades and salary bands");
            if (d.LeaveTypes.Count > 0) sharedAreas.Add("leave types");
            if (d.Shifts.Count > 0 || d.WorkingWeek is not null) sharedAreas.Add("shifts and working week");
            if (d.AttendancePolicy is not null || d.OvertimePolicy is not null) sharedAreas.Add("attendance and overtime policies");
            if (d.PayComponents.Count > 0 || d.StatutoryRules.Count > 0) sharedAreas.Add("payroll and statutory rules");
            if (d.HrConfig is not null) sharedAreas.Add("workspace governance");
            if (d.Localization is not null) sharedAreas.Add("language, time zone and currency defaults");
            if (sharedAreas.Count > 0)
                problems.Add("A company administrator cannot change group-shared " + string.Join(", ", sharedAreas) + ". Ask a group administrator to publish the shared baseline, then apply only company-owned policies.");
        }

        Company? company = null;
        var createAsDraft = false;
        var legalName = (req.LegalEntityName ?? string.Empty).Trim();
        if (req.CompanyId.HasValue)
        {
            if (!scope.CanAccessCompany(req.CompanyId.Value))
            {
                problems.Add("The selected company is outside your company scope.");
            }
            else
            {
                company = await ScopedBypass.TenantWide(_db.Companies, tenantId,
                        "Resolve the authorised Setup Studio apply target across the tenant company catalog.")
                    .FirstOrDefaultAsync(x => x.Id == req.CompanyId.Value && !x.IsDeleted, ct);
                if (company is null)
                    problems.Add("The selected company no longer exists in this workspace.");
                else
                {
                    if (!company.IsActive || company.ApprovalStatus != CompanyApprovalStatuses.Active)
                        problems.Add($"Company '{company.LegalNameEn}' is not active. Finish its approval before opening Setup Studio.");
                    if (legalName.Length > 0 && !string.Equals(company.LegalNameEn.Trim(), legalName, StringComparison.OrdinalIgnoreCase))
                        problems.Add("The reviewed company name does not match the selected company. Refresh Setup Studio and review the draft again.");
                    if (CountryCodeStandard.NormalizeToIso2(company.CountryCode) != CountryCodeStandard.NormalizeToIso2(req.CountryCode)
                        || !string.Equals(company.DefaultCurrency, req.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                        problems.Add("The reviewed country and currency must match the selected company.");
                }
            }
        }
        else if (legalName.Length > 0)
        {
            var upper = legalName.ToUpperInvariant();
            var matches = await _db.Companies
                .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.LegalNameEn.ToUpper() == upper)
                .ToListAsync(ct);
            if (matches.Count > 1)
                problems.Add($"'{legalName}' matches more than one company (their names differ only in letter case). Rename one in Setup first.");
            company = matches.Count == 1 ? matches[0] : null;
            if (company is not null && !scope.CanAccessCompany(company.Id))
                problems.Add($"Company '{company.LegalNameEn}' is outside your company scope.");
            if (company is null && matches.Count == 0)
            {
                if (!scope.IsGroupLevel)
                    problems.Add("Only a group-scope administrator can create a legal entity.");
                else
                {
                    var creation = await CompanyCreationGate.EvaluateAsync(_db, tenantId, ct);
                    if (!creation.Allowed) problems.Add(creation.Message);
                    createAsDraft = creation.AsDraft;
                }
            }
        }
        else
        {
            company = await _db.Companies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted, ct);
            if (company is not null && !scope.CanAccessCompany(company.Id))
                problems.Add($"Company '{company.LegalNameEn}' is outside your company scope.");
        }
        if (!scope.IsGroupLevel && (d.AttendancePolicy is not null || d.OvertimePolicy is not null)
            && d.Branches.Count == 0 && (company is null || !await _db.Branches.AnyAsync(b => b.TenantId == tenantId && b.CompanyId == company.Id && !b.IsDeleted, ct)))
            problems.Add("Add a branch for the reviewed company before configuring attendance or overtime; a company-scoped user cannot create a workspace-wide policy.");

        foreach (var g in d.Grades)
        {
            if (g.MaxSalary > 0 && g.MinSalary > g.MaxSalary)
                problems.Add($"Grade '{g.Code}': MinSalary cannot exceed MaxSalary.");
            else if (g.MidSalary > 0 && (g.MidSalary < g.MinSalary || (g.MaxSalary > 0 && g.MidSalary > g.MaxSalary)))
                problems.Add($"Grade '{g.Code}': MidSalary must fall between MinSalary and MaxSalary.");
        }
        var selectedBranch = await ResolveSetupBranchAsync(tenantId, req, company, ct);
        if ((d.AttendancePolicy is not null || d.OvertimePolicy is not null) && selectedBranch.Problem is not null)
            problems.Add(selectedBranch.Problem);
        return new OrgGateResult(problems.Distinct().ToList(), company, createAsDraft, selectedBranch.Code);
    }

    /// <summary>Only the two rules the overtime/attendance engines actually evaluate. Anything
    /// else would be stored, displayed, and silently ignored at run time.</summary>
    private static string RoundingOrDefault(string? value, string fallback)
        => string.Equals(value, "NearestMinute", StringComparison.OrdinalIgnoreCase) ? "NearestMinute"
         : string.Equals(value, "Nearest15", StringComparison.OrdinalIgnoreCase) ? "Nearest15"
         : fallback;

    /// <summary>
    /// The setup-apply leg of the KSA statutory special-leave floor (Arts. 113, 114, 151, 160), run
    /// with the same guard as <c>LeavePoliciesController</c> before anything is written.
    ///
    /// <para>A policy's leave type is resolved the way the apply step below resolves it: the tenant's
    /// EXISTING type of that code first (apply never overwrites one), then the draft's. A statutory leave
    /// type in the draft is also checked on its own — marked unpaid, or with a day cap below the statute
    /// — whether or not the draft carries a policy for it.</para>
    /// </summary>
    private async Task<IActionResult?> RefuseBelowStatutoryLeaveFloorAsync(Guid tenantId, ApplySetupRequest req, CancellationToken ct)
    {
        var d = req.Draft;
        if (d.LeaveTypes.Count == 0 && d.LeavePolicies.Count == 0) return null;
        var violations = new List<string>();

        var existing = await _db.LeaveTypes.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => new { x.Id, x.Code, x.NameEn, x.Category, x.IsPaid, x.MaxConsecutiveDays })
            .ToListAsync(ct);
        var existingByCode = existing
            .GroupBy(x => x.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var draftByCode = d.LeaveTypes
            .GroupBy(t => t.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        if (await KsaStatutoryLeavePolicyGuard.ReachAsync(_db, tenantId, req.CountryCode, null, ct) != KsaPolicyReach.None)
        {
            var rules = new StatutoryRuleReader(_db);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            foreach (var t in d.LeaveTypes)
            {
                if (existingByCode.ContainsKey(t.Code.Trim())) continue;   // apply keeps the existing type
                if (KsaStatutorySpecialLeave.Classify(t.Code, t.NameEn, t.Category) is not { } kind) continue;
                if (!t.IsPaid)
                    violations.Add($"{t.NameEn}: {KsaStatutorySpecialLeave.Describe(kind)} is fully paid by statute ({KsaStatutorySpecialLeave.Citation(kind)}); it cannot be drafted as unpaid.");
                if (t.MaxConsecutiveDays > 0
                    && await KsaStatutorySpecialLeave.ResolveFloorAsync(rules, kind, today, ct) is { } floor
                    && t.MaxConsecutiveDays < floor)
                    violations.Add($"{t.NameEn}: {KsaStatutorySpecialLeave.Describe(kind)} cannot be capped below the statutory {floor:0.##} days ({KsaStatutorySpecialLeave.Citation(kind)}); the draft caps it at {t.MaxConsecutiveDays}.");
            }
        }

        foreach (var lp in d.LeavePolicies)
        {
            var code = (lp.LeaveTypeCode ?? string.Empty).Trim();
            Guid? typeId;
            string typeCode, typeName, typeCategory;
            if (existingByCode.TryGetValue(code, out var e)) (typeId, typeCode, typeName, typeCategory) = (e.Id, e.Code, e.NameEn, e.Category);
            else if (draftByCode.TryGetValue(code, out var t)) (typeId, typeCode, typeName, typeCategory) = (null, t.Code, t.NameEn, t.Category);
            else continue;   // the apply step skips a policy whose type resolves nowhere

            var found = await KsaStatutoryLeavePolicyGuard.CheckAsync(
                _db, tenantId, null, typeId, typeCode, typeName, typeCategory, req.CountryCode, null, "Active",
                lp.AnnualEntitlementDays, lp.MaximumDaysPerRequest, lp.PayrollImpact,
                lp.WeekendsIncluded, lp.PublicHolidaysIncluded, ct);
            violations.AddRange(found.Select(v => $"{typeName}: {v}"));
        }

        return violations.Count == 0
            ? null
            : BadRequest(new
            {
                error = "statutory_leave_floor",
                message = "Nothing was applied. " + string.Join(" ", violations) + " An employer may grant more than the law; it may not grant less.",
                violations,
            });
    }

    private Guid GetTenantId() => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid? GetUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id
        : Guid.TryParse(User.FindFirstValue("sub"), out id) ? id : null;
    /// <summary>The caller's roles as one string, matching how AiAdvisoryService records them.</summary>
    private string CallerRole() =>
        string.Join(",", User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value));
    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
    private RequestContext Context(Guid tenantId) => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(),
        GetUserId(),
        tenantId,
        User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList(),
        User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList());
}

public record ApplySetupRequest(SetupDraft Draft, string CountryCode, string CurrencyCode, string? LegalEntityName = null, Guid? CompanyId = null);
