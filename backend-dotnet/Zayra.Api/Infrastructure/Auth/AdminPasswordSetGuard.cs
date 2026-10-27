using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// Which accounts an administrator may set a temporary password for. Checked once when the request is authorized and
/// again under the user row lock when the password is written, so both sides refuse the same accounts.
/// </summary>
internal static class AdminPasswordSetGuard
{
    public static void ThrowIfAccountCannotTakeOne(User user)
    {
        if (string.Equals(user.AccessMode, AccessModes.NoLogin, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "This person has no portal login, so there is no password to set. Invite them to the portal first.");
        if (!string.Equals(user.IdentityProvider, "Local", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "This person signs in through your company's single sign-on, so their password is managed there.");
        if (user.Status is "Invited" or "PendingPasswordSetup")
            throw new InvalidOperationException(
                "This person has not finished setting up their login yet. Resend the invitation instead.");
        if (!user.IsActive || user.Status is "Deactivated" or "Suspended")
            throw new InvalidOperationException(
                "This account is not active. Restore access first, then set a temporary password.");
        if (!user.IsEmailConfirmed)
            throw new InvalidOperationException(
                "This person's email address is not confirmed yet, so they could not sign in with a temporary password.");
    }
}
