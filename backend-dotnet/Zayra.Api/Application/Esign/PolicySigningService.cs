using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;

namespace Zayra.Api.Application.Esign;

/// <param name="EmployeeUserId">The login of the employee the record belongs to.</param>
/// <param name="SignerUserId">The login whose fresh step-up proof opens the signature. Always the employee's own.</param>
/// <param name="WitnessUserId">HR user holding the device when the employee signs on it; null for self-service.</param>
public sealed record SignCommand(Guid TenantId, int EmployeeId, Guid EmployeeUserId, Guid SignerUserId, Guid? WitnessUserId, Guid DocumentId,
    EsignSignBody Body, string? StepUpToken, string IpAddress, string? UserAgent);

public sealed record SignOutcome(int Status, string? Code, Guid? AckId, string? RecordHash, DateTime? SignedAtUtc, Guid? CertificateDocumentId, string? VerificationCode)
{
    public bool Ok => Status == 201;
}

public sealed record VerifyOutcome(bool Found, bool Valid, bool DocumentIntact, bool ChainIntact, string? RecordHash, DateTime CheckedAtUtc, Guid? DocumentId);

public sealed class PolicySigningService(ZayraDbContext db, IDocumentStorage storage, IStepUpService stepUp, PdfRenderGate? gate = null, Func<DateTime>? clock = null)
{
    public Task<SignOutcome> SignAsync(SignCommand command, CancellationToken ct) => throw new NotImplementedException();

    public Task<VerifyOutcome> VerifyAsync(Guid tenantId, Guid ackId, int? employeeId, CancellationToken ct) => throw new NotImplementedException();
}
