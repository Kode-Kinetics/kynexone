using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees;

/// <summary>
/// THE ONE place an employee edit-modal patch is applied. Every path that turns a
/// <c>{ field: value }</c> patch into column writes calls this — the direct PUT
/// (<c>EmployeesController.UpdateEmployee</c>), the sensitive-change approval
/// (<c>EmployeesController.ApproveChange</c>) and the generic approvals decide
/// (<c>ApprovalWorkflowService</c>).
///
/// WHY IT IS SHARED. The approval path used to carry a HAND-COPIED duplicate of the controller's
/// switch. It had drifted: six keys were missing (<c>iqamaExpiryDate</c>, <c>emiratesIdExpiryDate</c>,
/// <c>qidExpiryDate</c>, <c>civilIdExpiryDate</c>, <c>idNumber</c>, <c>sponsorName</c>) and it had NO
/// <c>default</c> arm, so an approver could approve a change whose values were then discarded in
/// silence — four of those six are fail-closed PAY gates, so the employee stayed payroll-blocked on
/// the very value the approval had just accepted. The duplicate-applier pattern has produced four
/// separate defects in this module; it is not patched here, it is deleted. Add a field ONCE, in
/// <see cref="Apply"/>, and every path gets it.
///
/// NEVER add a silent fall-through to the switch. An unrecognised key is RETURNED to the caller,
/// which must act on it: the PUT rejects up front (400 naming the key), the approve paths log loudly
/// (their payload was validated when it was requested, so refusing there would strand an in-flight
/// approval with no operator remedy).
/// </summary>
public static class EmployeeChangeApplier
{
    /// <summary>
    /// Patch keys whose STORAGE TARGET is <see cref="EmployeePayrollProfile"/>, not <see cref="Employee"/>
    /// (registry binding <c>payrollProfile.*</c>). <see cref="Apply"/> recognises them — they are not
    /// unknown keys — but cannot write them, because the profile is a different row that has to be loaded.
    /// Callers apply them with <see cref="ApplyPayrollProfileAsync"/> in the same unit of work.
    /// </summary>
    public static readonly IReadOnlySet<string> PayrollProfileKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        // SocialInsuranceReference is a fail-closed PAY gate in five GCC branches (GPSSA/GRSIA/PIFSS/
        // SPF/SIO — see GccReadinessFloor). It had no write path anywhere outside CSV import, so every
        // Bahraini-company employee and every AE/QA/KW/OM national was permanently payroll-blocked on a
        // field the readiness checklist told the user to fix "in profile".
        "socialInsuranceReference",
        // The bank's routing code and the account number are read LIVE by the WPS/SIF export. They had no edit
        // key at all, so an approved move to another bank left the OLD bank's routing code on every wage line.
        // Approval-gated with the IBAN (EmployeesController.SensitiveFields); see EmployeeBankProfileSync.
        "bankRoutingCode",
        "accountNumber",
        "molId", "salaryCurrency", "payrollGroup", "salaryStructureReference", "paymentMethod",
    };

    /// <summary>True when the change set touches a key stored on the payroll profile.</summary>
    public static bool TouchesPayrollProfile(IEnumerable<string> changedKeys)
        => changedKeys.Any(PayrollProfileKeys.Contains);

    /// <summary>
    /// Applies the patch to the employee's own columns and RETURNS the keys it did not recognise.
    /// Ordinal, because a C# string switch is ordinal: "BankIban" is NOT "bankIban".
    /// </summary>
    public static IReadOnlyList<string> Apply(Employee employee, IReadOnlyDictionary<string, JsonElement> changes)
    {
        var unknown = new List<string>();
        foreach (var (field, value) in changes)
        {
            switch (field)
            {
                case "englishName":
                    employee.EnglishName = value.GetString() ?? employee.EnglishName;
                    employee.FullName = employee.EnglishName;
                    break;
                case "arabicName": employee.ArabicName = value.GetString() ?? employee.ArabicName; break;
                case "preferredName": employee.PreferredName = value.GetString() ?? employee.PreferredName; break;
                case "gender": employee.Gender = value.GetString() ?? employee.Gender; break;
                case "nationality": employee.Nationality = value.GetString() ?? employee.Nationality; break;
                case "personalEmail": employee.PersonalEmail = value.GetString() ?? employee.PersonalEmail; break;
                case "workEmail": employee.WorkEmail = value.GetString() ?? employee.WorkEmail; break;
                case "phone": employee.Phone = value.GetString() ?? employee.Phone; break;
                case "jobTitle": employee.JobTitle = value.GetString() ?? employee.JobTitle; break;
                case "employmentType": employee.EmploymentType = value.GetString() ?? employee.EmploymentType; break;
                case "joiningDate":
                    if (value.ValueKind == JsonValueKind.String && DateTime.TryParse(value.GetString(), out var joining))
                        employee.JoiningDate = DateTime.SpecifyKind(joining, DateTimeKind.Utc);
                    break;
                case "department": employee.Department = value.GetString() ?? employee.Department; break;
                case "designation": employee.Designation = value.GetString() ?? employee.Designation; break;
                case "branch": employee.Branch = value.GetString() ?? employee.Branch; break;
                case "workLocation": employee.WorkLocation = value.GetString() ?? employee.WorkLocation; break;
                case "managerEmployeeId": employee.ManagerEmployeeId = value.ValueKind == JsonValueKind.Null ? null : value.GetInt32(); break;
                case "dateOfBirth": employee.DateOfBirth = ReadDateOnly(value); break;
                case "maritalStatus": employee.MaritalStatus = value.GetString() ?? employee.MaritalStatus; break;
                case "emergencyContactName": employee.EmergencyContactName = value.GetString() ?? employee.EmergencyContactName; break;
                case "emergencyContactPhone": employee.EmergencyContactPhone = value.GetString() ?? employee.EmergencyContactPhone; break;
                case "contractType": employee.ContractType = value.GetString() ?? employee.ContractType; break;
                case "grade": employee.Grade = value.GetString() ?? employee.Grade; break;
                case "costCenter": employee.CostCenter = value.GetString() ?? employee.CostCenter; break;
                case "salary": employee.Salary = value.GetDecimal(); break;
                case "bankName": employee.BankName = value.GetString() ?? employee.BankName; break;
                case "bankIban": employee.BankIban = value.GetString() ?? employee.BankIban; break;
                case "wpsBankDetails": employee.WpsBankDetails = value.GetString() ?? employee.WpsBankDetails; break;
                case "passportNumber": employee.PassportNumber = value.GetString() ?? employee.PassportNumber; break;
                case "passportIssueDate": employee.PassportIssueDate = ReadDateOnly(value); break;
                case "passportExpiryDate": employee.PassportExpiryDate = ReadDateOnly(value); break;
                case "visaNumber": employee.VisaNumber = value.GetString() ?? employee.VisaNumber; break;
                case "visaIssueDate": employee.VisaIssueDate = ReadDateOnly(value); break;
                case "visaExpiryDate": employee.VisaExpiryDate = ReadDateOnly(value); break;
                case "iqamaNumber": employee.IqamaNumber = value.GetString() ?? employee.IqamaNumber; break;
                // IqamaExpiry was readable and CSV-importable but had no edit path: it is exported at
                // the employee-detail projection and read by both CSV importers, yet the applier had
                // no case for it. GccReadinessFloor treats it as a fail-closed PAY gate for non-GCC
                // expats, so an employee whose iqama expiry was wrong could be blocked from payroll
                // with no supported way to correct it. Mirrors passportExpiryDate directly above.
                case "iqamaExpiryDate": employee.IqamaExpiryDate = ReadDateOnly(value); break;
                case "muqeemNumber": employee.MuqeemNumber = value.GetString() ?? employee.MuqeemNumber; break;
                case "gosiReference": employee.GosiReference = value.GetString() ?? employee.GosiReference; break;
                // F02 (#142): resolves the person's GOSI cohort. Null clears it back to Unknown (never to a cohort).
                case "gosiFirstRegisteredOn": employee.GosiFirstRegisteredOn = ReadDateOnly(value); break;
                case "emiratesId": employee.EmiratesId = value.GetString() ?? employee.EmiratesId; break;
                case "laborCardNumber": employee.LaborCardNumber = value.GetString() ?? employee.LaborCardNumber; break;
                case "visaFileNumber": employee.VisaFileNumber = value.GetString() ?? employee.VisaFileNumber; break;
                case "qid": employee.Qid = value.GetString() ?? employee.Qid; break;
                case "workPermitNumber": employee.WorkPermitNumber = value.GetString() ?? employee.WorkPermitNumber; break;
                case "workPermitIssueDate": employee.WorkPermitIssueDate = ReadDateOnly(value); break;
                case "civilId": employee.CivilId = value.GetString() ?? employee.CivilId; break;
                case "residencyNumber": employee.ResidencyNumber = value.GetString() ?? employee.ResidencyNumber; break;
                case "residencyIssueDate": employee.ResidencyIssueDate = ReadDateOnly(value); break;
                // The five (six with sponsorName) keys the modal emitted into a switch that had no case for
                // them. All mirror `iqamaExpiryDate` above, which already did the right thing for Saudi
                // Arabia only; the identical hole was left open for the other five GCC states.
                case "emiratesIdExpiryDate": employee.EmiratesIdExpiryDate = ReadDateOnly(value); break;   // AE pay gate
                case "qidExpiryDate": employee.QidExpiryDate = ReadDateOnly(value); break;                 // QA pay gate
                case "civilIdExpiryDate": employee.CivilIdExpiryDate = ReadDateOnly(value); break;         // KW/OM/BH pay gate
                case "idNumber": employee.IdNumber = value.GetString() ?? employee.IdNumber; break;        // SA national Hawiyya, activate gate
                case "qiwaContractNumber": employee.QiwaContractNumber = value.GetString() ?? employee.QiwaContractNumber; break;
                // Emitted by the server catalogue for every expat (registry `SponsorName`, compliance key
                // `sponsor`); unreachable while the catalogue was dead, reachable the moment it was fixed.
                case "sponsorName": employee.SponsorName = value.GetString() ?? employee.SponsorName; break;
                case "terminationReason": employee.TerminationReason = value.GetString() ?? employee.TerminationReason; break;
                // Stored on EmployeePayrollProfile, not on Employee — recognised here (so it is never
                // reported as unknown and never rejected by the PUT allow-list check) and written by
                // ApplyPayrollProfileAsync, which the caller runs in the same unit of work.
                case "socialInsuranceReference": break;
                case "bankRoutingCode": break;
                case "accountNumber": break;
                case "molId": break;
                case "salaryCurrency": break;
                case "payrollGroup": break;
                case "salaryStructureReference": break;
                case "paymentMethod": break;
                case "salaryBreakdown": break;
                // NEVER add a silent fall-through here. An unrecognised key is reported, not dropped.
                default: unknown.Add(field); break;
            }
        }
        return unknown;
    }

    /// <summary>
    /// Applies an APPROVED change set — every step, in one order, for every approval path: the sensitive-change
    /// approve endpoint, the Approval Center decide, and the effective-date job that applies a future-dated
    /// approval when its day arrives. Returns the keys <see cref="Apply"/> did not recognise.
    ///
    /// <para>The steps: the employee's own columns (<see cref="Apply"/>), the keys stored on the payroll
    /// profile (<see cref="ApplyPayrollProfileAsync"/>), free-text department/designation/branch resolved to
    /// ids (<see cref="EmployeeOrgFieldResolver"/>, throws <see cref="InvalidOperationException"/> when a name
    /// does not resolve), and the approved bank field(s) mirrored onto the payroll profile WPS pays from
    /// (<see cref="EmployeeBankProfileSync"/>). It used to be written out at each call site; a path that
    /// forgot the last step paid the old IBAN.</para>
    ///
    /// <para>Stages writes only — the caller validates first (manager, establishment) and persists in its
    /// own unit of work.</para>
    /// </summary>
    public static async Task<IReadOnlyList<string>> ApplyApprovedChangeAsync(
        ZayraDbContext db, Guid tenantId, Employee employee, IReadOnlyDictionary<string, JsonElement> changes,
        Guid? actorUserId, CancellationToken ct)
    {
        var unknown = Apply(employee, changes);
        await ApplyPayrollProfileAsync(db, employee, changes, actorUserId, ct);
        await EmployeeSalaryBreakdownChanges.ApplyAsync(db, employee, changes, actorUserId, ct);
        await EmployeeOrgFieldResolver.ResolveAppliedChangesAsync(db, tenantId, employee, changes.Keys, ct);
        await EmployeeBankProfileSync.SyncAsync(db, employee, changes.Keys, ct);
        return unknown;
    }

    /// <summary>
    /// Writes the <c>payrollProfile.*</c> keys of the same patch onto the employee's payroll profile,
    /// creating the row when the employee has none — unlike <see cref="EmployeeBankProfileSync"/>, which
    /// mirrors a value that also lives on the Employee scalar and can therefore skip a missing row. These
    /// keys have NO other home: skipping would be the silent discard this class exists to end.
    /// Does not SaveChanges — the caller persists as part of its own unit of work.
    /// </summary>
    public static async Task ApplyPayrollProfileAsync(
        ZayraDbContext db, Employee employee, IReadOnlyDictionary<string, JsonElement> changes,
        Guid? actorUserId, CancellationToken ct)
    {
        if (employee.TenantId is null || employee.Id == 0) return;
        if (!TouchesPayrollProfile(changes.Keys)) return;

        var tenantId = employee.TenantId.Value;
        var profile = db.EmployeePayrollProfiles.Local
                          .FirstOrDefault(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && !x.IsDeleted)
                      ?? await db.EmployeePayrollProfiles
                          .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && !x.IsDeleted, ct);
        if (profile is null)
        {
            profile = new EmployeePayrollProfile
            {
                TenantId = tenantId,
                EmployeeId = employee.Id,
                // Never manufacture the entity default ("AED") for a tenant that does not trade in it.
                SalaryCurrency = await db.ResolveTenantCurrencyAsync(tenantId, ct),
                CreatedBy = actorUserId,
            };
            db.EmployeePayrollProfiles.Add(profile);
        }

        foreach (var (field, value) in changes)
        {
            switch (field)
            {
                case "socialInsuranceReference":
                    profile.SocialInsuranceReference = value.ValueKind == JsonValueKind.Null
                        ? string.Empty
                        : value.GetString() ?? profile.SocialInsuranceReference;
                    break;
                case "bankRoutingCode":
                    profile.BankRoutingCode = value.ValueKind == JsonValueKind.Null
                        ? string.Empty
                        : (value.GetString() ?? profile.BankRoutingCode).Trim();
                    break;
                case "accountNumber":
                    profile.AccountNumber = value.ValueKind == JsonValueKind.Null
                        ? string.Empty
                        : (value.GetString() ?? profile.AccountNumber).Trim();
                    break;
                case "molId": profile.MolId = value.GetString()?.Trim() ?? string.Empty; break;
                case "salaryCurrency": profile.SalaryCurrency = value.GetString()?.Trim() ?? string.Empty; break;
                case "payrollGroup": profile.PayrollGroup = value.GetString()?.Trim() ?? string.Empty; break;
                case "salaryStructureReference": profile.SalaryStructureReference = value.GetString()?.Trim() ?? string.Empty; break;
                case "paymentMethod":
                    profile.PaymentMethod = value.GetString()?.Trim() ?? string.Empty;
                    employee.WpsBankDetails = profile.PaymentMethod;
                    break;
            }
        }

        profile.UpdatedAtUtc = DateTime.UtcNow;
        profile.UpdatedBy = actorUserId;
    }

    /// <summary>Why a <c>managerEmployeeId</c> patch was refused. <see cref="OutOfScope"/> maps to 403 at
    /// the HTTP boundary (same as <c>PUT {id}/manager</c>); everything else is a 422.</summary>
    public sealed record ManagerChangeRejection(string Message, bool OutOfScope);

    /// <summary>
    /// Validates a <c>managerEmployeeId</c> patch BEFORE <see cref="Apply"/> writes it. Returns null when the
    /// patch has no manager key, clears the manager, or names a valid manager.
    ///
    /// <para>WHY. <see cref="Apply"/> wrote <c>value.GetInt32()</c> straight onto the column: any integer was
    /// accepted — an employee id from ANOTHER TENANT, an employee outside the caller's data scope, the
    /// employee themself, or a subordinate (a reporting cycle that approval routing and the org chart then
    /// walk forever). The dedicated <c>PUT {id}/manager</c> endpoint has always refused all four; the
    /// edit-modal field bypassed every one of them.</para>
    ///
    /// <para>TENANT: the manager must exist in the employee's own tenant, not deleted, and be visible
    /// through the caller's company scope (the DbContext query filter). A foreign id reads "not found" —
    /// the same words as a missing one, so the check does not confirm that an id exists elsewhere.
    /// DATA SCOPE: <paramref name="canAccessEmployee"/> is the caller's <c>DataScope.CanAccessEmployee</c>
    /// (null on the approval paths, where the approver acts on an already-scoped request).
    /// CYCLE: the chain above the proposed manager is walked tenant-wide (company filter off), because a
    /// cycle through a company the caller cannot see is still a cycle.</para>
    /// </summary>
    public static async Task<ManagerChangeRejection?> ValidateManagerChangeAsync(
        ZayraDbContext db, Employee employee, IReadOnlyDictionary<string, JsonElement> changes,
        Func<int, bool>? canAccessEmployee, CancellationToken ct)
    {
        if (!changes.TryGetValue("managerEmployeeId", out var value)) return null;
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null; // clearing is always allowed
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var managerId))
            return new ManagerChangeRejection("managerEmployeeId must be an employee id (a whole number) or null.", false);
        if (employee.TenantId is not Guid tenantId)
            return new ManagerChangeRejection("The employee has no tenant; a manager cannot be assigned.", false);
        if (managerId == employee.Id)
            return new ManagerChangeRejection("An employee cannot be their own manager.", false);

        var managerVisible = await db.Employees.AsNoTracking()
            .AnyAsync(e => e.TenantId == tenantId && e.Id == managerId && !e.IsDeleted, ct);
        if (!managerVisible)
            return new ManagerChangeRejection($"Manager employee {managerId} was not found.", false);
        if (canAccessEmployee is not null && !canAccessEmployee(managerId))
            return new ManagerChangeRejection($"Manager employee {managerId} is outside your data scope.", true);

        var managerOf = await Zayra.Api.Infrastructure.Data.ScopedBypass
            .NullableTenantWide(db.Employees, tenantId,
                "Reporting-cycle walk: the chain above a proposed manager crosses companies; a cycle through a company the caller cannot see is still a cycle. Tenant re-applied.")
            .AsNoTracking()
            .Where(e => !e.IsDeleted)
            .Select(e => new { e.Id, e.ManagerEmployeeId })
            .ToDictionaryAsync(e => e.Id, e => e.ManagerEmployeeId, ct);
        var visited = new HashSet<int>();
        for (int? cursor = managerId; cursor is int current && visited.Add(current);
             cursor = managerOf.GetValueOrDefault(current))
        {
            if (current == employee.Id)
                return new ManagerChangeRejection(
                    $"Employee {managerId} reports (directly or indirectly) to this employee, so making them the manager would create a reporting cycle.", false);
        }
        return null;
    }

    private static DateOnly? ReadDateOnly(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return DateOnly.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }
}
