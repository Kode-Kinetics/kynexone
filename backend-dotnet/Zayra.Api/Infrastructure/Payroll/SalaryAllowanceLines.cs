using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Finance;
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

/// <summary>A line as an approver sees it: with its names, and the amount it comes to; and, so Finance can see what approving it does, the GL
/// driver (and its label) the expense posts to and whether it raises the end-of-service liability (with the reason when it does not).
/// A line whose definition is gone has no driver and no end-of-service answer (null), never a guess.</summary>
public sealed record SalaryAllowanceLineView(
    string Code, string? NameEn, string? NameAr, string Basis, decimal? Rate, decimal Amount,
    string? GlDriverKey = null, string? GlDriverLabel = null, bool? EosbIncluded = null, string? EosbExclusionReason = null);

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
    /// <summary>The most one line, or the whole monthly pay (the salary components plus every allowance line), may come to: payslips.OtherAllowances and
    /// payslips.GrossSalary are numeric(12,2), and employee salary is too. The salary-package limits elsewhere are the same figure.</summary>
    public const decimal ColumnLimit = 9_999_999_999.99m;

    private static string Money(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>The refusal for a whole monthly pay (<paramref name="slotsGross"/> plus the lines) that no payslip column could hold; null when it fits.</summary>
    public static AllowanceProblem? CheckWholePay(decimal slotsGross, decimal linesTotal) =>
        slotsGross + linesTotal > ColumnLimit
            ? Bad($"The whole monthly pay (the salary components plus the allowance lines) comes to {Money(slotsGross + linesTotal)}, which is more than the {Money(ColumnLimit)} a payslip can hold. Lower the amounts.")
            : null;

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
        ZayraDbContext db, Guid tenantId, IReadOnlyList<SalaryAllowanceLine>? proposed, decimal basic, CancellationToken ct, decimal slotsGross = 0m)
    {
        var resolved = new List<ResolvedAllowanceLine>();
        if (proposed is null || proposed.Count == 0) return (resolved, null);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in proposed)
        {
            // A null entry ("allowances": [null]) is a malformed request, not a server fault.
            if (line is null) return (resolved, Bad("Every allowance line needs the allowance it is for."));
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
                if (decimal.Round(amount, 2) != amount)
                    return (resolved, Bad($"{code}: the monthly amount must have at most two decimal places."));
                if (amount > ColumnLimit)
                    return (resolved, Bad($"{code}: the monthly amount cannot be more than {Money(ColumnLimit)}."));
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

        // The payslip columns hold at most the limit, so the whole pay is checked here, where every path that stages or applies lines passes.
        if (CheckWholePay(slotsGross, resolved.Sum(l => l.Amount)) is { } tooMuch) return (resolved, tooMuch);

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
    /// re-worked on the new basic); a list (even empty) is exactly those lines, validated. <paramref name="slotsGross"/> is the salary
    /// components' total (everything but the fixed deduction), so the whole pay is held to the payslip column limit.
    /// </summary>
    public static async Task<AllowanceResolution> ResolveForProposalAsync(
        ZayraDbContext db, Guid tenantId, int employeeId, DateOnly effectiveDate, decimal basic,
        IReadOnlyList<SalaryAllowanceLine>? stated, CancellationToken ct, decimal slotsGross = 0m)
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
        var (resolved, problem) = await ValidateAsync(db, tenantId, lines, basic, ct, slotsGross);
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

    /// <summary>
    /// Whether a staged salary change's lines name <paramref name="code"/>, for the guard on removing an allowance that a pending change
    /// proposes. <c>null</c> = the JSON cannot be read (or has an entry that is not a line): the guard FAILS CLOSED on that and refuses the
    /// remove, because reading it as "not named" would let an allowance that a waiting change still names be removed.
    /// </summary>
    public static bool? Names(string? json, string code)
    {
        List<SalaryAllowanceLine>? lines;
        try { lines = Deserialize(json); }
        catch (JsonException) { return null; }
        if (lines is null) return false;
        var named = false;
        foreach (var line in lines)
        {
            if (line is null) return null;
            if (string.Equals((line.Code ?? string.Empty).Trim(), code, StringComparison.OrdinalIgnoreCase)) named = true;
        }
        return named;
    }

    /// <summary>
    /// Whether an Approvals employee change (<see cref="EmployeeChangeRequest.ProposedChangesJson"/>) proposes a salary package whose
    /// <c>allowances</c> name <paramref name="code"/>. Same contract as <see cref="Names"/>: <c>null</c> = cannot be read, fail closed.
    /// A change with no salary package, or a package that does not state allowances, names nothing.
    /// </summary>
    public static bool? ChangeNames(string? proposedChangesJson, string code)
    {
        if (string.IsNullOrWhiteSpace(proposedChangesJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(proposedChangesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var named = false;
            var unreadable = false;
            foreach (var field in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(field.Name, EmployeeSalaryBreakdownKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (field.Value.ValueKind != JsonValueKind.Object) continue;     // a non-package value states no lines (and would never apply)
                foreach (var inner in field.Value.EnumerateObject())
                {
                    if (!string.Equals(inner.Name, "allowances", StringComparison.OrdinalIgnoreCase)) continue;
                    if (inner.Value.ValueKind == JsonValueKind.Null) continue;
                    if (inner.Value.ValueKind != JsonValueKind.Array) { unreadable = true; continue; }
                    foreach (var element in inner.Value.EnumerateArray())
                    {
                        if (element.ValueKind != JsonValueKind.Object) { unreadable = true; continue; }
                        foreach (var property in element.EnumerateObject())
                            if (string.Equals(property.Name, "code", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String
                                && string.Equals(property.Value.GetString()?.Trim(), code, StringComparison.OrdinalIgnoreCase))
                                named = true;
                    }
                }
            }
            return named ? true : unreadable ? null : false;
        }
        catch (JsonException) { return null; }
    }

    private const string EmployeeSalaryBreakdownKey = "salaryBreakdown";

    // ── The tenant lock a stage shares with a remove ────────────────────────────────────────────

    /// <summary>
    /// Takes the tenant lock <c>RemoveBenefitAsync</c> (and every other Benefits by grade write) holds, so staging a proposal that NAMES
    /// an allowance and removing that allowance cannot interleave: whichever takes the lock second sees the other's committed result. It
    /// is a transaction-scoped advisory lock, so the caller must already be inside a transaction (the Postgres guard refuses otherwise)
    /// and must call this BEFORE it validates the lines against the live definitions. Only a proposal that states lines needs it; one
    /// that states none (or only carries lines already on a salary record, which the remove guard already refuses on) takes no lock.
    /// </summary>
    public static Task LockForStagingAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct) =>
        FinanceDecisionSerializer.AcquireAsync(db, EntitlementMatrixService.LockScope, tenantId, tenantId, ct);

    /// <summary>True when a proposal's lines name at least one allowance (so staging it must take <see cref="LockForStagingAsync"/>).</summary>
    public static bool NamesAny(IReadOnlyList<SalaryAllowanceLine>? stated) => stated is { Count: > 0 };

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
        var definitions = await DefinitionFactsAsync(db, tenantId, codes, ct);

        SalaryAllowanceLineView View(string code, string basis, decimal? rate, decimal amount) =>
            definitions.TryGetValue(code, out var d)
                ? new(code, d.NameEn, d.NameAr, basis, rate, amount, d.GlDriverKey, d.GlDriverLabel, d.EosbIncluded, d.EosbExclusionReason)
                : new(code, null, null, basis, rate, amount);

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

    /// <summary>What the approval screen and the read screens know about each allowance code: its names, the GL driver it posts to (with that driver's
    /// label) and its end-of-service answer. Removed definitions are included (a line on an old record still has to be shown); a code with no definition is absent.</summary>
    private sealed record DefinitionFacts(string NameEn, string NameAr, string? GlDriverKey, string? GlDriverLabel, bool EosbIncluded, string? EosbExclusionReason);

    private static async Task<Dictionary<string, DefinitionFacts>> DefinitionFactsAsync(ZayraDbContext db, Guid tenantId, IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        var result = new Dictionary<string, DefinitionFacts>(StringComparer.Ordinal);
        if (codes.Count == 0) return result;
        var rows = (await ScopedBypass.TenantWide(db.PayComponents, tenantId, "Screens that show a cash allowance line name it, say where it posts and whether it counts in end of service, removed definitions included.")
                .AsNoTracking().Where(c => c.CompanyId == null && c.ComponentType == PayComponentTypes.Earning && codes.Contains(c.Code))
                .Select(c => new { c.Code, c.NameEn, c.NameAr, c.GlDriverKey, c.EosbIncluded, c.EosbExclusionReason }).ToListAsync(ct))
            .GroupBy(c => c.Code, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var driverKeys = rows.Where(r => !string.IsNullOrWhiteSpace(r.GlDriverKey)).Select(r => r.GlDriverKey!).Distinct().ToList();
        var labels = driverKeys.Count == 0 ? new Dictionary<string, string>(StringComparer.Ordinal)
            : (await ScopedBypass.TenantWide(db.GlDrivers, tenantId, "The label of the tenant-wide GL driver a cash allowance posts to, shown beside its key.")
                    .AsNoTracking().Where(g => g.CompanyId == null && driverKeys.Contains(g.Key)).Select(g => new { g.Key, g.Label }).ToListAsync(ct))
                .GroupBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Label, StringComparer.Ordinal);
        foreach (var r in rows)
        {
            // The tenant's own driver row, else the built-in earning catalogue; null when neither knows the key.
            string? label = null;
            if (!string.IsNullOrWhiteSpace(r.GlDriverKey))
                label = labels.TryGetValue(r.GlDriverKey!, out var own) && !string.IsNullOrWhiteSpace(own) ? own
                    : PayrollGlCatalog.Drivers.FirstOrDefault(d => d.Key == r.GlDriverKey)?.Label;
            result[r.Code] = new DefinitionFacts(r.NameEn, r.NameAr, string.IsNullOrWhiteSpace(r.GlDriverKey) ? null : r.GlDriverKey, label, r.EosbIncluded, r.EosbExclusionReason);
        }
        return result;
    }

    /// <summary>The same lines with their English and Arabic names filled in (for a screen that may not read the catalogue); a line whose definition is gone keeps no name.</summary>
    public static async Task<List<SalaryAllowanceLine>> NamedAsync(ZayraDbContext db, Guid tenantId, IEnumerable<SalaryAllowanceLine> lines, CancellationToken ct)
    {
        var list = lines.Where(l => l is not null).ToList();
        var codes = list.Select(l => (l.Code ?? string.Empty).Trim().ToUpperInvariant()).Where(c => c.Length > 0).Distinct().ToList();
        var facts = await DefinitionFactsAsync(db, tenantId, codes, ct);
        return list.Select(l => facts.TryGetValue((l.Code ?? string.Empty).Trim().ToUpperInvariant(), out var d) ? l with { NameEn = d.NameEn, NameAr = d.NameAr } : l).ToList();
    }

    /// <summary>Each salary record's allowance lines (named), by record id; a record with none is absent.</summary>
    public static async Task<Dictionary<Guid, List<SalaryAllowanceLine>>> NamedByRecordAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid> salaryIds, CancellationToken ct)
    {
        var rows = await LoadAsync(db, tenantId, salaryIds, ct);
        var all = rows.SelectMany(g => g).ToList();
        var facts = await DefinitionFactsAsync(db, tenantId, all.Select(r => r.Code).Distinct().ToList(), ct);
        return rows.ToDictionary(g => g.Key, g => g.OrderBy(r => r.Code, StringComparer.Ordinal)
            .Select(r => facts.TryGetValue(r.Code, out var d) ? new SalaryAllowanceLine(r.Code, r.Basis, r.Rate, r.Amount, d.NameEn, d.NameAr)
                                                             : new SalaryAllowanceLine(r.Code, r.Basis, r.Rate, r.Amount)).ToList());
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
