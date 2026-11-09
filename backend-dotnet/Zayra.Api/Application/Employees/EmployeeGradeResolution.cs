using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees;

/// <summary>A grade edit that names no single grade. Carries both languages so the screen can show the right one.</summary>
public sealed class GradeNotResolvedException(string message, string messageAr) : InvalidOperationException(message)
{
    public const string Code = "grade_not_resolved";
    public string MessageAr { get; } = messageAr;
    public object ToPayload() => new { error = Code, code = Code, message = Message, messageAr = MessageAr };
}

/// <summary>
/// The one rule for turning the grade an edit names into the employee's grade: exactly one active grade of the employee's company (or an unattributed legacy
/// grade of the tenant), found by code or name. The employee's grade text and <c>GradeId</c> are always written together, so they never disagree — the range
/// check on a salary package, grade benefits and every grade-based report read <c>GradeId</c>.
/// </summary>
public static class EmployeeGradeResolution
{
    public static async Task<IReadOnlyList<Grade>> MatchesAsync(ZayraDbContext db, Employee employee, string term, CancellationToken ct)
    {
        var key = term.Trim().ToLower();
        return await db.Grades.AsNoTracking()
            .Where(g => g.TenantId == employee.TenantId && g.IsActive && !g.IsDeleted
                && (g.CompanyId == employee.CompanyId || g.CompanyId == null)
                && (g.Code.ToLower() == key || g.Name.ToLower() == key))
            .Take(2).ToListAsync(ct);
    }

    /// <summary>The grade the edit names, or null when it names none or several (or the edit does not touch the grade).</summary>
    public static async Task<Grade?> FindAsync(ZayraDbContext db, Employee employee, string? term, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(term)) return null;
        var matches = await MatchesAsync(db, employee, term, ct);
        return matches.Count == 1 ? matches[0] : null;
    }

    public static string? TermOf(IReadOnlyDictionary<string, JsonElement> changes) =>
        changes.TryGetValue("grade", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Refuses, before anything is written, a grade edit that does not name exactly one grade. An empty value clears the grade.</summary>
    public static async Task CheckAsync(ZayraDbContext db, Employee employee, IReadOnlyDictionary<string, JsonElement> changes, CancellationToken ct)
    {
        if (!changes.TryGetValue("grade", out var value)) return;
        var term = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : null;
        if (term is null) throw new GradeNotResolvedException("The grade must be text. No change was applied.", "يجب أن تكون الدرجة نصًا. لم يتم تطبيق أي تغيير.");
        if (term.Length == 0) return;
        var count = (await MatchesAsync(db, employee, term, ct)).Count;
        if (count == 1) return;
        throw count == 0
            ? new GradeNotResolvedException($"\"{term}\" is not an active grade of this employee's company. Pick a grade from the list. No change was applied.",
                $"«{term}» ليست درجة نشطة في شركة هذا الموظف. اختر درجة من القائمة. لم يتم تطبيق أي تغيير.")
            : new GradeNotResolvedException($"\"{term}\" matches more than one grade of this employee's company. Pick the grade by its code. No change was applied.",
                $"«{term}» تطابق أكثر من درجة في شركة هذا الموظف. اختر الدرجة برمزها. لم يتم تطبيق أي تغيير.");
    }

    /// <summary>Writes the grade text and <c>GradeId</c> together when the change names a grade (an empty value clears both). Stages only.</summary>
    public static async Task ApplyAsync(ZayraDbContext db, Employee employee, IReadOnlyDictionary<string, JsonElement> changes, CancellationToken ct)
    {
        if (!changes.ContainsKey("grade")) return;
        await CheckAsync(db, employee, changes, ct);
        var term = TermOf(changes)?.Trim() ?? "";
        if (term.Length == 0) { employee.Grade = ""; employee.GradeId = null; return; }
        var grade = (await FindAsync(db, employee, term, ct))!;
        employee.Grade = grade.Code;
        employee.GradeId = grade.Id;
    }
}
