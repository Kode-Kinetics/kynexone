using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// WORK-EMAIL SETTER RULE. The work email is the address every credential for the employee's login is delivered to
/// (invitations, reset links, welcome codes), so whoever set it steers those credentials. The person who last set an
/// employee's work email therefore never also issues or binds a credential for that employee's login: not an
/// invitation (including a resend), not a login link, not a password-reset link, not an onboarding welcome code.
///
/// <para>"Setter" = the actor of the latest <see cref="AccessManagementService.WorkEmailChangedAction"/> row for the
/// employee (written by every create, draft approval, import and edit — see WorkEmailLoginGuard) AND, when that row
/// came from a draft approval, the drafter who typed the address (its <c>draftedBy</c>). An employee with no such row
/// (created before the rule existed) has no recorded setter.</para>
///
/// <para>Every credential path calls <see cref="ThrowIfCallerIsSetterAsync"/> (or reads <see cref="GetAsync"/>) — one
/// rule, one place. Refusals are 403 with code <see cref="SetByCallerCode"/>.</para>
/// </summary>
public static class WorkEmailSetterRule
{
    public const string SetByCallerCode = "work_email_set_by_caller";

    /// <summary>
    /// THE RULE BLOCKS THE EMAIL CHANNEL, NOT THE CREDENTIAL. A setter can still issue an invitation, a resend, a
    /// credential-rotating link or a reset link (a single-admin tenant, an HR officer inviting the people they added),
    /// but it is NEVER emailed to the address they typed: the link is handed back to them, recorded as a disclosure
    /// (so they become a credential handler of the login), and this is what they are told.
    /// </summary>
    public const string HandOverMessage = "You entered this work email, so hand the link over in person.";
    public const string SetByHandlerCode = "work_email_set_by_handler";

    public const string SetByCallerMessage =
        "You set this employee's work email, so you cannot also issue or link a credential for their login. Another administrator must do it.";
    public const string SetByCallerMessageAr =
        "أنت من عيّن البريد الإلكتروني للعمل لهذا الموظف، لذا لا يمكنك أيضاً إصدار بيانات اعتماد لحساب دخوله أو ربطه. يجب أن يقوم بذلك مسؤول آخر.";

    private const string Why =
        "Work-email setter rule: who last set one employee's work email is read from the tenant's audit trail; the tenant is re-applied and the employee was already scope-checked by the caller.";

    /// <summary>
    /// The last work-email set for an employee: who did it, and — for a draft approval — who drafted the hire and
    /// every editor who set the draft's work email; when; and whether it was a change to an existing employee
    /// (<see cref="ChangedAfterCreation"/>: the row carries an old value) rather than the value it was created with.
    /// </summary>
    public sealed record Setter(Guid? ActorUserId, Guid? DraftedByUserId, IReadOnlyList<Guid> DraftEmailSetterIds, DateTime SetAtUtc,
        bool ChangedAfterCreation)
    {
        /// <summary>Everyone counted as having set the address.</summary>
        public IReadOnlyCollection<Guid> UserIds =>
            new[] { ActorUserId, DraftedByUserId }.Where(x => x.HasValue).Select(x => x!.Value)
                .Concat(DraftEmailSetterIds).Distinct().ToList();

        public bool Includes(Guid? userId) => userId is Guid id && UserIds.Contains(id);
    }

    /// <summary>Written whenever a DRAFT's work email is set (create, edit): the editor is a setter of the hire's address.</summary>
    public const string DraftWorkEmailSetAction = "employee.draft_work_email_set";

    public const string ConfirmWorkEmailCode = "confirm_work_email";
    public const string ConfirmWorkEmailMessage =
        "This employee's work email was changed after the record was created. Confirm the address with the employee, then try again.";
    public const string ConfirmWorkEmailMessageAr =
        "تم تغيير البريد الإلكتروني للعمل لهذا الموظف بعد إنشاء السجل. تأكد من العنوان مع الموظف، ثم حاول مرة أخرى.";

