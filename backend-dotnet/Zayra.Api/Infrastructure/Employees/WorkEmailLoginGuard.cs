using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// What an employee work-email edit may do to the employee's login. The work email IS the login's username
/// (KynexOne never creates mailboxes), and forgot-password mails whatever address the login carries — so an
/// employee edit that rewrote a live login's username let anyone with employee-edit rights (HR Officer, Payroll
/// Officer) repoint a colleague's login to an address they control and reset its password.
/// <list type="bullet">
///   <item>STAGED login (<see cref="IsStaged"/>: its live link still awaits a first password, it has never signed
///     in, and nothing shows it was ever activated): the username follows the work email as before, and any
///     outstanding invitation link is cancelled — it was sent to the old address and must not be redeemable for
///     the new one.</item>
///   <item>ACTIVATED login (anything else): the login is left exactly as it is. The employee record keeps the new
///     work email, and the edit reports that the login's username now differs. Changing a live login's username
///     is a security-administration act, not an employee edit.</item>
/// </list>
/// The login is found through the live EmployeeUserAccounts link AND the legacy Employee.UserAccountId pointer,
/// so a login linked without the pointer is covered too.
/// </summary>
public static class WorkEmailLoginGuard
{
    private const string Why =
        "Work-email edit: the edited employee's own login is resolved through its link across legal entities; the tenant is re-applied and the employee row was already authorised for the caller.";

    public sealed record Outcome(string? RenamedJson, string? HeldJson, IReadOnlyList<Guid> HeldLoginIds)
    {
        public static readonly Outcome None = new(null, null, Array.Empty<Guid>());

        /// <summary>The work email changed but an activated login kept its username: the two now differ.</summary>
        public bool LoginUsernameDiffers => HeldLoginIds.Count > 0;
    }

    /// <summary>Audit actions (entity User) that prove a login was activated at some point: a password the owner
    /// set, a sign-in, or creation as an active login (Create User, import).</summary>
    private static readonly string[] ActivationEvidence =
        ["auth.invitation_accepted", "auth.password_reset", "auth.password_changed", "auth.login", "access.user_created"];

    /// <summary>
    /// STAGED = never activated. Every one of: a live link to this employee still awaiting its first password
    /// (RequiresPasswordSetup, invitation not accepted); never signed in; not active; still Invited or
    /// PendingPasswordSetup; email never confirmed; and no audit evidence of activation. Offboarding revocation puts
    /// an activated login back into PendingPasswordSetup and clears its link — the link's RequiresPasswordSetup and
    /// the activation evidence keep it from ever looking staged again.
    /// </summary>
    public static bool IsStaged(User user, EmployeeUserAccount? link, bool hasActivationEvidence) =>
        link is { IsDeleted: false, RequiresPasswordSetup: true, InvitationAcceptedAtUtc: null }
        && user.LastLoginAtUtc is null
        && !user.IsActive
        && !user.IsEmailConfirmed
        && user.Status is "Invited" or "PendingPasswordSetup"
        && !hasActivationEvidence;

