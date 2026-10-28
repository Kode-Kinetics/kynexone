using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Esign;
using Zayra.Api.Models;

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

/// <summary>
/// Records a click-to-sign acknowledgement. Everything that is evidence is derived here, on the server: the time, the document hash
/// (from the stored bytes), the consent text hash, the chain link. The browser supplies only what the employee did (typed name,
/// the hash and consent version they were shown, a language and a device label); a mismatch with the server's view is a refusal.
/// The record, its certificate document and an audit row are written in ONE transaction, serialised per tenant so the hash chain
/// has no fork and no gap.
/// </summary>
public sealed class PolicySigningService(ZayraDbContext db, IDocumentStorage storage, IEssStepUpService stepUp, PdfRenderGate? gate = null, Func<DateTime>? clock = null)
{
    public static string DeviceBinding(int employeeId, Guid documentId, string sha256) => $"{employeeId}:{documentId:N}:{sha256.ToLowerInvariant()}";

    private DateTime Now() => SignatureChain.ToMicroseconds((clock ?? (() => DateTime.UtcNow))());

    private static SignOutcome Fail(int status, string code, Guid? ackId = null) => new(status, code, ackId, null, null, null, null);

    public async Task<SignOutcome> SignAsync(SignCommand c, CancellationToken ct)
    {
        var tenant = c.TenantId;
        var doc = await db.EmployeeDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.TenantId == tenant && d.Id == c.DocumentId && d.EmployeeId == c.EmployeeId
            && !d.IsDeleted && d.SignatureRequiredAtUtc != null && (d.ScanStatus == ScanStatuses.Clean || d.ScanStatus == ScanStatuses.NotScannedTrustedSource), ct);
        var employee = doc is null ? null : await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenant && e.Id == c.EmployeeId && !e.IsDeleted, ct);
        if (doc is null || employee is null) return Fail(404, EsignProblemCodes.NotFound);

        var existingId = await ExistingAckAsync(tenant, c.EmployeeId, c.DocumentId, ct);
        if (existingId is not null) return Fail(409, EsignProblemCodes.AlreadySigned, existingId);

        // Ten signatures an hour per person: counted from the evidence itself, so it cannot be reset by restarting anything.
        var now = Now();
        var recent = await db.EmployeePolicyAcknowledgements.AsNoTracking()
            .CountAsync(a => a.TenantId == tenant && a.UserId == c.SignerUserId && a.Method != EsignConstants.MethodLegacy && a.AcknowledgedAtUtc >= now.AddHours(-1), ct);
        if (recent >= EsignConstants.MaxSignsPerHour) return Fail(429, EsignProblemCodes.RateLimited);

        byte[] bytes;
        try { bytes = await storage.GetBytesAsync(tenant, doc.StorageUrl, ct); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException) { return Fail(404, EsignProblemCodes.NotFound); }
        var sha = SignatureChain.Sha256Hex(bytes);

        // An HR-device token was issued for exactly this employee, this document and these bytes, to this HR user.
        var bind = c.WitnessUserId is null ? null : DeviceBinding(c.EmployeeId, c.DocumentId, sha);
        var jtiText = stepUp.ReadJti(c.StepUpToken, c.SignerUserId, tenant, StepUpPurposes.Esign, bind, c.WitnessUserId);
        var fresh = stepUp.IsFresh(c.StepUpToken, c.SignerUserId, tenant, StepUpPurposes.Esign, StepUpPurposes.EsignMaxAge, bind, c.WitnessUserId) && Guid.TryParseExact(jtiText, "N", out _);
        var refusal = ClickSignGuard.Evaluate(new ClickSignFacts(sha, c.Body.DocumentSha256, c.SignerUserId, c.EmployeeUserId, fresh, c.Body.ConsentVersion, c.Body.TypedName,
            [employee.EnglishName, employee.ArabicName, employee.FullName], doc.IntegrityFlaggedAtUtc is not null, false));
        if (refusal is not null) return Fail(refusal.Status, refusal.Code);

        var jti = Guid.ParseExact(jtiText!, "N");
        var typed = c.Body.TypedName!.Trim();
        var language = string.Equals(c.Body.Language, "ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en";
        var consentVersion = ConsentCatalog.Current;
        var consentHash = ConsentCatalog.TextSha256(consentVersion);
        var method = c.WitnessUserId is null ? EsignConstants.MethodEss : EsignConstants.MethodHrDevice;
        var user = await db.Users.AsNoTracking().Where(u => u.Id == c.SignerUserId).Select(u => new { u.MFAEnabled, u.MfaSecretEncrypted }).FirstOrDefaultAsync(ct);
        var authMethod = user is { MFAEnabled: true, MfaSecretEncrypted: { Length: > 0 } } ? EsignConstants.AuthPasswordMfa : EsignConstants.AuthPassword;
        var companyName = await CompanyNameAsync(tenant, employee, ct);
        string? witnessName = c.WitnessUserId is null ? null : await db.Users.AsNoTracking().Where(u => u.Id == c.WitnessUserId).Select(u => u.FullName).FirstOrDefaultAsync(ct);

        var ackId = Guid.NewGuid(); var certId = Guid.NewGuid();
        var certKey = storage.TenantKey(tenant, $"esign/{ackId:N}.pdf");
        var storedCert = false;
        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                if ((db.Database.ProviderName ?? string.Empty).Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
                {
                    // One chain per tenant: the second signer waits here until the first has committed, then reads the new tail.
                    var key = $"ack:{tenant:N}";
                    await db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\" FROM (SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))) AS t").ToListAsync(ct);
                }
                var again = await ExistingAckAsync(tenant, c.EmployeeId, c.DocumentId, ct);
                if (again is not null) return Fail(409, EsignProblemCodes.AlreadySigned, again);

                var tail = await db.EmployeePolicyAcknowledgements.AsNoTracking().Where(a => a.TenantId == tenant && a.ChainSeq != null)
                    .OrderByDescending(a => a.ChainSeq).Select(a => new { a.ChainSeq, a.RecordHash }).FirstOrDefaultAsync(ct);
                var prev = tail?.RecordHash ?? EsignConstants.ZeroHash;
                var signedAt = Now();
                var evidence = new AckEvidence(tenant, c.EmployeeId, c.DocumentId, sha, typed, signedAt, c.IpAddress, Clip(c.UserAgent, 400), c.SignerUserId, c.WitnessUserId, consentHash, method);
                var recordHash = SignatureChain.ComputeHash(prev, evidence);
                var code = VerificationCode.From(recordHash);

                var render = () => Task.FromResult(AckCertificateRenderer.Render(new AckCertificateData(companyName, DisplayName(employee), employee.EmployeeCode, doc.DocumentType, doc.FileName, sha, typed,
                    signedAt, method, witnessName, consentVersion, ConsentCatalog.Text(consentVersion, "en"), ConsentCatalog.Text(consentVersion, "ar"), recordHash, code)));
                var certBytes = gate is null ? await render() : await gate.RenderAsync(render, ct);
                await storage.PutAtAsync(tenant, certKey, certBytes, "application/pdf", ct);
                storedCert = true;

                db.EmployeeDocuments.Add(new EmployeeDocument
                {
                    Id = certId, TenantId = tenant, CompanyId = employee.CompanyId, EmployeeId = employee.Id, DocumentType = EsignConstants.DocumentType, DocumentCategory = "Acknowledgement",
                    FileName = $"acknowledgement-{code}.pdf", ContentType = "application/pdf", StorageUrl = certKey, ApprovalStatus = "Approved", UploadedBy = c.SignerUserId,
                    UploadedAtUtc = signedAt, Notes = $"Certificate of electronic acknowledgement for document {doc.Id}",
                });
                db.EmployeePolicyAcknowledgements.Add(new EmployeePolicyAcknowledgement
                {
                    Id = ackId, TenantId = tenant, EmployeeId = c.EmployeeId, PolicyId = c.DocumentId, UserId = c.SignerUserId, AcknowledgedAtUtc = signedAt, Method = method,
                    DocumentSha256 = sha, DocumentVersion = doc.VersionNumber, ConsentVersion = consentVersion, ConsentTextSha256 = consentHash, TypedName = typed, IpAddress = c.IpAddress,
                    UserAgent = Clip(c.UserAgent, 400), DeviceId = Clip(c.Body.DeviceId, 64), Language = language, AuthMethod = authMethod, StepUpAtUtc = signedAt, StepUpJti = jti,
                    WitnessUserId = c.WitnessUserId, CertificateDocumentId = certId, ChainSeq = (tail?.ChainSeq ?? 0) + 1, PrevHash = prev, RecordHash = recordHash,
                });
                db.EmployeeSelfServiceAuditLogs.Add(new EmployeeSelfServiceAuditLog
                {
                    TenantId = tenant, EmployeeId = c.EmployeeId, UserId = c.WitnessUserId ?? c.SignerUserId, Action = method == EsignConstants.MethodHrDevice ? "esign.signed_on_hr_device" : "esign.signed",
                    EntityName = "EmployeeDocument", EntityId = c.DocumentId.ToString(),
                });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new SignOutcome(201, null, ackId, recordHash, signedAt, certId, code);
            });
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (storedCert) await storage.TryDeleteAsync(tenant, certKey, CancellationToken.None);
            var winner = await ExistingAckAsync(tenant, c.EmployeeId, c.DocumentId, CancellationToken.None);
            if (winner is not null) return Fail(409, EsignProblemCodes.AlreadySigned, winner);
            if (await db.EmployeePolicyAcknowledgements.AsNoTracking().AnyAsync(a => a.StepUpJti == jti, CancellationToken.None)) return Fail(401, EsignProblemCodes.StepUpRequired);
            throw;
        }
        catch
        {
            if (storedCert) await storage.TryDeleteAsync(tenant, certKey, CancellationToken.None);
            throw;
        }
    }

    public async Task<VerifyOutcome> VerifyAsync(Guid tenantId, Guid ackId, int? employeeId, CancellationToken ct)
    {
        var checkedAt = Now();
        var ack = await db.EmployeePolicyAcknowledgements.AsNoTracking().FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == ackId
            && a.Method != EsignConstants.MethodLegacy && (employeeId == null || a.EmployeeId == employeeId), ct);
        if (ack is null) return new VerifyOutcome(false, false, false, false, null, checkedAt, null);

        var recordValid = SignatureChain.IsRecordValid(ack.PrevHash!, ack.RecordHash!, EvidenceOf(ack));
        bool chainIntact;
        if (ack.ChainSeq == 1) chainIntact = recordValid && ack.PrevHash == EsignConstants.ZeroHash;
        else
        {
            var before = await db.EmployeePolicyAcknowledgements.AsNoTracking().FirstOrDefaultAsync(a => a.TenantId == tenantId && a.ChainSeq == ack.ChainSeq - 1 && a.Method != EsignConstants.MethodLegacy, ct);
            if (before is not null)
                chainIntact = recordValid && ack.PrevHash == before.RecordHash && SignatureChain.IsRecordValid(before.PrevHash!, before.RecordHash!, EvidenceOf(before));
            else
                // The predecessor is gone. That is expected only when the retention purge removed it and left its marker; any other absence is a break.
                chainIntact = recordValid && await db.EmployeeSelfServiceAuditLogs.AsNoTracking().AnyAsync(l => l.TenantId == tenantId
                    && l.Action == Zayra.Api.Infrastructure.Retention.Rules.EsignEvidenceRetentionRule.PurgeMarkerAction && l.EntityId == ack.PrevHash, ct);
        }

        var doc = await db.EmployeeDocuments.FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Id == ack.PolicyId, ct);
        var documentIntact = false;
        if (doc is not null)
        {
            try { documentIntact = string.Equals(SignatureChain.Sha256Hex(await storage.GetBytesAsync(tenantId, doc.StorageUrl, ct)), ack.DocumentSha256, StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException) { documentIntact = false; }
        }
        if (!documentIntact || !chainIntact)
        {
            if (doc is not null && doc.IntegrityFlaggedAtUtc is null && !documentIntact) doc.IntegrityFlaggedAtUtc = checkedAt;
            // Asking again must not write again: one alarm per record and kind, however often the page is opened.
            var action = documentIntact ? "esign.chain_failed" : "esign.integrity_failed";
            var already = await db.EmployeeSelfServiceAuditLogs.AsNoTracking().AnyAsync(l => l.TenantId == tenantId && l.Action == action && l.EntityId == ack.Id.ToString(), ct);
            if (!already)
                db.EmployeeSelfServiceAuditLogs.Add(new EmployeeSelfServiceAuditLog
                {
                    TenantId = tenantId, EmployeeId = ack.EmployeeId, Action = action, EntityName = "EmployeePolicyAcknowledgement", EntityId = ack.Id.ToString(),
                });
            if (!already || db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        }
        return new VerifyOutcome(true, recordValid && documentIntact && chainIntact, documentIntact, chainIntact, ack.RecordHash, checkedAt, ack.PolicyId);
    }

    private static AckEvidence EvidenceOf(EmployeePolicyAcknowledgement a) => new(a.TenantId, a.EmployeeId, a.PolicyId, a.DocumentSha256 ?? string.Empty, a.TypedName ?? string.Empty,
        a.AcknowledgedAtUtc, a.IpAddress ?? string.Empty, a.UserAgent, a.UserId ?? Guid.Empty, a.WitnessUserId, a.ConsentTextSha256 ?? string.Empty, a.Method);

    private async Task<Guid?> ExistingAckAsync(Guid tenant, int employeeId, Guid documentId, CancellationToken ct) =>
        await db.EmployeePolicyAcknowledgements.AsNoTracking().Where(a => a.TenantId == tenant && a.EmployeeId == employeeId && a.PolicyId == documentId)
            .Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);

    private async Task<string> CompanyNameAsync(Guid tenant, Employee employee, CancellationToken ct)
    {
        if (employee.CompanyId is not null)
        {
            var name = await db.Companies.AsNoTracking().Where(x => x.Id == employee.CompanyId).Select(x => x.LegalNameEn).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return await db.Tenants.AsNoTracking().Where(t => t.Id == tenant).Select(t => t.Name).FirstOrDefaultAsync(ct) ?? "KynexOne";
    }

    private static string DisplayName(Employee e) => !string.IsNullOrWhiteSpace(e.EnglishName) ? e.EnglishName : e.FullName;
    private static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
