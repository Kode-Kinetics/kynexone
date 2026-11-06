using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>A cash allowance line after validation: the canonical code and basis, and the monthly amount.</summary>
public sealed record ResolvedAllowanceLine(string Code, string Basis, decimal? Rate, decimal Amount);

/// <summary>Why a set of allowance lines was refused: a stable code, a plain-language message, and the HTTP status a caller sends.</summary>
public sealed record AllowanceProblem(string Code, string Message, int Status);

/// <summary>What a salary proposal's allowance lines come to: the lines (null when none could be resolved), whether they were carried
/// forward from the current salary record rather than stated, and the refusal if any.</summary>
public sealed record AllowanceResolution(IReadOnlyList<ResolvedAllowanceLine> Lines, bool Carried, AllowanceProblem? Problem)
{
    public decimal Total => Lines.Sum(l => l.Amount);
}

/// <summary>A line as an approver sees it: with its names, and the amount it comes to.</summary>
public sealed record SalaryAllowanceLineView(string Code, string? NameEn, string? NameAr, string Basis, decimal? Rate, decimal Amount);

/// <summary>What the approval screen shows of a salary change's allowances: the lines on the salary record in force now, the lines the
/// change would leave, and whether those were carried forward because the proposal did not mention allowances.</summary>
public sealed record SalaryAllowanceView(IReadOnlyList<SalaryAllowanceLineView> Current, IReadOnlyList<SalaryAllowanceLineView> Proposed, bool CarriedForward)
{
    public static readonly SalaryAllowanceView None = new([], [], false);
}

/// <summary>A cash allowance line that counts in the end-of-service wage.</summary>
public sealed record EosbCashAllowance(string Code, decimal Amount);