    /// <summary>The latest setter of <paramref name="employeeId"/>'s work email, or null when none is recorded.</summary>
    public static async Task<Setter?> GetAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct)
    {
        var employeeKey = employeeId.ToString(CultureInfo.InvariantCulture);
        var row = await ScopedBypass.NullableTenantWide(db.AuditLogs, tenantId, Why).AsNoTracking()
            .Where(x => x.EntityName == "Employee" && x.EntityId == employeeKey && x.Action == AccessManagementService.WorkEmailChangedAction)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Select(x => new { x.UserId, x.Metadata, x.CreatedAtUtc })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var (draftedBy, draftSetters, changedAfterCreation) = Parse(row.Metadata);
        return new Setter(row.UserId, draftedBy, draftSetters, row.CreatedAtUtc, changedAfterCreation);
    }

    /// <summary>
    /// Every user who set a draft's work email (its <see cref="DraftWorkEmailSetAction"/> rows), for the approval row.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> DraftWorkEmailSettersAsync(ZayraDbContext db, Guid tenantId, Guid draftId, CancellationToken ct)
    {
        var draftKey = draftId.ToString();
        return await ScopedBypass.NullableTenantWide(db.AuditLogs, tenantId, Why).AsNoTracking()
            .Where(x => x.EntityName == "EmployeeDraft" && x.EntityId == draftKey && x.Action == DraftWorkEmailSetAction && x.UserId != null)
            .Select(x => x.UserId!.Value)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <summary>
    /// CONFIRMATION WHERE IT MATTERS. True when the latest work-email set was a CHANGE to an existing employee (not
    /// the value it was created with) and the employee has no activated login yet: a credential for that address
    /// (an invitation, a link) then needs the caller's explicit confirmation that they checked it with the person.
    /// </summary>
    public static async Task<bool> RequiresConfirmationAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct)
    {
        var setter = await GetAsync(db, tenantId, employeeId, ct);
        if (setter?.ChangedAfterCreation != true) return false;
        var linkedUserIds = await ScopedBypass.TenantWide(db.EmployeeUserAccounts, tenantId, Why).AsNoTracking()
            .Where(x => x.EmployeeId == employeeId && !x.IsDeleted && x.UserId != null)
            .Select(x => x.UserId!.Value)
            .ToListAsync(ct);
        var pointer = await ScopedBypass.NullableTenantWide(db.Employees, tenantId, Why).AsNoTracking()
            .Where(x => x.Id == employeeId)
            .Select(x => x.UserAccountId)
            .FirstOrDefaultAsync(ct);
        if (pointer is Guid p) linkedUserIds.Add(p);
        var hasActivatedLogin = linkedUserIds.Count > 0 && await ScopedBypass.TenantWide(db.Users, tenantId, Why).AsNoTracking()
            .AnyAsync(x => linkedUserIds.Contains(x.Id) && !x.IsDeleted && (x.IsActive || x.LastLoginAtUtc != null), ct);
        return !hasActivatedLogin;
    }

    /// <summary>Refuses (400 <see cref="ConfirmWorkEmailCode"/>) a credential for a changed, unconfirmed work email.</summary>
    public static async Task ThrowIfConfirmationMissingAsync(ZayraDbContext db, Guid tenantId, int employeeId, bool confirmed, CancellationToken ct)
    {
        if (!confirmed && await RequiresConfirmationAsync(db, tenantId, employeeId, ct))
            throw new WorkEmailConfirmationRequiredException();
    }

    /// <summary>True when <paramref name="callerUserId"/> is a setter of the employee's current work email.</summary>
    public static async Task<bool> IsCallerSetterAsync(ZayraDbContext db, Guid tenantId, int employeeId, Guid? callerUserId, CancellationToken ct) =>
        callerUserId is not null && (await GetAsync(db, tenantId, employeeId, ct))?.Includes(callerUserId) == true;

    /// <summary>Refuses (<see cref="WorkEmailSetterRefusedException"/>, 403) when the caller set the employee's work email.</summary>
    public static async Task ThrowIfCallerIsSetterAsync(ZayraDbContext db, Guid tenantId, int employeeId, Guid? callerUserId, CancellationToken ct)
    {
        if (await IsCallerSetterAsync(db, tenantId, employeeId, callerUserId, ct))
            throw new WorkEmailSetterRefusedException(SetByCallerCode, SetByCallerMessage, SetByCallerMessageAr);
    }

    /// <summary>
    /// Every living employee of the tenant this login belongs to: its live EmployeeUserAccounts mappings UNION the
    /// legacy <c>Employee.UserAccountId</c> pointer (a state the link and work-email guards still support).
    /// </summary>
    public static async Task<IReadOnlyList<int>> EmployeesOfLoginAsync(ZayraDbContext db, Guid tenantId, Guid userId, CancellationToken ct)
    {
        var mapped = await ScopedBypass.TenantWide(db.EmployeeUserAccounts, tenantId, Why).AsNoTracking()
            .Where(x => x.UserId == userId && !x.IsDeleted)
            .Select(x => x.EmployeeId)
            .ToListAsync(ct);
        var pointed = await ScopedBypass.NullableTenantWide(db.Employees, tenantId, Why).AsNoTracking()
            .Where(x => x.UserAccountId == userId && !x.IsDeleted)
            .Select(x => x.Id)
            .ToListAsync(ct);
        return mapped.Concat(pointed).Distinct().ToList();
    }

    /// <summary>True when the caller set the work email of any employee the login belongs to (live link or legacy pointer).</summary>
    public static async Task<bool> IsCallerSetterForLoginAsync(ZayraDbContext db, Guid tenantId, Guid userId, Guid? callerUserId, CancellationToken ct)
    {
        if (callerUserId is null) return false;
        var employeeIds = await EmployeesOfLoginAsync(db, tenantId, userId, ct);
        foreach (var employeeId in employeeIds)
            if (await IsCallerSetterAsync(db, tenantId, employeeId, callerUserId, ct)) return true;
        return false;
    }

    /// <summary>The same refusal for a credential issued against a LOGIN: checked for every employee the login is live-linked to.</summary>
    public static async Task ThrowIfCallerIsSetterForLoginAsync(ZayraDbContext db, Guid tenantId, Guid userId, Guid? callerUserId, CancellationToken ct)
    {
        if (callerUserId is null) return;
        var employeeIds = await EmployeesOfLoginAsync(db, tenantId, userId, ct);
        foreach (var employeeId in employeeIds)
            await ThrowIfCallerIsSetterAsync(db, tenantId, employeeId, callerUserId, ct);
    }

    private static (Guid? DraftedBy, IReadOnlyList<Guid> DraftSetters, bool ChangedAfterCreation) Parse(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return (null, Array.Empty<Guid>(), false);
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            var root = doc.RootElement;
            Guid? draftedBy = root.TryGetProperty("draftedBy", out var d) && d.ValueKind == JsonValueKind.String
                && Guid.TryParse(d.GetString(), out var id) ? id : null;
            var setters = new List<Guid>();
            if (root.TryGetProperty("draftEmailSetters", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var setter))
                        setters.Add(setter);
            // A change carries the old value (possibly ""); a creation records old = null.
            var changed = root.TryGetProperty("oldWorkEmail", out var old) && old.ValueKind == JsonValueKind.String;
            return (draftedBy, setters, changed);
        }
        catch (JsonException)
        {
            return (null, Array.Empty<Guid>(), false);
        }
    }
}

