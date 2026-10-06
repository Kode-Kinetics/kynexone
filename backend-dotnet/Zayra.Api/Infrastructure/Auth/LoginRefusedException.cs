namespace Zayra.Api.Infrastructure.Auth;

/// <summary>A sign-in refused before any hashing (LoginAbuseGuard); the endpoint answers 429.</summary>
public sealed class LoginRefusedException(LoginRefusal refusal, int retryAfterSeconds)
    : Exception(LoginAbuseGuard.Describe(refusal, retryAfterSeconds).Message)
{
    public LoginRefusal Refusal { get; } = refusal;
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}
