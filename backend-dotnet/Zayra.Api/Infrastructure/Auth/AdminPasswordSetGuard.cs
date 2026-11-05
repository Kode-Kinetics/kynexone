using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// Which accounts an administrator may set a temporary password for. Checked once when the request is authorized and
/// again under the user row lock when the password is written, so both sides refuse the same accounts.
/// </summary>
internal static class AdminPasswordSetGuard
{
    /// <summary>The login is staged: invited, nothing accepted yet (an invited login is stored as NoLogin until it is).</summary>
    public static bool IsAwaitingInvitation(User user) => user.Status is "Invited" or "PendingPasswordSetup";

    /// <summary>The live invitation a hand-over would complete: not deleted, a real access mode, still awaiting setup.</summary>
    public static EmployeeUserAccount? PendingInvitation(User user) => user.EmployeeUserAccounts
        .Where(x => !x.IsDeleted && x.RequiresPasswordSetup && x.Status == "Invited"
            && !string.Equals(x.AccessMode, AccessModes.NoLogin, StringComparison.Ordinal))
        .OrderByDescending(x => x.IsPrimary).ThenByDescending(x => x.CreatedAtUtc)
        .FirstOrDefault();

    public static void ThrowIfAccountCannotTakeOne(User user, bool allowInvitationHandover)
    {
        // An invited login is staged as NoLogin until accepted, so this must be decided first. A temporary password
        // handed over by an administrator completes the invitation (they vouch for the person; audited, and the
        // person must choose their own password at first sign-in). It needs an invitation that is really pending.
        if (IsAwaitingInvitation(user))
        {
            if (!string.Equals(user.IdentityProvider, "Local", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "This person signs in through your company's single sign-on, so their password is managed there.");
            if (!allowInvitationHandover)
                throw new InvalidOperationException(
                    "This person has been invited but has not accepted yet. Invite them again to get a fresh link and hand it over. "
                    + "Temporary passwords for invited people are switched off for this workspace; your platform administrator can enable them.");
            if (PendingInvitation(user) is null)
                throw new InvalidOperationException(
                    "This person has no pending invitation to complete. Invite them again, then set a temporary password.");
            return;
        }
        if (string.Equals(user.AccessMode, AccessModes.NoLogin, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "This person has no portal login, so there is no password to set. Invite them to the portal first.");
        if (!string.Equals(user.IdentityProvider, "Local", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "This person signs in through your company's single sign-on, so their password is managed there.");
        if (!user.IsActive || user.Status is "Deactivated" or "Suspended")
            throw new InvalidOperationException(
                "This account is not active. Restore access first, then set a temporary password.");
        // An unconfirmed email is not a reason to refuse: this path exists for people whose email is not working. The
        // administrator vouches for the address (the service confirms it and the audit records that they did).
    }
}
