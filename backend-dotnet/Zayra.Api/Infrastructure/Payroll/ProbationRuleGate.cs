using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Payroll;

public sealed record ProbationFinding(string Message, int Days, int MaxDays);

/// <summary>
/// The one check behind every place a probation is entered by hand (create, edit, draft submit): is it longer than
/// the employing company's own maximum? Returns the finding or null; the caller decides whether to ask for an
/// acknowledgement. A company that set no maximum is never warned.
/// </summary>
public static class ProbationRuleGate
{
    public static async Task<ProbationFinding?> CheckAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, DateOnly? start, DateOnly? end, CancellationToken ct)
    {
        if (companyId is not { } company || end is not { } last || start is not { } first) return null;
        var rules = await CompanyPayrollRules.ResolveAsync(db, tenantId, company, ct);
        if (CompanyPayrollRules.ProbationDatesOverLimit(rules, first, last) is not { } message) return null;
        return new ProbationFinding(message, last.DayNumber - first.DayNumber + 1, rules.MaxProbationDays!.Value);
    }

    /// <summary>The audit payload for an acknowledged overrun.</summary>
    public static string AuditJson(ProbationFinding f, DateOnly? start, DateOnly? end) =>
        System.Text.Json.JsonSerializer.Serialize(new { days = f.Days, maxDays = f.MaxDays, probationStart = start, probationEnd = end });
}
