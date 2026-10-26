using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// CD28: validates what is STORED for an employee against the same hard rules the create guard applies. A draft may
/// hold an invalid IBAN, phone, identity number or contract date (autosave never fights typing); each one is reported
/// here so it can be shown as a readiness blocker and so activation refuses it with the field code.
/// A checksum failure that an authorised user already waived at write time is not re-asked (the waiver is audited).
/// </summary>
public static class EmployeeStoredDataCheck
{
    public static async Task<Dictionary<string, List<string>>> ErrorsAsync(
        ZayraDbContext db, Guid tenantId, int employeeId, ITenantClock? clock, CancellationToken ct)
    {
        var e = await ScopedBypass.NullableTenantWide(db.Employees, tenantId, "Stored-data re-validation for activation/readiness; tenant re-applied, nothing written.")
            .AsNoTracking().FirstOrDefaultAsync(x => x.Id == employeeId && !x.IsDeleted, ct);
        if (e is null) return new();
        var profile = await db.EmployeePayrollProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employeeId && !x.IsDeleted, ct);
        var records = await db.EmployeeComplianceRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && !x.IsDeleted).ToListAsync(ct);
        var country = e.CompanyId is Guid cid
            ? await ScopedBypass.TenantWide(db.Companies, tenantId, "Stored-data re-validation: the employing company's country; tenant re-applied.")
                .AsNoTracking().Where(c => c.Id == cid).Select(c => c.CountryCode).FirstOrDefaultAsync(ct)
            : null;
        country = string.IsNullOrWhiteSpace(country) ? e.CountryCode : country;

        var tz = OnboardingClock.ZoneFor(country);
        var today = clock is not null ? await clock.TodayAsync(tenantId, ct) : DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
        var request = new EmployeeCreateRequest(
            e.EmployeeCode, false, string.IsNullOrWhiteSpace(e.EnglishName) ? e.FullName : e.EnglishName, e.ArabicName.NullIfEmpty(), null,
            e.Gender.NullIfEmpty(), e.DateOfBirth, e.Nationality.NullIfEmpty(), null, e.PersonalEmail.NullIfEmpty(), e.WorkEmail.NullIfEmpty(),
            e.Phone.NullIfEmpty(), null, e.CompanyId, null, null, null, null, null, null, null, null, null,
            e.ContractType.NullIfEmpty(), e.JoiningDate, e.ConfirmationDate, e.ProbationStartDate, e.ProbationEndDate, e.NoticePeriodDays,
            null, null, null, null, null,
            profile is null ? null : new EmployeePayrollProfileRequest(profile.BankName.NullIfEmpty(), profile.Iban.NullIfEmpty(), null, null, null, null, null,
                profile.WpsEligible, profile.EosbEligible, null, null, null),
            null,
            records.Select(r => new EmployeeComplianceRecordRequest(r.CountryCode, r.FieldKey, r.FieldLabel, r.FieldValue, r.IssueDate, r.ExpiryDate, r.IsSensitive, r.IsRequired)).ToList(),
            ContractEndDate: e.ContractEndDate);

        var result = EmployeeInputValidator.Validate(request, new EmployeeInputValidationContext(country, today, EmployeeGuardMode.Create, tz));
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (field, codes) in result.Errors)
        {
            var kept = codes.Where(c => c != EmployeeWarningCodes.IdChecksumInvalid).ToList();
            if (kept.Count > 0) errors[field] = kept;
        }
        return errors;
    }

    /// <summary>
    /// Adds one blocking item per invalid stored value to a DRAFT's readiness, so the checklist and the blocker count
    /// show what activation will refuse. Only drafts are affected; a live employee's readiness is untouched.
    /// </summary>
    public static async Task<EmployeeReadiness> MergeBlockersAsync(
        ZayraDbContext db, Guid tenantId, int employeeId, string? status, EmployeeReadiness readiness, CancellationToken ct)
    {
        if (!string.Equals(status, EmployeeStatuses.Draft, StringComparison.OrdinalIgnoreCase)) return readiness;
        var errors = await ErrorsAsync(db, tenantId, employeeId, null, ct);
        if (errors.Count == 0) return readiness;
        var items = errors.Select(kv => new ReadinessItem(
            Key: "invalid:" + kv.Key, Label: "Invalid value: " + kv.Key, Category: "data", Reason: string.Join(",", kv.Value),
            Jurisdiction: null, Gate: "activate", FixKind: "field", FixTarget: kv.Key, DocumentType: null)).ToList();
        return readiness with
        {
            State = "Blocked",
            Score = Math.Min(readiness.Score, 55m),
            Blocking = readiness.Blocking.Concat(items).ToList(),
        };
    }

    private static string? NullIfEmpty(this string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
