using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Recruitment;

/// <summary>A blacklisted candidate is out of every hiring step: they cannot apply, be scheduled for an interview, be moved forward, be given an
/// offer, or be hired from an offer that was already sent. Rejecting an existing application stays possible.</summary>
public static class CandidateStanding
{
    public const string Blacklisted = "Blacklisted";
    public const string ErrorCode = "candidate_blacklisted";
    public const string Message = "This candidate is blacklisted and cannot be considered for a role. Ask an HR Manager if the listing is a mistake.";
    public const string MessageAr = "هذا المرشح مدرج في القائمة السوداء ولا يمكن النظر في ترشيحه لأي وظيفة. اسأل مدير الموارد البشرية إن كان القيد خطأ.";

    public static Task<bool> IsBlacklistedAsync(ZayraDbContext db, Guid tenantId, Guid candidateId, CancellationToken ct) =>
        db.Candidates.AsNoTracking().AnyAsync(c => c.TenantId == tenantId && c.Id == candidateId && c.Status == Blacklisted, ct);
}