    /// <summary>
    /// Applies the rule for one employee whose work email may have changed from <paramref name="priorWorkEmail"/>.
    /// Throws <see cref="InvalidOperationException"/> before anything is written when a staged login's new
    /// username would collide with another login. Saves nothing itself: the caller's unit of work commits it.
    /// </summary>
    /// <param name="context">The editor. Every work-email change is audited as
    /// <see cref="AccessManagementService.WorkEmailChangedAction"/> with them as actor, in the caller's unit of work —
    /// the two-person rule refuses a login link by whoever last changed the work email.</param>
    public static async Task<Outcome> ApplyAsync(
        ZayraDbContext db, Employee employee, Guid tenantId, string? priorWorkEmail, RequestContext context, DateTime nowUtc, CancellationToken ct)
    {
        var newWorkEmail = (employee.WorkEmail ?? string.Empty).Trim();
        if (newWorkEmail.Length == 0) return Outcome.None;
        var newNorm = AuthService.Normalize(newWorkEmail);
        if (string.Equals(newNorm, AuthService.Normalize(priorWorkEmail ?? string.Empty), StringComparison.Ordinal))
            return Outcome.None;

        db.AuditLogs.Add(AuthAuditEntry.Create(
            Guid.NewGuid(),
            nowUtc,
            AccessManagementService.WorkEmailChangedAction,
            "Employee",
            employee.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            context with { TenantId = tenantId },
            JsonSerializer.Serialize(new { oldWorkEmail = (priorWorkEmail ?? string.Empty).Trim(), newWorkEmail })));

        var links = await ScopedBypass.TenantWide(db.EmployeeUserAccounts, tenantId, Why)
            .Where(x => x.EmployeeId == employee.Id && !x.IsDeleted)
            .ToListAsync(ct);
        var userIds = links.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value)
            .Concat(employee.UserAccountId is Guid pointer ? new[] { pointer } : Array.Empty<Guid>())
            .Distinct()
            .ToList();
        if (userIds.Count == 0) return Outcome.None;

        // Locked before the staged decision, so an invitation accepted concurrently cannot slip between the check
        // and the rename (the lock holds for the caller's transaction).
        var users = await ScopedBypass.TenantWide(db.Users, tenantId, Why)
            .TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => userIds.Contains(x.Id) && !x.IsDeleted)
            .OrderBy(x => x.Id)
            .ToListAsync(ct);
        var userKeys = users.Select(x => x.Id.ToString()).ToList();
        var activated = (await ScopedBypass.NullableTenantWide(db.AuditLogs, tenantId, Why).AsNoTracking()
                .Where(x => x.EntityName == "User" && x.EntityId != null && userKeys.Contains(x.EntityId)
                    && ActivationEvidence.Contains(x.Action))
                .Select(x => x.EntityId!)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var renamed = new List<object>();
        var held = new List<Guid>();
        foreach (var user in users)
        {
            if (string.Equals(user.NormalizedEmail, newNorm, StringComparison.Ordinal)) continue;
            var link = links.FirstOrDefault(x => x.UserId == user.Id);
            if (!IsStaged(user, link, activated.Contains(user.Id.ToString())))
            {
                held.Add(user.Id);
                continue;
            }

            var clash = await ScopedBypass.TenantWide(db.Users, tenantId, Why)
                .AnyAsync(x => x.Id != user.Id && x.NormalizedEmail == newNorm, ct);
            if (clash)
                throw new InvalidOperationException(
                    $"Cannot rename work email to '{newWorkEmail}': another login already uses that address. Resolve the conflicting account first.");

            var oldEmail = user.Email;
            user.Email = newWorkEmail.ToLowerInvariant();
            user.NormalizedEmail = newNorm;
            user.UpdatedAtUtc = nowUtc;
            var invitationsCancelled = 0;
            foreach (var row in links.Where(x => x.UserId == user.Id))
            {
                if (string.IsNullOrEmpty(row.InvitationTokenHash) && row.InvitationExpiresAtUtc is null) continue;
                row.InvitationTokenHash = string.Empty;
                row.InvitationExpiresAtUtc = null;
                row.UpdatedAtUtc = nowUtc;
                invitationsCancelled++;
            }
            renamed.Add(new
            {
                userId = user.Id,
                oldEmail,
                newEmail = user.Email,
                invitationsCancelled,
                note = "Staged login: username follows the work email; any invitation sent to the old address was cancelled. No mailbox provisioned."
            });
        }

        return new Outcome(
            renamed.Count == 0 ? null : JsonSerializer.Serialize(renamed.Count == 1 ? renamed[0] : renamed),
            held.Count == 0 ? null : JsonSerializer.Serialize(new
            {
                loginIds = held,
                note = "Activated login kept its username; the employee's work email now differs from it. Change a live login's username through security administration."
            }),
            held);
    }
}
