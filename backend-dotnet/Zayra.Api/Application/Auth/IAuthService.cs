namespace Zayra.Api.Application.Auth;

public interface IAuthService
{
    Task<AuthLoginResult> LoginAsync(LoginRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, RequestContext context, CancellationToken cancellationToken);
    Task LogoutAsync(LogoutRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<ForgotPasswordResponse> ForgotPasswordAsync(ForgotPasswordRequest request, RequestContext context, CancellationToken cancellationToken);
    Task ResetPasswordAsync(ResetPasswordRequest request, RequestContext context, CancellationToken cancellationToken);
    Task AcceptInvitationAsync(AcceptInvitationRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<AuthUserDto?> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken);
    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, RequestContext context, CancellationToken cancellationToken);
    /// <summary>
    /// Sets a temporary password an administrator chose for another user, already authorized by
    /// IAccessManagementService.AuthorizeAdminPasswordSetAsync. Ends every session and outstanding credential; the user
    /// must replace it at their next sign-in, and is told it happened.
    /// </summary>
    Task AdminSetTemporaryPasswordAsync(Guid tenantId, Guid userId, string temporaryPassword, string reason, RequestContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException();
    /// <summary>Atomically verifies and consumes a tenant-login MFA challenge, then issues one session.</summary>
    Task<AuthResponse> CompleteMfaLoginAsync(string challengeToken, string totpCode, RequestContext context, CancellationToken cancellationToken);
    /// <summary>As above; <paramref name="rememberDevice"/> also trusts this browser (MfaTrustedDevices) when the tenant allows it.</summary>
    Task<AuthResponse> CompleteMfaLoginAsync(string challengeToken, string totpCode, RequestContext context, bool rememberDevice, CancellationToken cancellationToken)
        => CompleteMfaLoginAsync(challengeToken, totpCode, context, cancellationToken);
    /// <summary>
    /// Completes the code step with a one-time backup code instead of the app code. The code is consumed in the same
    /// transaction as the session; it never trusts the browser. A wrong, used or malformed code throws
    /// <see cref="MfaCodeInvalidException"/>; an invalid challenge throws <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    /// <summary>
    /// Amendment 1: right after the signed-in caller turned two-step on (which ended every session, theirs included),
    /// issues them one new session exactly as login does. Only for the factor configured at
    /// <paramref name="configuredAtUtc"/>, only once, and only while the account may hold a session; null otherwise.
    /// </summary>
    Task<AuthResponse?> IssueSessionAfterMfaSetupAsync(Guid userId, Guid tenantId, DateTime configuredAtUtc, RequestContext context, CancellationToken cancellationToken)
        => Task.FromResult<AuthResponse?>(null);
    Task<AuthResponse> CompleteMfaLoginWithBackupCodeAsync(string challengeToken, string backupCode, RequestContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException("This IAuthService does not support backup codes.");

    /// <summary>The workspace slug the email's domain routes to (unique active match only), or null.</summary>
    Task<string?> ResolveWorkspaceAsync(string? email, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <summary>POST api/auth/welcome/redeem. Never issues a session.</summary>
    Task<Zayra.Api.Infrastructure.Auth.WelcomeRedeemResponse> RedeemWelcomeCodeAsync(
        Zayra.Api.Infrastructure.Auth.WelcomeRedeemRequest request, RequestContext context,
        Zayra.Api.Infrastructure.Auth.WelcomeCodeRedeemer.Presenter presenter, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>GET api/auth/password-policy.</summary>
    Task<PasswordPolicyDto> GetPasswordPolicyAsync(string? tenantSlug, CancellationToken cancellationToken) =>
        Task.FromResult(new PasswordPolicyDto(10));
}
