using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Application.Esign;

/// <summary>
/// A document somebody signed (and the certificate that evidences it) is evidence: it is never edited, replaced or archived.
/// A changed document is a NEW version with its own request, never an edit of the signed one.
/// </summary>
public static class SignedDocumentGuard
{
    public const string Message = "This document is retained as signed evidence and cannot be changed or archived. Upload a new version instead.";

    public static async Task EnsureMutableAsync(ZayraDbContext db, Guid tenantId, Guid documentId, CancellationToken ct)
    {
        var document = await db.EmployeeDocuments.AsNoTracking().Where(d => d.TenantId == tenantId && d.Id == documentId)
            .Select(d => new { d.DocumentType }).FirstOrDefaultAsync(ct);
        if (document is null) return;
        if (string.Equals(document.DocumentType, EsignConstants.DocumentType, StringComparison.OrdinalIgnoreCase)
            || await db.EmployeePolicyAcknowledgements.AsNoTracking().AnyAsync(a => a.TenantId == tenantId && a.PolicyId == documentId && a.Method != EsignConstants.MethodLegacy, ct)
            || await db.EmployeePolicyAcknowledgements.AsNoTracking().AnyAsync(a => a.TenantId == tenantId && a.CertificateDocumentId == documentId, ct))
            throw new InvalidOperationException(Message);
    }
}

/// <summary>The legacy one-click acknowledge route's answer for a document that needs a real signature.</summary>
public static class PolicySigningGate
{
    public static async Task<object?> RefusalAsync(ZayraDbContext db, Guid tenantId, Guid documentId, CancellationToken ct)
    {
        var required = await db.EmployeeDocuments.AsNoTracking().AnyAsync(d => d.TenantId == tenantId && d.Id == documentId && d.SignatureRequiredAtUtc != null, ct);
        return !required ? null : new
        {
            code = EsignProblemCodes.SignatureRequired, titleKey = EsignConstants.ProblemKeyPrefix + EsignProblemCodes.SignatureRequired,
            message = "This document needs your signature. Please sign it from Documents to sign on the KynexOne web app.",
            messageAr = "هذا المستند يتطلب توقيعك. يرجى توقيعه من قسم المستندات المطلوب توقيعها في تطبيق KynexOne على الويب.",
        };
    }
}
