using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// What an employee work-email edit may do to the employee's login. The work email IS the login's username
/// (KynexOne never creates mailboxes), and forgot-password mails whatever address the login carries — so an
/// employee edit that rewrote a live login's username let anyone with employee-edit rights (HR Officer, Payroll
/// Officer) repoint a colleague's login to an address they control and reset its password.
/// <list type="bullet">
///   <item>STAGED login (never activated: not active, still Invited / PendingPasswordSetup, never signed in): the
///     username follows the work email as before, and any outstanding invitation link is cancelled — it was sent
///     to the old address and must not be redeemable for the new one.</item>
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

    /// <summary>Never activated: cannot sign in, still waiting for its first password, and has never signed in.</summary>
    public static bool IsStaged(User user) =>
        user.LastLoginAtUtc is null
        && !user.IsActive
        && user.Status is "Invited" or "PendingPasswordSetup";

    /// <summary>
    /// Applies the rule for one employee whose work email may have changed from <paramref name="priorWorkEmail"/>.
    /// Throws <see cref="InvalidOperationException"/> before anything is written when a staged login's new
    /// username would collide with another login. Saves nothing itself: the caller's unit of work commits it.
    /// </summary>
    public static async Task<Outcome> ApplyAsync(
        ZayraDbContext db, Employee employee, Guid tenantId, string? priorWorkEmail, DateTime nowUtc, CancellationToken ct)
    {
        var newWorkEmail = (employee.WorkEmail ?? string.Empty).Trim();
        if (newWorkEmail.Length == 0) return Outcome.None;
        var newNorm = AuthService.Normalize(newWorkEmail);
        if (string.Equals(newNorm, AuthService.Normalize(priorWorkEmail ?? string.Empty), StringComparison.Ordinal))
            return Outcome.None;

        var links = await ScopedBypass.TenantWide(db.EmployeeUserAccounts, tenantId, Why)
            .Where(x => x.EmployeeId == employee.Id && !x.IsDeleted)
            .ToListAsync(ct);
        var userIds = links.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value)
            .Concat(employee.UserAccountId is Guid pointer ? new[] { pointer } : Array.Empty<Guid>())
            .Distinct()
            .ToList();
        if (userIds.Count == 0) return Outcome.None;

        var users = await ScopedBypass.TenantWide(db.Users, tenantId, Why)
            .Where(x => userIds.Contains(x.Id) && !x.IsDeleted)
            .OrderBy(x => x.Id)
            .ToListAsync(ct);

        var renamed = new List<object>();
        var held = new List<Guid>();
        foreach (var user in users)
        {
            if (string.Equals(user.NormalizedEmail, newNorm, StringComparison.Ordinal)) continue;
            if (!IsStaged(user))
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
            foreach (var link in links.Where(x => x.UserId == user.Id))
            {
                if (string.IsNullOrEmpty(link.InvitationTokenHash) && link.InvitationExpiresAtUtc is null) continue;
                link.InvitationTokenHash = string.Empty;
                link.InvitationExpiresAtUtc = null;
                link.UpdatedAtUtc = nowUtc;
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