/// <summary>
/// The ONE place the monthly cash allowances (<c>ALW_*</c>) a salary record carries are validated, carried forward, serialised for
/// staging and read back. A salary change is HR-proposed and Finance-approved, and every path that creates a new salary record goes
/// through here, so an allowance is never dropped by a change that did not mention it.
///
/// <para><b>Semantics of a proposal's lines.</b> <c>null</c> = the proposal does not mention allowances: the new record carries the
/// salary record in force's lines forward, a percentage re-worked on the new basic. An empty list = remove them all. A list = exactly
/// those lines. <c>allowances_json</c> keeps the same three states (null / "[]" / lines), and a null is carried at APPROVAL time from
/// whatever is in force then, so an allowance added between proposal and approval is not lost either.</para>
/// </summary>
public static class SalaryAllowanceLines
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // numeric(14,2)
    private const decimal MaxAmount = 999_999_999_999.99m;

    /// <summary>A percentage's amount: rate × basic, to the halala, half away from zero — as the grade grid rounds it.</summary>
    public static decimal PercentAmount(decimal rate, decimal basic) => Math.Round(rate * basic, 2, MidpointRounding.AwayFromZero);

    public static AllowanceProblem Bad(string message) => new("salary_allowance_invalid", message, 400);
    public static AllowanceProblem Unknown(string message) => new("salary_allowance_unknown", message, 422);

    // ── Validation ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks a proposal's lines against the tenant's live definitions and works out each amount on <paramref name="basic"/>.
    /// A code must be a live tenant-wide <c>ALW_*</c> cash allowance (Earning, GradePackage, active, not removed); no code twice; an
    /// Amount line needs an amount above zero and no rate; a PercentOfBasic line a rate above 0 and up to 1 (four decimals) that comes to
    /// something on this basic.
    /// </summary>
    public static async Task<(List<ResolvedAllowanceLine> Lines, AllowanceProblem? Problem)> ValidateAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyList<SalaryAllowanceLine>? proposed, decimal basic, CancellationToken ct)
    {
        var resolved = new List<ResolvedAllowanceLine>();
        if (proposed is null || proposed.Count == 0) return (resolved, null);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in proposed)
        {
            var code = (line.Code ?? string.Empty).Trim().ToUpperInvariant();
            if (code.Length == 0) return (resolved, Bad("Every allowance line needs the allowance it is for."));
            if (!EntitlementComponentRules.IsAllowance(code))
                return (resolved, Bad($"{code} is not a monthly cash allowance. Only allowances added in Benefits by grade (kind: monthly cash allowance) can be put on a salary."));
            if (!seen.Add(code)) return (resolved, Bad($"{code} is on this salary twice. Keep one line for it."));

            var basis = (line.Basis ?? string.Empty).Trim();
            if (string.Equals(basis, AllowanceBases.Amount, StringComparison.OrdinalIgnoreCase)) basis = AllowanceBases.Amount;
            else if (string.Equals(basis, AllowanceBases.PercentOfBasic, StringComparison.OrdinalIgnoreCase)) basis = AllowanceBases.PercentOfBasic;
            else return (resolved, Bad($"{code}: choose whether it is a fixed amount or a percentage of basic salary."));

            if (basis == AllowanceBases.Amount)
            {
                if (line.Rate is not null) return (resolved, Bad($"{code}: a fixed amount has no percentage. Remove the rate, or make it a percentage of basic."));
                if (line.Amount is not decimal amount || amount <= 0m)
                    return (resolved, Bad($"{code}: the monthly amount must be greater than zero. To stop paying it, remove the line."));
                if (amount > MaxAmount || decimal.Round(amount, 2) != amount)
                    return (resolved, Bad($"{code}: the monthly amount must have at most two decimal places and be a realistic figure."));
                resolved.Add(new ResolvedAllowanceLine(code, basis, null, amount));
            }
            else
            {
                if (line.Rate is not decimal rate || rate <= 0m || rate > 1m || decimal.Round(rate, 4) != rate)
                    return (resolved, Bad($"{code}: a percentage of basic must be above 0% and at most 100% (give it as a fraction, 0.10 for 10%)."));
                var amount = PercentAmount(rate, basic);
                if (amount <= 0m)
                    return (resolved, Bad($"{code}: {rate:P2} of a basic of {basic:N2} comes to nothing. Use a fixed amount instead."));
                resolved.Add(new ResolvedAllowanceLine(code, basis, rate, amount));
            }
        }

        var definitions = await LiveDefinitionCodesAsync(db, tenantId, seen.ToList(), ct);
        foreach (var line in resolved.Where(l => !definitions.Contains(l.Code)))
            return (resolved, Unknown($"{line.Code} is not a monthly cash allowance that is in use: it was removed or switched off in Benefits by grade. Take the line off this salary, or restore the allowance first."));

        resolved.Sort((a, b) => string.CompareOrdinal(a.Code, b.Code));
        return (resolved, null);
    }

    /// <summary>The codes among <paramref name="codes"/> that are a live tenant-wide cash allowance definition.</summary>
    public static async Task<HashSet<string>> LiveDefinitionCodesAsync(ZayraDbContext db, Guid tenantId, IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        if (codes.Count == 0) return [];
        var found = await ScopedBypass.TenantWide(db.PayComponents, tenantId,
                "A salary proposal's allowance lines name tenant-wide cash allowance definitions; a company row never defines one.")
            .AsNoTracking()
            .Where(c => c.CompanyId == null && c.IsActive && !c.IsDeleted && c.ComponentType == PayComponentTypes.Earning
                     && c.CalcMethod == PayComponentCalcMethods.GradePackage && codes.Contains(c.Code))
            .Select(c => c.Code)
            .ToListAsync(ct);
        return found.ToHashSet(StringComparer.Ordinal);
    }

    // ── Resolution of a proposal (explicit, or carried forward) ─────────────────────────────────

    /// <summary>
    /// The lines a salary proposal comes to for <paramref name="employeeId"/> from <paramref name="effectiveDate"/> on a proposed
    /// <paramref name="basic"/>. <paramref name="stated"/> null carries the salary record in force's lines forward (a percentage
    /// re-worked on the new basic); a list (even empty) is exactly those lines, validated.
    /// </summary>
    public static async Task<AllowanceResolution> ResolveForProposalAsync(
        ZayraDbContext db, Guid tenantId, int employeeId, DateOnly effectiveDate, decimal basic,
        IReadOnlyList<SalaryAllowanceLine>? stated, CancellationToken ct)
    {
        var carried = stated is null;
        IReadOnlyList<SalaryAllowanceLine> lines;
        if (stated is not null) lines = stated;
        else
        {
            var current = await InForceAsync(db, tenantId, employeeId, effectiveDate, ct);
            // Carried as stated, so a percentage is worked out again on the new basic and a fixed amount is unchanged.
            lines = current.Lines.Select(l => new SalaryAllowanceLine(l.Code, l.Basis, l.Rate, l.Basis == AllowanceBases.Amount ? l.Amount : null)).ToList();
        }
        if (lines.Count == 0) return new AllowanceResolution([], carried, null);
        var (resolved, problem) = await ValidateAsync(db, tenantId, lines, basic, ct);
        if (problem is not null && carried)
            problem = problem with { Message = "This employee's current salary carries an allowance that cannot be carried over. " + problem.Message };
        return new AllowanceResolution(resolved, carried, problem);
    }

    /// <summary>The salary record in force for the employee on <paramref name="asOf"/> and its allowance lines.</summary>
    public static async Task<(EmployeeSalaryStructure? Record, List<EmployeeSalaryAllowance> Lines)> InForceAsync(
        ZayraDbContext db, Guid tenantId, int employeeId, DateOnly asOf, CancellationToken ct)
    {
        var record = await EntitlementResolver.SalaryInForce(db, tenantId, employeeId, asOf, ct);
        if (record is null) return (null, []);
        var lines = await db.EmployeeSalaryAllowances.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SalaryStructureId == record.Id).ToListAsync(ct);
        return (record, lines.OrderBy(l => l.Code, StringComparer.Ordinal).ToList());
    }

    /// <summary>The new salary record's allowance rows for a resolved proposal.</summary>
    public static List<EmployeeSalaryAllowance> RowsFor(Guid tenantId, Guid salaryStructureId, IEnumerable<ResolvedAllowanceLine> lines) =>
        lines.Select(l => new EmployeeSalaryAllowance
        {
            TenantId = tenantId, SalaryStructureId = salaryStructureId, Code = l.Code, Basis = l.Basis, Rate = l.Rate, Amount = l.Amount,
        }).ToList();

    // ── Staging (allowances_json) ───────────────────────────────────────────────────────────────

    /// <summary>The JSON a staged change keeps: null when the proposal did not state any (carry), "[]" for none, otherwise the lines.</summary>
    public static string? Serialize(IReadOnlyList<ResolvedAllowanceLine>? lines) =>
        lines is null ? null : JsonSerializer.Serialize(lines.Select(l => new SalaryAllowanceLine(l.Code, l.Basis, l.Rate, l.Amount)), Json);

    /// <summary>The lines in a staged change's JSON; null for a change that stated none (carry forward). Unreadable JSON is never read as "none"
    /// (that would drop an allowance): it throws a <see cref="JsonException"/>, and a staged change is only ever written by <see cref="Serialize"/>.</summary>
    public static List<SalaryAllowanceLine>? Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<List<SalaryAllowanceLine>>(json, Json) ?? [];

    /// <summary>True when two staged states are the same proposal (jsonb does not keep the text it was given, so the lines are compared, not the strings).</summary>
    public static bool SameStaged(string? a, string? b)
    {
        var x = Deserialize(a); var y = Deserialize(b);
        if (x is null || y is null) return x is null && y is null;
        if (x.Count != y.Count) return false;
        var xs = x.OrderBy(l => l.Code, StringComparer.Ordinal).ToList(); var ys = y.OrderBy(l => l.Code, StringComparer.Ordinal).ToList();
        return xs.Zip(ys).All(p => p.First.Code == p.Second.Code && p.First.Basis == p.Second.Basis && p.First.Rate == p.Second.Rate && p.First.Amount == p.Second.Amount);
    }

    /// <summary>Whether a staged change names <paramref name="code"/> (for the guard on removing an allowance that a pending change proposes).</summary>
    public static bool Names(string? json, string code)
    {
        try { return Deserialize(json)?.Any(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) == true; }
        catch (JsonException) { return false; }
    }

    // ── Reads for display, payroll helpers and end of service ───────────────────────────────────

    /// <summary>The approval screen's view of each change's allowances (one set of queries for the whole list).</summary>
    public static async Task<Dictionary<Guid, SalaryAllowanceView>> ViewsAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<SalaryChangeRequest> changes, CancellationToken ct)
    {
        var views = new Dictionary<Guid, SalaryAllowanceView>();
        if (changes.Count == 0) return views;
        var employeeIds = changes.Select(c => c.EmployeeId).Distinct().ToList();
        var records = await db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive && employeeIds.Contains(x.EmployeeId))
            .Select(x => new { x.Id, x.EmployeeId, x.EffectiveDate, x.CreatedAtUtc })
            .ToListAsync(ct);
        var appliedIds = changes.Where(c => c.AppliedAssignmentId.HasValue).Select(c => c.AppliedAssignmentId!.Value).ToList();
        var recordIds = records.Select(r => r.Id).Concat(appliedIds).Distinct().ToList();
        var rows = recordIds.Count == 0 ? new List<EmployeeSalaryAllowance>()
            : await db.EmployeeSalaryAllowances.AsNoTracking().Where(x => x.TenantId == tenantId && recordIds.Contains(x.SalaryStructureId)).ToListAsync(ct);
        var byRecord = rows.ToLookup(r => r.SalaryStructureId);

        var codes = rows.Select(r => r.Code).ToHashSet(StringComparer.Ordinal);
        var staged = new Dictionary<Guid, List<SalaryAllowanceLine>?>();
        foreach (var c in changes)
        {
            List<SalaryAllowanceLine>? lines = null;
            try { lines = Deserialize(c.AllowancesJson); } catch (JsonException) { }
            staged[c.Id] = lines;
            if (lines is not null) foreach (var l in lines) if (!string.IsNullOrWhiteSpace(l.Code)) codes.Add(l.Code.Trim().ToUpperInvariant());
        }
        var names = codes.Count == 0 ? new Dictionary<string, (string En, string Ar)>(StringComparer.Ordinal)
            : (await ScopedBypass.TenantWide(db.PayComponents, tenantId, "The approval screen names each allowance line, removed definitions included.")
                .AsNoTracking().Where(c => c.CompanyId == null && c.ComponentType == PayComponentTypes.Earning && codes.Contains(c.Code))
                .Select(c => new { c.Code, c.NameEn, c.NameAr }).ToListAsync(ct))
                .GroupBy(c => c.Code, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (En: g.First().NameEn, Ar: g.First().NameAr), StringComparer.Ordinal);

        SalaryAllowanceLineView View(string code, string basis, decimal? rate, decimal amount) =>
            names.TryGetValue(code, out var n) ? new(code, n.En, n.Ar, basis, rate, amount) : new(code, null, null, basis, rate, amount);

        foreach (var c in changes)
        {
            // The record in force for the change's date; for a decided change, the one the change itself wrote is not "current".
            var current = records.Where(r => r.EmployeeId == c.EmployeeId && r.EffectiveDate <= c.EffectiveDate && r.Id != c.AppliedAssignmentId)
                .OrderByDescending(r => r.EffectiveDate).ThenByDescending(r => r.CreatedAtUtc).FirstOrDefault();
            var currentRows = current is null ? [] : byRecord[current.Id].OrderBy(r => r.Code, StringComparer.Ordinal).ToList();
            var currentViews = currentRows.Select(r => View(r.Code, r.Basis, r.Rate, r.Amount)).ToList();

            List<SalaryAllowanceLineView> proposed;
            var carried = false;
            if (c.Status == SalaryChangeRequest.Approved && c.AppliedAssignmentId is Guid applied)
                proposed = byRecord[applied].OrderBy(r => r.Code, StringComparer.Ordinal).Select(r => View(r.Code, r.Basis, r.Rate, r.Amount)).ToList();
            else if (staged[c.Id] is { } stated)
                proposed = stated.Select(l => View((l.Code ?? string.Empty).Trim().ToUpperInvariant(), l.Basis ?? string.Empty, l.Rate, l.Amount ?? 0m))
                    .OrderBy(l => l.Code, StringComparer.Ordinal).ToList();
            else if (c.Status == SalaryChangeRequest.Pending)
            {
                // Not mentioned: what approval would carry over, a percentage re-worked on the proposed basic.
                carried = currentRows.Count > 0;
                proposed = currentRows.Select(r => View(r.Code, r.Basis, r.Rate,
                    r.Basis == AllowanceBases.PercentOfBasic && r.Rate is decimal rate ? PercentAmount(rate, c.BasicSalary) : r.Amount)).ToList();
            }
            else proposed = [];
            views[c.Id] = new SalaryAllowanceView(currentViews, proposed, carried);
        }
        return views;
    }

    /// <summary>All allowance rows of the given salary records, by record.</summary>
    public static async Task<ILookup<Guid, EmployeeSalaryAllowance>> LoadAsync(ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid> salaryIds, CancellationToken ct)
    {
        if (salaryIds.Count == 0) return Enumerable.Empty<EmployeeSalaryAllowance>().ToLookup(x => x.SalaryStructureId);
        return (await db.EmployeeSalaryAllowances.AsNoTracking()
            .Where(x => x.TenantId == tenantId && salaryIds.Contains(x.SalaryStructureId)).ToListAsync(ct)).ToLookup(x => x.SalaryStructureId);
    }

    /// <summary>The lines of each salary record whose definition counts in the end-of-service wage, by record. A line whose definition
    /// is gone is left out: it cannot be known to count.</summary>
    public static async Task<Dictionary<Guid, List<EosbCashAllowance>>> EosbIncludedAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid> salaryIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, List<EosbCashAllowance>>();
        var rows = await LoadAsync(db, tenantId, salaryIds, ct);
        var codes = rows.SelectMany(g => g).Select(r => r.Code).Distinct().ToList();
        if (codes.Count == 0) return result;
        var counted = (await ScopedBypass.TenantWide(db.PayComponents, tenantId,
                "End of service reads which cash allowance definitions count in the last wage; the definition is tenant-wide.")
            .AsNoTracking()
            .Where(c => c.CompanyId == null && !c.IsDeleted && c.EosbIncluded && c.ComponentType == PayComponentTypes.Earning
                     && c.CalcMethod == PayComponentCalcMethods.GradePackage && codes.Contains(c.Code))
            .Select(c => c.Code).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var group in rows)
        {
            var lines = group.Where(r => counted.Contains(r.Code)).OrderBy(r => r.Code, StringComparer.Ordinal)
                .Select(r => new EosbCashAllowance(r.Code, r.Amount)).ToList();
            if (lines.Count > 0) result[group.Key] = lines;
        }
        return result;
    }
}
