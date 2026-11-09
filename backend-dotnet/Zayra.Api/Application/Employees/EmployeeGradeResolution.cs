using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees;

/// <summary>A grade edit that cannot be applied as asked. Carries both languages so the screen can show the right one.</summary>
public sealed class GradeNotResolvedException(string message, string messageAr) : InvalidOperationException(message)
{
    public const string Code = "grade_not_resolved";
    public string MessageAr { get; } = messageAr;
    public object ToPayload() => new { error = Code, code = Code, message = Message, messageAr = MessageAr };
}

/// <summary>
/// The one rule for turning the grade an edit names into the employee's grade: exactly one active grade of the employee's company (or an unattributed legacy
/// grade of the tenant), found by code or name. The employee's grade text and <c>GradeId</c> are always written together, so they never disagree — the range
/// check on a salary package, grade benefits, loan limits, overtime and approval routing all read <c>GradeId</c>.
/// </summary>
public static class EmployeeGradeResolution
{
    public const string Key = "grade";

    /// <summary>A draft has never been activated: its grade edits apply at once. Anyone else's go through approval.</summary>
    public static bool IsDraft(Employee employee) => employee.Status == "Draft" && employee.ActivatedAtUtc is null;

    public static async Task<IReadOnlyList<Grade>> MatchesAsync(ZayraDbContext db, Employee employee, string term, CancellationToken ct)
    {
        var key = term.Trim().ToLower();
        return await db.Grades.AsNoTracking()
            .Where(g => g.TenantId == employee.TenantId && g.IsActive && !g.IsDeleted
                && (g.CompanyId == employee.CompanyId || g.CompanyId == null)
                && (g.Code.ToLower() == key || g.Name.ToLower() == key))
            .Take(2).ToListAsync(ct);
    }

