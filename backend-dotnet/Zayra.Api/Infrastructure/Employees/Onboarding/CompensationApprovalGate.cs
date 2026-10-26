using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// HR makes the pay package of a new hire, Finance approves it. A create may still store the salary and the bank
/// details (nothing regresses for the form), but it also opens one durable gap, <see cref="GapType"/>, on the
/// existing import-gap mechanism (no new table). While that gap is open:
/// <list type="bullet">
///   <item>the activation gate refuses (<see cref="EnsureApprovedAsync"/>), so a hire cannot become Active, and
///     therefore cannot be paid, on a figure no second person has seen. Payroll selects Active employees only,
///     so a Draft is excluded by construction;</item>
///   <item>the gap never self-heals: it is cleared only by <c>POST /api/employees/{id}/compensation/approve</c>.</item>
/// </list>
/// A later change to an Active employee's pay goes through the salary-change approval, not this gate.
/// Audit rows carry hashes and the last four characters only, never a figure or a full account number.
/// </summary>
public static class CompensationApprovalGate
{
    public const string GapType = "pay:compensationUnapproved";
    public const string CreatedAction = "employee.created_compensation_set";
    public const string ApprovedAction = "employee.created_compensation_approved";
    public const string ApprovePermission = "payroll.approve";

    /// <summary>True when the stored hire carries pay figures or bank details that need a second person.</summary>
    public static bool CarriesCompensation(decimal? salary, string? iban, string? accountNumber) =>
        (salary ?? 0m) > 0m || !string.IsNullOrWhiteSpace(iban) || !string.IsNullOrWhiteSpace(accountNumber);

    /// <summary>
    /// Called inside the create transaction after the salary and payroll profile are written. Stages the gap and the
    /// audit row on the caller's context; the caller's next SaveChanges commits them with the employee.
    /// </summary>
    public static async Task OpenAsync(ZayraDbContext db, Employee employee, RequestContext context, CancellationToken ct)
    {
        if (employee.TenantId is not Guid tenantId || employee.Id == 0) return;
        // The profile is staged by the caller in this same transaction and may not be saved yet, so the tracked copy
        // wins over a database read (which would miss it and leave a hire with bank details and no approval gap).
        var profile = db.EmployeePayrollProfiles.Local
                .FirstOrDefault(p => p.TenantId == tenantId && p.EmployeeId == employee.Id && !p.IsDeleted)
            ?? await db.EmployeePayrollProfiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.EmployeeId == employee.Id && !p.IsDeleted, ct);
        if (!CarriesCompensation(employee.Salary, profile?.Iban, profile?.AccountNumber)) return;
        if (await db.EmployeeImportGaps.AnyAsync(g => g.TenantId == tenantId && g.EmployeeId == employee.Id
                && g.GapType == GapType && g.ResolvedAtUtc == null, ct))
            return;

        db.EmployeeImportGaps.Add(new EmployeeImportGap
        {
            TenantId = tenantId,
            CompanyId = employee.CompanyId,
            ImportBatchId = Guid.Empty,
            EmployeeId = employee.Id,
            RowNumber = 0,
            GapType = GapType,
            GapCategory = "pay",
            Detail = "Pay package and bank details are awaiting approval by Finance.",
        });
        db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), DateTime.UtcNow, CreatedAction, "Employee",
            employee.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), context with { TenantId = tenantId },
            JsonSerializer.Serialize(Fingerprint(employee.Salary, profile?.Iban))));
    }

    /// <summary>
    /// The activation gate with the pay-approval blocker folded in: evaluates the policy, adds
    /// <see cref="GapType"/> to the blockers while the gap is open, and throws the structured 422 the rest of the
    /// gate already raises. Replaces the evaluator call in <c>EmployeeActivationGuard.EnsureActivatableAsync</c>, so
    /// the checklist always lists the pay blocker next to any other missing detail.
    /// </summary>
    public static async Task<EmployeeReadiness> EnsureActivatableAsync(
        ZayraDbContext db, Guid tenantId, IEmployeeReadinessEvaluator evaluator, EmployeeReadinessSnapshot snapshot,
        ResolvedReadinessPolicy policy, CancellationToken ct)
    {
        var readiness = evaluator.Evaluate(snapshot, policy);
        if (await IsOpenAsync(db, tenantId, snapshot.EmployeeId, ct))
        {
            var blocker = new ReadinessItem(
                Key: GapType, Label: "Pay package awaiting Finance approval", Category: "pay", Reason: "statutory",
                Jurisdiction: null, Gate: "activate", FixKind: "pay", FixTarget: null, DocumentType: null);
            readiness = readiness with { State = "Blocked", Blocking = readiness.Blocking.Concat([blocker]).ToList() };
        }
        if (readiness.IsBlocked) throw new EmployeeActivationBlockedException(snapshot.EmployeeId, readiness, policy);
        return readiness;
    }

    public static Task<bool> IsOpenAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct) =>
        db.EmployeeImportGaps.AsNoTracking().AnyAsync(g => g.TenantId == tenantId && g.EmployeeId == employeeId
            && g.GapType == GapType && g.ResolvedAtUtc == null, ct);

    /// <summary>Why the caller may not approve, or null when they may. Pure; the rules are the second-person set.</summary>
    public static string? RefusalFor(Guid approver, Guid? creator, Guid? employeeUserAccount, IReadOnlyCollection<Guid> makers)
    {
        if (creator == approver || makers.Contains(approver)) return "creator";
        if (employeeUserAccount == approver) return "subject";
        return null;
    }

    private static object Fingerprint(decimal? salary, string? iban)
    {
        var normalisedIban = (iban ?? string.Empty).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        return new
        {
            salaryHash = salary is > 0 ? Sha256(salary.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)) : null,
            ibanLast4 = normalisedIban.Length >= 4 ? normalisedIban[^4..] : null,
            ibanHash = normalisedIban.Length > 0 ? Sha256(normalisedIban) : null,
        };
    }

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    internal static object FingerprintForApproval(decimal? salary, string? iban) => Fingerprint(salary, iban);
}