/// <summary>A credential for a changed work email, sent without the caller's confirmation. The Access API answers 400.</summary>
public sealed class WorkEmailConfirmationRequiredException : InvalidOperationException
{
    public WorkEmailConfirmationRequiredException() : base(WorkEmailSetterRule.ConfirmWorkEmailMessage) { }
    public string Code => WorkEmailSetterRule.ConfirmWorkEmailCode;
    public string MessageAr => WorkEmailSetterRule.ConfirmWorkEmailMessageAr;
}

/// <summary>A credential refused by the <see cref="WorkEmailSetterRule"/>. The Access API answers 403 with its code.</summary>
public sealed class WorkEmailSetterRefusedException : InvalidOperationException
{
    public WorkEmailSetterRefusedException(string code, string message, string messageAr) : base(message)
    {
        Code = code;
        MessageAr = messageAr;
    }

    public string Code { get; }
    public string MessageAr { get; }
}

/// <summary>
/// A work email whose local part carries '+' (sub-addressing). Plus-addresses route to the same mailbox under a
/// different login username, so they are refused on every path that sets a work email; existing rows are untouched.
/// </summary>
public sealed class WorkEmailPlusAddressException : Exception
{
    public const string Code = "work_email_plus_address";
    public const string Text = "Work email can't contain '+'.";

    public WorkEmailPlusAddressException() : base(Text) { }

    /// <summary>True when the address's local part contains '+'.</summary>
    public static bool IsPlusAddressed(string? workEmail)
    {
        var value = (workEmail ?? string.Empty).Trim();
        var at = value.IndexOf('@');
        var local = at < 0 ? value : value[..at];
        return local.Contains('+');
    }

    public static void ThrowIfPlusAddressed(string? workEmail)
    {
        if (IsPlusAddressed(workEmail)) throw new WorkEmailPlusAddressException();
    }
}