    /// <summary>The grade the edit names, or null when it names none or several.</summary>
    public static async Task<Grade?> FindAsync(ZayraDbContext db, Employee employee, string? term, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(term)) return null;
        var matches = await MatchesAsync(db, employee, term, ct);
        return matches.Count == 1 ? matches[0] : null;
    }

    public static string? TermOf(IReadOnlyDictionary<string, JsonElement> changes) =>
        changes.TryGetValue(Key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Drops from the change set a "grade" that is not an edit, so the save carries on without it: a JSON null (always ignored — the field was left alone), or the
    /// grade the employee already holds echoed back unchanged (a legacy or free-text grade that no master grade matches must not fail the whole save). A grade
    /// that equals the held text but has no grade link yet, and names exactly one grade, IS an edit: it repairs the link. Anything that is not text is refused.
    /// </summary>
    public static async Task DropNonEditAsync(ZayraDbContext db, Employee employee, IDictionary<string, JsonElement> changes, CancellationToken ct)
    {
        if (!changes.TryGetValue(Key, out var value)) return;
        if (value.ValueKind == JsonValueKind.Null) { changes.Remove(Key); return; }
        if (value.ValueKind != JsonValueKind.String)
            throw new GradeNotResolvedException("The grade must be text. No change was applied.", "يجب أن تكون الدرجة نصًا. لم يتم تطبيق أي تغيير.");
        var term = value.GetString()?.Trim() ?? "";
        var held = employee.Grade?.Trim() ?? "";
        if (!string.Equals(term, held, StringComparison.OrdinalIgnoreCase)) return;
        if (term.Length > 0 && employee.GradeId is null && await FindAsync(db, employee, term, ct) is not null) return;
        changes.Remove(Key);
    }

    private static async Task<bool> HasSalaryRecordAsync(ZayraDbContext db, Employee employee, CancellationToken ct) =>
        await db.EmployeeSalaryStructures.AsNoTracking().AnyAsync(s => s.TenantId == employee.TenantId && s.EmployeeId == employee.Id && s.IsActive, ct);

    /// <summary>Refuses, before anything is written, a grade edit that does not name exactly one grade, and the clearing of the grade of anyone who is not a draft
    /// while they have a salary record (with no grade there is no range to judge the pay against). An empty value clears the grade.</summary>
    public static async Task CheckAsync(ZayraDbContext db, Employee employee, IReadOnlyDictionary<string, JsonElement> changes, CancellationToken ct)
    {
        if (!changes.TryGetValue(Key, out var value)) return;
        if (value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.String)
            throw new GradeNotResolvedException("The grade must be text. No change was applied.", "يجب أن تكون الدرجة نصًا. لم يتم تطبيق أي تغيير.");
        var term = value.GetString()?.Trim() ?? "";
        if (term.Length == 0)
        {
            if (!IsDraft(employee) && await HasSalaryRecordAsync(db, employee, ct))
                throw new GradeNotResolvedException(
                    "An employee with a salary record must keep a grade. Choose another grade instead of clearing it. No change was applied.",
                    "يجب أن تبقى للموظف الذي لديه سجل راتب درجة. اختر درجة أخرى بدلًا من إزالتها. لم يتم تطبيق أي تغيير.");
            return;
        }
        var count = (await MatchesAsync(db, employee, term, ct)).Count;
        if (count == 1) return;
        throw count == 0
            ? new GradeNotResolvedException($"\"{term}\" is not an active grade of this employee's company. Pick a grade from the list. No change was applied.",
                $"«{term}» ليست درجة نشطة في شركة هذا الموظف. اختر درجة من القائمة. لم يتم تطبيق أي تغيير.")
            : new GradeNotResolvedException($"\"{term}\" matches more than one grade of this employee's company. Pick the grade by its code. No change was applied.",
                $"«{term}» تطابق أكثر من درجة في شركة هذا الموظف. اختر الدرجة برمزها. لم يتم تطبيق أي تغيير.");
    }

    /// <summary>The audit row for a grade move: the grade before and after (OldValue / NewValue), who made it and who approved it, with the masked employee snapshot
    /// every history row carries. Staged with the change itself.</summary>
    public static void StageHistory(ZayraDbContext db, Employee employee, string? fromGrade, Guid? fromGradeId, Guid? byUserId, Guid? approvedByUserId)
    {
        if (string.Equals(fromGrade?.Trim() ?? "", employee.Grade?.Trim() ?? "", StringComparison.Ordinal) && fromGradeId == employee.GradeId) return;
        db.EmployeeHistories.Add(new EmployeeHistory
        {
            TenantId = employee.TenantId ?? Guid.Empty,
            EmployeeId = employee.Id,
            EventType = "GradeChanged",
            FieldName = Key,
            OldValue = fromGrade ?? "",
            NewValue = employee.Grade ?? "",
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow),
            ApprovedByUserId = approvedByUserId,
            CreatedByUserId = byUserId,
            SnapshotJson = EmployeeSafeSnapshot.Serialize(employee),
        });
    }

    /// <summary>Writes the grade text and <c>GradeId</c> together when an approved change names a grade (an empty value clears both), and stages the audit row with
    /// the approver. The grade held BEFORE the change's text was applied is passed in. Stages only.</summary>
    public static async Task ApplyApprovedAsync(ZayraDbContext db, Employee employee, IReadOnlyDictionary<string, JsonElement> changes, Guid? approverId,
        string? priorGrade, Guid? priorGradeId, CancellationToken ct)
    {
        if (!changes.ContainsKey(Key)) return;
        await CheckAsync(db, employee, changes, ct);
        if (changes[Key].ValueKind == JsonValueKind.Null) return;
        var term = TermOf(changes)?.Trim() ?? "";
        if (term.Length == 0) { employee.Grade = ""; employee.GradeId = null; }
        else
        {
            var grade = (await FindAsync(db, employee, term, ct))!;
            employee.Grade = grade.Code;
            employee.GradeId = grade.Id;
        }
        StageHistory(db, employee, priorGrade, priorGradeId, approverId, approverId);
    }
}
