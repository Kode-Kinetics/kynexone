using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Data;
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
    /// <summary>One audit row per write that changed the pay package of a hire still awaiting approval; its actor can no longer approve.</summary>
    public const string WrittenAction = "employee.compensation_written";
    /// <summary>Marks a resolved gap with the pay fingerprint Finance approved (the rest of the field is the hash).</summary>
    public const string ApprovedMarker = "approved:";

    /// <summary>True when the stored hire carries pay figures or bank details that need a second person.</summary>
    public static bool CarriesCompensation(decimal? salary, string? iban, string? accountNumber) =>
        (salary ?? 0m) > 0m || !string.IsNullOrWhiteSpace(iban) || !string.IsNullOrWhiteSpace(accountNumber);

    /// <summary>The figures Finance approves, in one comparable shape.</summary>
    public sealed record PayPackage(
        decimal? Salary, decimal Basic, decimal Housing, decimal Transport, decimal Food, decimal Mobile, decimal Other,
        decimal FixedDeduction, string? Currency, string? Iban, string? AccountNumber);

    /// <summary>SHA-256 over the whole package (basic, allowances, deductions, currency, bank account), normalised so
    /// spacing and case never matter. Used to bind an approval to the exact figures it approved.</summary>
    public static string PayFingerprint(PayPackage p)
    {
        static string Money(decimal? v) => (v ?? 0m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        static string Compact(string? v) => (v ?? string.Empty).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        return Sha256(string.Join('|', "pay.v1", Money(p.Salary), Money(p.Basic), Money(p.Housing), Money(p.Transport),
            Money(p.Food), Money(p.Mobile), Money(p.Other), Money(p.FixedDeduction), Compact(p.Currency), Compact(p.Iban), Compact(p.AccountNumber)));
    }

    /// <summary>The employee's stored pay package right now: staged rows win over saved ones.</summary>
    public static async Task<PayPackage> LivePackageAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct)
    {
        var salary = db.Employees.Local.FirstOrDefault(e => e.Id == employeeId)?.Salary;
        if (salary is null)
            salary = await ScopedBypass.NullableTenantWide(db.Employees, tenantId, "Pay approval: the stored salary of the employee being approved; tenant pinned.")
                .AsNoTracking().Where(e => e.Id == employeeId).Select(e => e.Salary).FirstOrDefaultAsync(ct);
        var profile = db.EmployeePayrollProfiles.Local
                .FirstOrDefault(p => p.TenantId == tenantId && p.EmployeeId == employeeId && !p.IsDeleted)
            ?? await db.EmployeePayrollProfiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.EmployeeId == employeeId && !p.IsDeleted, ct);
        var staged = db.EmployeeSalaryStructures.Local.Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.IsActive).ToList();
        var saved = await db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.IsActive).ToListAsync(ct);
        var structure = staged.Concat(saved.Where(x => staged.All(t => t.Id != x.Id)))
            .OrderByDescending(x => x.EffectiveDate).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefault();
        return new PayPackage(salary, structure?.BasicSalary ?? 0m, structure?.HousingAllowance ?? 0m, structure?.TransportAllowance ?? 0m,
            structure?.FoodAllowance ?? 0m, structure?.MobileAllowance ?? 0m, structure?.OtherAllowance ?? 0m, structure?.FixedDeduction ?? 0m,
            structure?.Currency ?? profile?.SalaryCurrency, profile?.Iban, profile?.AccountNumber);
    }

    public static async Task<string> LiveFingerprintAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct) =>
        PayFingerprint(await LivePackageAsync(db, tenantId, employeeId, ct));

    /// <summary>
    /// The pay fingerprint Finance last approved: a hash, <see cref="NoApproval"/> when nobody has approved, or
    /// <see cref="LegacyApproval"/> when the approval predates the fingerprint (nothing to compare against).
    /// </summary>
    public const string NoApproval = "none";
    public const string LegacyApproval = "legacy";

    public static async Task<string> ApprovedFingerprintAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct)
    {
        var last = await db.EmployeeImportGaps.AsNoTracking()
            .Where(g => g.TenantId == tenantId && g.EmployeeId == employeeId && g.GapType == GapType && g.ResolvedAtUtc != null)
            .OrderByDescending(g => g.ResolvedAtUtc).Select(g => g.RawValue).FirstOrDefaultAsync(ct);
        // Rows are read newest first; only the newest decides.
        var any = await db.EmployeeImportGaps.AsNoTracking().AnyAsync(g => g.TenantId == tenantId && g.EmployeeId == employeeId
            && g.GapType == GapType && g.ResolvedAtUtc != null, ct);
        if (!any) return NoApproval;
        return last is { } v && v.StartsWith(ApprovedMarker, StringComparison.Ordinal) ? v[ApprovedMarker.Length..] : LegacyApproval;
    }

    /// <summary>
    /// Called after ANY write of an employee's pay (create, update, draft autosave, import, rehire). For an employee who is
    /// not yet in post it keeps the Finance gap honest: opens it when the stored figures are not the approved ones, and
    /// records the actor of a write that changed them so they can never approve their own figures. An employee already in
    /// post is left to the salary-change approval. <paramref name="priorFingerprint"/> is the package before the write
    /// (null = unknown, treated as a write).
    /// </summary>
    public static async Task ReconcileAsync(ZayraDbContext db, Employee employee, string? priorFingerprint, RequestContext context,
        CancellationToken ct, bool forceReopen = false, Guid? workflowApprover = null)
    {
        if (employee.TenantId is not Guid tenantId || employee.Id == 0) return;
        if (Zayra.Api.Application.Organization.EstablishmentOccupancy.IsOccupyingStatus(employee.Status)) return;
        var package = await LivePackageAsync(db, tenantId, employee.Id, ct);
        var live = PayFingerprint(package);
        var written = forceReopen || priorFingerprint is null || !string.Equals(priorFingerprint, live, StringComparison.Ordinal);
        if (!CarriesCompensation(package.Salary, package.Iban, package.AccountNumber)) return;

        var approved = await ApprovedFingerprintAsync(db, tenantId, employee.Id, ct);
        var needsApproval = forceReopen || approved == NoApproval
            || (approved == LegacyApproval ? written : !string.Equals(approved, live, StringComparison.Ordinal));
        if (!needsApproval) return;

        if (!await db.EmployeeImportGaps.AnyAsync(g => g.TenantId == tenantId && g.EmployeeId == employee.Id
                && g.GapType == GapType && g.ResolvedAtUtc == null, ct))
        {
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
        }
        if (written)
            db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), DateTime.UtcNow, priorFingerprint is null && approved == NoApproval ? CreatedAction : WrittenAction,
                "Employee", employee.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), context with { TenantId = tenantId },
                JsonSerializer.Serialize(Fingerprint(package.Salary, package.Iban))));
        // The person who approved the change in the workflow has also seen and signed these figures: they are a second
        // pair of eyes already, but never the Finance approver of the same package.
        if (written && workflowApprover is Guid wf && wf != context.UserId)
            db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), DateTime.UtcNow, WrittenAction,
                "Employee", employee.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), context with { TenantId = tenantId, UserId = wf },
                JsonSerializer.Serialize(Fingerprint(package.Salary, package.Iban))));
    }

    /// <summary>
    /// Called inside the create transaction after the salary and payroll profile are written. Stages the gap and the
    /// audit row on the caller's context; the caller's next SaveChanges commits them with the employee.
    /// </summary>
    public static Task OpenAsync(ZayraDbContext db, Employee employee, RequestContext context, CancellationToken ct) =>
        ReconcileAsync(db, employee, null, context, ct);

    /// <summary>Everyone who wrote this employee's pay figures since the last approval (the create counts).</summary>
    public static async Task<IReadOnlyCollection<Guid>> PayWritersSinceApprovalAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct)
    {
        var since = await db.EmployeeImportGaps.AsNoTracking()
            .Where(g => g.TenantId == tenantId && g.EmployeeId == employeeId && g.GapType == GapType && g.ResolvedAtUtc != null)
            .MaxAsync(g => g.ResolvedAtUtc, ct) ?? DateTime.MinValue;
        var id = employeeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var writers = await ScopedBypass.NullableTenantWide(db.AuditLogs, tenantId, "Pay approval: who wrote this employee's figures since the last approval; tenant pinned, entity pinned.")
            .AsNoTracking()
            .Where(a => a.EntityName == "Employee" && a.EntityId == id && a.UserId != null && a.CreatedAtUtc > since
                && (a.Action == CreatedAction || a.Action == WrittenAction))
            .Select(a => a.UserId!.Value).Distinct().ToListAsync(ct);
        return writers;
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
        if (await IsOpenAsync(db, tenantId, snapshot.EmployeeId, ct) || await HasDriftedFromApprovalAsync(db, tenantId, snapshot.EmployeeId, ct)
            || await CarriesUnapprovedPayAsync(db, tenantId, snapshot.EmployeeId, ct))
        {
            var blocker = new ReadinessItem(
                Key: GapType, Label: "Pay package awaiting Finance approval", Category: "pay", Reason: "statutory",
                Jurisdiction: null, Gate: "activate", FixKind: "pay", FixTarget: null, DocumentType: null);
            readiness = readiness with { State = "Blocked", Blocking = readiness.Blocking.Concat([blocker]).ToList() };
        }
        if (readiness.IsBlocked) throw new EmployeeActivationBlockedException(snapshot.EmployeeId, readiness, policy);
        return readiness;
    }

    /// <summary>
    /// True when the live package carries pay (basic salary, an allowance, a salary or a bank account) and nobody has ever approved
    /// one. Whatever wrote the figures (a payroll salary-structure assignment, a bank-profile sync, a path with no hook), the second
    /// person is still required before the hire can be paid. A legacy approval has nothing to compare against and is not re-asked.
    /// </summary>
    public static async Task<bool> CarriesUnapprovedPayAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct)
    {
        if (await ApprovedFingerprintAsync(db, tenantId, employeeId, ct) != NoApproval) return false;
        var p = await LivePackageAsync(db, tenantId, employeeId, ct);
        return CarriesCompensation(p.Salary, p.Iban, p.AccountNumber)
            || p.Basic > 0m || p.Housing > 0m || p.Transport > 0m || p.Food > 0m || p.Mobile > 0m || p.Other > 0m;
    }

    /// <summary>True when Finance approved a specific package and the stored one is no longer it (a write that bypassed every hook).</summary>
    public static async Task<bool> HasDriftedFromApprovalAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct)
    {
        var approved = await ApprovedFingerprintAsync(db, tenantId, employeeId, ct);
        if (approved is NoApproval or LegacyApproval) return false;
        return !string.Equals(approved, await LiveFingerprintAsync(db, tenantId, employeeId, ct), StringComparison.Ordinal);
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

    /// <summary>
    /// True when <paramref name="caller"/> may not approve this hire's pay (CD39 / CD46): the creator, anyone who wrote the pay since
    /// the last approval (including the workflow approver of a change), the last editor of the employee or profile, the subject, or
    /// the person who handed the subject their sign-in code. Shared by the full approve route and the scoped approval view.
    /// </summary>
    public static async Task<bool> IsBarredAsync(ZayraDbContext db, Guid tenantId, Employee employee, Guid caller, CancellationToken ct)
    {
        var profile = await db.EmployeePayrollProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.EmployeeId == employee.Id && !p.IsDeleted, ct);
        var makers = new HashSet<Guid>(await PayWritersSinceApprovalAsync(db, tenantId, employee.Id, ct));
        if (profile?.CreatedBy is Guid pc) makers.Add(pc);
        if (profile?.UpdatedBy is Guid pu) makers.Add(pu);
        if (employee.UpdatedBy is Guid eu) makers.Add(eu);
        var subjects = new HashSet<Guid>(await Zayra.Api.Infrastructure.Approvals.ApprovalUnblock.SubjectUserIdsAsync(db, tenantId, employee.Id, ct));
        if (employee.UserAccountId is Guid u) subjects.Add(u);
        var refusal = RefusalFor(caller, employee.CreatedBy, subjects.Contains(caller) ? caller : employee.UserAccountId, makers);
        return refusal is not null || await CredentialHandlerBar.IsBarredAsync(db, tenantId, employee.Id, caller, DateTime.UtcNow, ct);
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
