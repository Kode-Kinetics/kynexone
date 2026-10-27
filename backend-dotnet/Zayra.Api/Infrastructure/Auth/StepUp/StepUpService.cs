using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Auth.StepUp;

public sealed class StepUpService : IStepUpService
{
    public StepUpService(ZayraDbContext db, IPasswordHasher hasher, TotpService totp, IConfiguration config,
        PasswordVerificationGate? gate = null, Func<DateTime>? clock = null) { }

    public Task<StepUpResult> VerifyAsync(Guid userId, string purpose, StepUpCredential credential, CancellationToken ct) => throw new NotImplementedException();
    public bool IsFresh(string? token, ClaimsPrincipal principal, string purpose, TimeSpan max) => throw new NotImplementedException();
    public bool IsFresh(string? token, Guid userId, Guid tenantId, string purpose, TimeSpan max) => throw new NotImplementedException();
    public string? ReadJti(string? token, Guid userId, Guid tenantId, string purpose) => throw new NotImplementedException();
    /// <summary>Test seam: mint a token exactly as VerifyAsync does, without the credential check.</summary>
    internal string Issue(Guid userId, Guid tenantId, string purpose, DateTime nowUtc, string? bind = null) => throw new NotImplementedException();
}
