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
    /// <summary>Atomically verifies and consumes a tenant-login MFA challenge, then issues one session.</summary>
    Task<AuthResponse> CompleteMfaLoginAsync(string challengeToken, string totpCode, RequestContext context, CancellationToken cancellationToken);

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
