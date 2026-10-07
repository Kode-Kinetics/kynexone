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
    public const string SetByHandlerCode = "work_email_set_by_handler";

    public const string SetByCallerMessage =
        "You set this employee's work email, so you cannot also issue or link a credential for their login. Another administrator must do it.";
    public const string SetByCallerMessageAr =
        "أنت من عيّن البريد الإلكتروني للعمل لهذا الموظف، لذا لا يمكنك أيضاً إصدار بيانات اعتماد لحساب دخوله أو ربطه. يجب أن يقوم بذلك مسؤول آخر.";

    private const string Why =
        "Work-email setter rule: who last set one employee's work email is read from the tenant's audit trail; the tenant is re-applied and the employee was already scope-checked by the caller.";

    /// <summary>The last work-email set for an employee: who did it (and, for a draft approval, who drafted it), and when.</summary>
    public sealed record Setter(Guid? ActorUserId, Guid? DraftedByUserId, DateTime SetAtUtc)
    {
        /// <summary>Everyone counted as having set the address.</summary>
        public IReadOnlyCollection<Guid> UserIds =>
            new[] { ActorUserId, DraftedByUserId }.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();

        public bool Includes(Guid? userId) => userId is Guid id && UserIds.Contains(id);
    }

    /// <summary>The latest setter of <paramref name="employeeId"/>'s work email, or null when none is recorded.</summary>
    public static async Task<Setter?> GetAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct)
    {
        var employeeKey = employeeId.ToString(CultureInfo.InvariantCulture);
        var row = await ScopedBypass.NullableTenantWide(db.AuditLogs, tenantId, Why).AsNoTracking()
            .Where(x => x.EntityName == "Employee" && x.EntityId == employeeKey && x.Action == AccessManagementService.WorkEmailChangedAction)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Select(x => new { x.UserId, x.Metadata, x.CreatedAtUtc })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : new Setter(row.UserId, DraftedBy(row.Metadata), row.CreatedAtUtc);
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

    /// <summary>The same refusal for a credential issued against a LOGIN: checked for every employee the login is live-linked to.</summary>
    public static async Task ThrowIfCallerIsSetterForLoginAsync(ZayraDbContext db, Guid tenantId, Guid userId, Guid? callerUserId, CancellationToken ct)
    {
        if (callerUserId is null) return;
        var employeeIds = await ScopedBypass.TenantWide(db.EmployeeUserAccounts, tenantId, Why).AsNoTracking()
            .Where(x => x.UserId == userId && !x.IsDeleted)
            .Select(x => x.EmployeeId)
            .Distinct()
            .ToListAsync(ct);
        foreach (var employeeId in employeeIds)
            await ThrowIfCallerIsSetterAsync(db, tenantId, employeeId, callerUserId, ct);
    }

    private static Guid? DraftedBy(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            return doc.RootElement.TryGetProperty("draftedBy", out var value)
                && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var id)
                ? id
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
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
