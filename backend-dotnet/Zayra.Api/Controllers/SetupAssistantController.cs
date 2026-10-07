using System.Security.Claims;
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
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Payroll;
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
        var result = await _assistant.GenerateAsync(new SetupRequester(tenantId, GetUserId(), CallerRole()), profile, ct);
        return Ok(result);
    }

    /// <summary>Persist an approved draft. Idempotent — existing codes are skipped, not duplicated.</summary>
    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] ApplySetupRequest req, CancellationToken ct)
    {
        if (!HasPermission("organization.setup.apply")) return Forbid();
        // One spelling of the country everywhere it is written (company, branches, leave policies, calendars, rules):
        // the forms store ISO codes upper-case, and "sa" would otherwise sit beside "SA".
        req = req with { CountryCode = (req.CountryCode ?? string.Empty).Trim().ToUpperInvariant() };
        var tenantId = GetTenantId();
        var d = req.Draft;
        // Before anything is written: a reviewed draft can still have been edited below the Saudi
        // statutory floor, and applying half of it first would leave the tenant half-configured.
        if (await RefuseBelowStatutoryLeaveFloorAsync(tenantId, req, ct) is { } floorRefusal)
            return floorRefusal;
        // The Setup forms' gates, before anything is written (P1, the same class as the org-structure import).
        var gate = await EvaluateOrgGatesAsync(tenantId, req, ct);
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
        var defaultBranch = branchByCode.Values.FirstOrDefault(x => company is null || x.CompanyId == company.Id);

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
                x.TenantId == tenantId && x.GradeId == grade.Id && x.ComponentCode == component.ComponentCode, ct);
            if (exists) continue;
            var payComponent = new GradePayScaleComponent
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
        var existingPolicyKeys = (await _db.LeavePolicies.Where(x => x.TenantId == tenantId)
            .Select(x => new { x.LeaveTypeId, x.Name }).ToListAsync(ct))
            .Select(x => $"{x.LeaveTypeId}|{x.Name.ToUpperInvariant()}").ToHashSet();
        foreach (var lp in d.LeavePolicies)
        {
            if (!leaveByCode.TryGetValue((lp.LeaveTypeCode ?? "").ToUpperInvariant(), out var leaveType)) continue;
            var policyName = string.IsNullOrWhiteSpace(lp.Name) ? $"{leaveType.NameEn} Policy" : lp.Name.Trim();
            if (!existingPolicyKeys.Add($"{leaveType.Id}|{policyName.ToUpperInvariant()}")) continue;
            _db.LeavePolicies.Add(new LeavePolicy
            {
                TenantId = tenantId,
                Name = policyName,
                LeaveTypeId = leaveType.Id,
                CountryCode = req.CountryCode ?? string.Empty,
                CompanyId = company?.Id,
                AppliesOnProbation = lp.AppliesOnProbation,
                AnnualEntitlementDays = Math.Clamp(lp.AnnualEntitlementDays, 0m, 365m),
                AccrualMethod = string.Equals(lp.AccrualMethod, "Monthly", StringComparison.OrdinalIgnoreCase) ? "Monthly" : "Yearly",
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
            });
            Bump("leavePolicies", 1);
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
        // One calendar per (country, year); its holidays are keyed by date, so re-applying a draft
        // tops up a partial calendar instead of duplicating the days already in it.
        if (d.HolidayCalendar is not null && d.HolidayCalendar.Holidays.Count > 0)
        {
            var year = d.HolidayCalendar.CalendarYear;
            var countryCode = (req.CountryCode ?? string.Empty).Trim().ToUpperInvariant();
            var calendar = await _db.PublicHolidayCalendars.FirstOrDefaultAsync(
                x => x.TenantId == tenantId && x.CountryCode == countryCode && x.CalendarYear == year, ct);
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
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Code == ap.Code, ct);
            if (existingAttendance is null)
            {
                _db.AttendancePolicies.Add(new AttendancePolicy
                {
                    TenantId = tenantId,
                    Code = ap.Code,
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
                });
                Bump("attendancePolicies", 1);
            }
        }

        // ── Overtime policy + its multipliers ────────────────────────────────
        if (d.OvertimePolicy is not null && !string.IsNullOrWhiteSpace(d.OvertimePolicy.Code))
        {
            var op = d.OvertimePolicy;
            var overtimePolicy = await _db.OvertimePolicies
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.Code == op.Code, ct);
            if (overtimePolicy is null)
            {
                overtimePolicy = new OvertimePolicy
                {
                    TenantId = tenantId,
                    Code = op.Code,
                    Name = op.Name,
                    BranchId = defaultBranch?.Id,
                    HourlyRateBasis = string.IsNullOrWhiteSpace(op.HourlyRateBasis) ? "BasicSalary" : op.HourlyRateBasis,
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
                    DayCategory = m.DayCategory,
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
            _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), at, action, entity, id.ToString(), context,
                JsonSerializer.Serialize(new { source = "setup_assistant", key })));
        _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), at, "setup.assistant_applied", "SetupDraft", "bulk", context,
            JsonSerializer.Serialize(new
            {
                countryCode = req.CountryCode,
                currencyCode = req.CurrencyCode,
                legalEntityName = req.LegalEntityName,
                applied = counts,
                total = counts.Values.Sum(),
                entities = audited.Count,
                skipped,
            })));
        // The company gate is asked again INSIDE the save's transaction, as the org-structure import does: another
        // legal entity created between the gate above and this save (a second apply, the form) could otherwise take
        // the plan's last company or turn a single-company account into two.
        var creatingCompany = company is not null && _db.Entry(company).State == EntityState.Added;
        if (creatingCompany && _db.Database.IsRelational())
        {
            IActionResult? refusal = null;
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                refusal = null;
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var creation = await CompanyCreationGate.EvaluateAsync(_db, tenantId, ct);
                if (!creation.Allowed)
                {
                    await tx.RollbackAsync(ct);
                    refusal = UnprocessableEntity(new
                    {
                        error = "setup_apply_refused",
                        message = "Nothing was applied. " + creation.Message,
                        problems = new[] { creation.Message },
                    });
                    return;
                }
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            });
            if (refusal is not null)
            {
                _db.ChangeTracker.Clear();
                return refusal;
            }
        }
        else
        {
            await _db.SaveChangesAsync(ct);
        }
        return Ok(new { applied = counts, total = counts.Values.Sum(), skipped });
    }

    private sealed record OrgGateResult(List<string> Problems, Company? Company, bool CreateAsDraft);

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

        Company? company = null;
        var createAsDraft = false;
        var legalName = (req.LegalEntityName ?? string.Empty).Trim();
        if (legalName.Length > 0)
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

        if (d.Branches.Count > 0)
        {
            var codes = d.Branches.Where(b => !string.IsNullOrWhiteSpace(b.Code)).Select(b => b.Code.Trim().ToUpperInvariant()).ToList();
            var saved = await _db.Branches.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted && codes.Contains(x.Code.ToUpper()))
                .Select(x => new { x.Code, x.CompanyId })
                .ToListAsync(ct);
            foreach (var branch in saved)
                if (company is null || branch.CompanyId != company.Id)
                    problems.Add($"Branch '{branch.Code}' belongs to another company and cannot be moved to another company by the setup assistant.");
        }

        foreach (var g in d.Grades)
        {
            if (g.MaxSalary > 0 && g.MinSalary > g.MaxSalary)
                problems.Add($"Grade '{g.Code}': MinSalary cannot exceed MaxSalary.");
            else if (g.MidSalary > 0 && (g.MidSalary < g.MinSalary || (g.MaxSalary > 0 && g.MidSalary > g.MaxSalary)))
                problems.Add($"Grade '{g.Code}': MidSalary must fall between MinSalary and MaxSalary.");
        }
        return new OrgGateResult(problems.Distinct().ToList(), company, createAsDraft);
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

public record ApplySetupRequest(SetupDraft Draft, string CountryCode, string CurrencyCode, string? LegalEntityName = null);
