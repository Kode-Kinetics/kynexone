using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>
/// Bulk punches: the CSV import, its dry run and mass entry for a selection of employees. All three go through ONE judging routine
/// (<see cref="EvaluateBulkAsync"/>) and ONE idempotent save, so a file cannot be judged one way in the preview and another for real,
/// and mass entry can never write something the import would have refused.
///
/// <para>Every guard of the single-punch paths holds per row: no punch for the caller's own record (I1); only employees inside the
/// caller's data scope, an out-of-scope row reading exactly like an unknown code (I2); active employees only (I3); the server's
/// verification labels are reserved (I4); nothing in a payroll-locked period (I5); nothing more than five minutes ahead of now (I6).</para>
/// </summary>
public partial class AttendanceService
{
    /// <summary>How far ahead of the tenant's now a bulk punch may be.</summary>
    internal const int BulkMaxFutureMinutes = 5;
    public const int BulkMaxEmployees = 500;
    public const int BulkMaxDays = 31;
    public const int BulkMaxReasonLength = 500;
    /// <summary>The most error rows a response carries (the counts are always complete).</summary>
    internal const int BulkMaxReportedErrors = 500;
    public const string CsvImportSource = "CSV import";
    public const string BulkEntrySource = "Manual (bulk)";

    private const string BulkNotFoundMessage =
        "No active employee with this code is in your scope. Check the code, and that the employee is active and one you manage.";
    private const string BulkSelfMessage =
        "You cannot record attendance for your own employee record. Ask another HR user to enter it.";

    // ── public surface ───────────────────────────────────────────────────────────────────────────────────────

    public Task<AttendanceImportResult> PreviewImportAsync(Guid tenantId, ImportAttendanceRequest request, AttendanceBulkAuthority authority,
        RequestContext context, CancellationToken ct) =>
        RunImportAsync(tenantId, request, authority, context, commit: false, ct);

    public Task<AttendanceImportResult> ImportCsvAsync(Guid tenantId, ImportAttendanceRequest request, AttendanceBulkAuthority authority,
        RequestContext context, CancellationToken ct) =>
        RunImportAsync(tenantId, request, authority, context, commit: true, ct);

    private async Task<AttendanceImportResult> RunImportAsync(Guid tenantId, ImportAttendanceRequest request, AttendanceBulkAuthority authority,
        RequestContext context, bool commit, CancellationToken ct)
    {
        var tz = await ResolveTenantTimeZoneAsync(tenantId, ct);
        var file = AttendancePunchCsv.Parse(request.CsvContent, tz);
        var fileName = string.IsNullOrWhiteSpace(request.FileName) ? "attendance.csv" : request.FileName.Trim();
        var batchId = Guid.NewGuid();

        var units = file.Rows.Select(row =>
        {
            var unit = new BulkUnit { RowNumber = row.RowNumber, RawRow = row.RawRow, EmployeeCode = row.EmployeeCode, ParseError = row.Error, ParseColumn = row.Column };
            if (row.Error is null)
                unit.Punches.Add(new BulkPunch
                {
                    Direction = row.Direction!, Utc = row.PunchUtc!.Value, Location = row.Location,
                    Method = string.IsNullOrWhiteSpace(row.Method) ? "CSV" : row.Method, Source = CsvImportSource,
                    PayloadJson = JsonSerializer.Serialize(row.Cells),
                });
            return unit;
        }).ToList();

        return await InBulkTransactionAsync(tenantId, commit, async () =>
        {
            await EvaluateBulkAsync(tenantId, units, authority, tz, ct);
            if (commit)
            {
                await StoreBulkPunchesAsync(tenantId, units, batchId.ToString(), context, ct);
                var failedUnits = units.Where(u => u.Error is not null).ToList();
                var (created, duplicates) = Tally(units);
                _db.AttendanceImportBatches.Add(new AttendanceImportBatch
                {
                    Id = batchId, TenantId = tenantId, FileName = fileName, Source = CsvImportSource, CreatedBy = context.UserId,
                    TotalRows = units.Count, ImportedRows = created, FailedRows = failedUnits.Count,
                    Status = failedUnits.Count > 0 ? "CompletedWithErrors" : "Completed",
                });
                foreach (var u in failedUnits)
                    _db.AttendanceImportErrors.Add(new AttendanceImportError
                    {
                        TenantId = tenantId, ImportBatchId = batchId, RowNumber = u.RowNumber, RawRow = u.RawRow, ErrorMessage = u.Error!,
                    });
                await Audit(tenantId, context, "attendance.import.completed", "AttendanceImportBatch", batchId.ToString(),
                    JsonSerializer.Serialize(new { fileName, total = units.Count, imported = created, duplicates, failed = failedUnits.Count, timeZone = tz.Id }), ct);
                await _db.SaveChangesAsync(ct);
            }

            var failed = units.Where(u => u.Error is not null).ToList();
            var (valid, duplicate) = Tally(units);
            return new AttendanceImportResult(
                commit ? batchId : null,
                commit ? (failed.Count > 0 ? "CompletedWithErrors" : "Completed") : null,
                fileName, units.Count, valid, commit ? valid : 0, duplicate, failed.Count, tz.Id,
                failed.Take(BulkMaxReportedErrors).Select(u => new AttendanceImportRowError(u.RowNumber, u.ErrorColumn, u.Error!, Truncate(u.RawRow, 500))).ToList());
        }, ct);
    }

    public async Task<BulkAttendanceEntryResult> BulkEntryAsync(Guid tenantId, BulkAttendanceEntryRequest request, AttendanceBulkAuthority authority,
        RequestContext context, CancellationToken ct)
    {
        var ids = (request.EmployeeIds ?? Array.Empty<int>()).Distinct().ToList();
        if (ids.Count == 0) throw new InvalidOperationException("Choose at least one employee.");
        if (ids.Count > BulkMaxEmployees)
            throw new InvalidOperationException($"Mass entry is limited to {BulkMaxEmployees} employees at a time and this selection has {ids.Count}. Split it into smaller groups.");
        if (request.FromDate > request.ToDate) throw new InvalidOperationException("From date must be on or before To date.");
        var days = request.ToDate.DayNumber - request.FromDate.DayNumber + 1;
        if (days > BulkMaxDays)
            throw new InvalidOperationException($"Mass entry is limited to {BulkMaxDays} days at a time and this range has {days}. Enter the rest as a second range.");
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length == 0) throw new InvalidOperationException("A reason is required for mass entry. Say why these punches are being entered by hand.");
        if (reason.Length > BulkMaxReasonLength) throw new InvalidOperationException($"The reason is limited to {BulkMaxReasonLength} characters.");
        if (!TryParseClock(request.InTime, out var inTime)) throw new InvalidOperationException("In time must be a time of day such as 08:00.");
        TimeOnly? outTime = null;
        if (!string.IsNullOrWhiteSpace(request.OutTime))
        {
            if (!TryParseClock(request.OutTime, out var parsedOut)) throw new InvalidOperationException("Out time must be a time of day such as 17:00, or left empty.");
            outTime = parsedOut;
        }
        var method = Clean(request.VerificationMethod, "Manual");
        if (ReservedVerificationLabels.Violates(method, null)) throw new AttendanceRefusalException(AttendanceRefusals.VerificationLabelReserved);
        var location = Clean(request.LocationName);

        var tz = await ResolveTenantTimeZoneAsync(tenantId, ct);
        var batchRef = Guid.NewGuid().ToString();
        var payload = JsonSerializer.Serialize(new { reason, bulkEntry = true, batch = batchRef });

        var units = new List<BulkUnit>();
        var ordinal = 0;
        foreach (var id in ids)
            for (var date = request.FromDate; date <= request.ToDate; date = date.AddDays(1))
            {
                var unit = new BulkUnit { RowNumber = ++ordinal, EmployeeId = id, Date = date, Planned = outTime is null ? 1 : 2 };
                // An out time at or before the in time is the next morning: an overnight shift.
                var outDate = outTime is { } later && later <= inTime ? date.AddDays(1) : date;
                DateTime outUtc = default;
                var readable = TryLocalToUtc(date, inTime, tz, out var inUtc, out var timeError);
                if (readable && outTime is { } outAt) readable = TryLocalToUtc(outDate, outAt, tz, out outUtc, out timeError);
                if (!readable) unit.ParseError = timeError;
                else
                {
                    unit.Punches.Add(new BulkPunch { Direction = "In", Utc = inUtc, Location = location, Method = method, Source = BulkEntrySource, PayloadJson = payload });
                    if (outTime is not null)
                        unit.Punches.Add(new BulkPunch { Direction = "Out", Utc = outUtc, Location = location, Method = method, Source = BulkEntrySource, PayloadJson = payload });
                }
                units.Add(unit);
            }

        return await InBulkTransactionAsync(tenantId, commit: true, async () =>
        {
            await EvaluateBulkAsync(tenantId, units, authority, tz, ct);
            await StoreBulkPunchesAsync(tenantId, units, batchRef, context, ct);
            var created = units.Sum(u => u.Punches.Count(p => p.State == BulkPunchState.New));
            var duplicates = units.Sum(u => u.Punches.Count(p => p.State == BulkPunchState.Duplicate));
            var failedUnits = units.Where(u => u.Error is not null).ToList();
            var failed = failedUnits.Sum(u => u.Planned);
            await Audit(tenantId, context, "attendance.bulk_entry.completed", "AttendanceRawEvent", batchRef,
                JsonSerializer.Serialize(new
                {
                    employees = ids.Count, from = request.FromDate, to = request.ToDate, inTime = request.InTime, outTime = request.OutTime,
                    method, reason, requested = units.Sum(u => u.Planned), created, duplicates, failed,
                }), ct);
            await _db.SaveChangesAsync(ct);
            return new BulkAttendanceEntryResult(units.Sum(u => u.Planned), created, duplicates, failed,
                failedUnits.Take(BulkMaxReportedErrors).Select(u => new BulkAttendanceEntryError(u.ResolvedCode ?? $"#{u.EmployeeId}", u.Date!.Value, u.Error!)).ToList());
        }, ct);
    }

    // ── the shared routine ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rows that are new, and rows that are all duplicates (a row that lost a concurrent race was reclassified before this).</summary>
    private static (int New, int Duplicate) Tally(IEnumerable<BulkUnit> units)
    {
        var judged = units.Where(u => u.Error is null).ToList();
        return (judged.Count(u => u.Punches.Any(p => p.State == BulkPunchState.New)),
                judged.Count(u => u.Punches.All(p => p.State == BulkPunchState.Duplicate)));
    }

    private enum BulkPunchState { Pending, New, Duplicate }

    private sealed class BulkPunch
    {
        public required string Direction { get; init; }
        public required DateTime Utc { get; init; }
        public string Location { get; init; } = string.Empty;
        public required string Method { get; init; }
        public required string Source { get; init; }
        public required string PayloadJson { get; init; }
        public BulkPunchState State { get; set; }
        public bool OnRecord { get; set; }
    }

    /// <summary>One thing that succeeds or fails as a whole: a row of a file (one punch) or an employee-day of mass entry (an In and an Out).</summary>
    private sealed class BulkUnit
    {
        public int RowNumber { get; init; }
        public string RawRow { get; init; } = string.Empty;
        public string? EmployeeCode { get; init; }
        public int? EmployeeId { get; init; }
        public DateOnly? Date { get; init; }
        public string? ParseError { get; set; }
        public string? ParseColumn { get; init; }
        public List<BulkPunch> Punches { get; } = new();
        public int Planned { get; set; } = 1;

        public string? Error { get; private set; }
        public string? ErrorColumn { get; private set; }
        public int ResolvedId { get; set; }
        public string? ResolvedCode { get; set; }

        public void Reset() { Error = ParseError; ErrorColumn = ParseColumn; ResolvedId = 0; ResolvedCode = null; foreach (var p in Punches) { p.State = BulkPunchState.Pending; p.OnRecord = false; } }
        public void Fail(string column, string message) { Error ??= message; ErrorColumn ??= column; }
    }

    /// <summary>
    /// Judges every unit: employee (active, in scope, not the caller), reserved labels, future time, what is already on record
    /// (a duplicate, counted and not an error) and payroll-locked days. Writes nothing; the preview stops here.
    /// </summary>
    private async Task EvaluateBulkAsync(Guid tenantId, IReadOnlyList<BulkUnit> units, AttendanceBulkAuthority authority, TimeZoneInfo tz, CancellationToken ct)
    {
        var latest = _clock.GetUtcNow().UtcDateTime.AddMinutes(BulkMaxFutureMinutes);
        foreach (var u in units) u.Reset();

        // 1. Who: active employees by code or id, then the caller's own record and the caller's scope.
        var open = units.Where(u => u.Error is null).ToList();
        var found = await FindActiveEmployeesAsync(tenantId,
            open.Where(u => u.EmployeeId is null).Select(u => u.EmployeeCode!).Distinct(StringComparer.Ordinal).ToList(),
            open.Where(u => u.EmployeeId is not null).Select(u => u.EmployeeId!.Value).Distinct().ToList(), ct);
        var byCode = new Dictionary<string, (int Id, string Code)>(StringComparer.Ordinal);
        foreach (var e in found) byCode.TryAdd(e.Code, e);
        var byId = found.ToDictionary(e => e.Id);
        var allowed = authority.AllowedEmployeeIds?.ToHashSet();
        foreach (var u in open)
        {
            (int Id, string Code) employee = default;
            var hit = u.EmployeeId is int id ? byId.TryGetValue(id, out employee) : byCode.TryGetValue(u.EmployeeCode!, out employee);
            if (!hit) { u.Fail(AttendancePunchCsv.ColumnCode, BulkNotFoundMessage); continue; }
            if (authority.SelfEmployeeId == employee.Id) { u.ResolvedCode = employee.Code; u.Fail(AttendancePunchCsv.ColumnCode, BulkSelfMessage); continue; }
            // Out of scope reads exactly like an unknown code, so the message never reveals that the employee exists.
            if (allowed is not null && !allowed.Contains(employee.Id)) { u.Fail(AttendancePunchCsv.ColumnCode, BulkNotFoundMessage); continue; }
            u.ResolvedId = employee.Id;
            u.ResolvedCode = employee.Code;
        }

        // 2. What: the server's own verification labels are never accepted from a file or a form, and no punch from the future.
        foreach (var u in units.Where(u => u.Error is null))
            foreach (var p in u.Punches)
            {
                if (ReservedVerificationLabels.Violates(p.Method, null)) { u.Fail(AttendancePunchCsv.ColumnMethod, AttendanceRefusals.VerificationLabelReserved.Message); break; }
                if (p.Utc > latest)
                {
                    u.Fail(AttendancePunchCsv.ColumnTimestamp, $"This time is in the future. A punch may be at most {BulkMaxFutureMinutes} minutes ahead of now ({tz.Id} time).");
                    break;
                }
            }

        // 3. Already on record: a duplicate. Counted, never an error, so re-sending a file or a request is safe.
        var live = units.Where(u => u.Error is null && u.Punches.Count > 0).ToList();
        var existing = await ExistingPunchKeysAsync(tenantId, live, ct);
        foreach (var u in live)
            foreach (var p in u.Punches)
                p.OnRecord = existing.Contains((u.ResolvedId, p.Utc.Ticks / 10, p.Direction));

        // 4. Payroll-locked days: only a punch that would be NEW is refused (a duplicate changes nothing).
        await RejectLockedDaysAsync(tenantId, live.Where(u => u.Error is null).ToList(), tz, ct);

        // 5. Classify, in file order: the first of two identical punches is new, the repeat is a duplicate.
        var seen = new HashSet<(int, long, string)>();
        foreach (var u in units.Where(u => u.Error is null))
            foreach (var p in u.Punches)
            {
                var key = (u.ResolvedId, p.Utc.Ticks / 10, p.Direction);
                p.State = p.OnRecord || !seen.Add(key) ? BulkPunchState.Duplicate : BulkPunchState.New;
            }
    }

    private async Task<List<(int Id, string Code)>> FindActiveEmployeesAsync(Guid tenantId, IReadOnlyList<string> codes, IReadOnlyList<int> ids, CancellationToken ct)
    {
        var found = new List<(int, string)>();
        foreach (var chunk in codes.Chunk(1000))
        {
            var list = chunk.ToList();
            var rows = await _db.Employees.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.Status == EmployeeStatuses.Active && list.Contains(x.EmployeeCode))
                .Select(x => new { x.Id, x.EmployeeCode }).ToListAsync(ct);
            found.AddRange(rows.Select(r => (r.Id, r.EmployeeCode)));
        }
        foreach (var chunk in ids.Chunk(1000))
        {
            var list = chunk.ToList();
            var rows = await _db.Employees.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.Status == EmployeeStatuses.Active && list.Contains(x.Id))
                .Select(x => new { x.Id, x.EmployeeCode }).ToListAsync(ct);
            found.AddRange(rows.Select(r => (r.Id, r.EmployeeCode)));
        }
        return found.Distinct().ToList();
    }

    /// <summary>The (employee, instant to the microsecond, direction) of every punch already stored in the window the candidates span.</summary>
    private async Task<HashSet<(int, long, string)>> ExistingPunchKeysAsync(Guid tenantId, IReadOnlyList<BulkUnit> live, CancellationToken ct)
    {
        var keys = new HashSet<(int, long, string)>();
        var punches = live.SelectMany(u => u.Punches).ToList();
        if (punches.Count == 0) return keys;
        var min = punches.Min(p => p.Utc);
        var max = punches.Max(p => p.Utc);
        foreach (var chunk in live.Select(u => u.ResolvedId).Distinct().Chunk(1000))
        {
            var empIds = chunk.ToList();
            var rows = await _db.AttendanceRawEvents.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.EmployeeId != null && empIds.Contains(e.EmployeeId.Value)
                            && e.PunchTimestampUtc >= min && e.PunchTimestampUtc <= max)
                .Select(e => new { e.EmployeeId, e.PunchTimestampUtc, e.PunchDirection }).ToListAsync(ct);
            foreach (var r in rows) keys.Add((r.EmployeeId!.Value, r.PunchTimestampUtc.Ticks / 10, r.PunchDirection));
        }
        return keys;
    }

    /// <summary>
    /// A new punch whose WORK DATE (AttendanceWorkDate: an overnight shift's early-morning punch belongs to the shift's own day) is in a
    /// payroll-locked period (AttendancePayrollLock) fails its unit. Two cheap range queries rule out the common case of no lock at all
    /// before the per-employee-day checks run.
    /// </summary>
    private async Task RejectLockedDaysAsync(Guid tenantId, IReadOnlyList<BulkUnit> live, TimeZoneInfo tz, CancellationToken ct)
    {
        var pending = live.SelectMany(u => u.Punches.Where(p => !p.OnRecord).Select(p => (Unit: u, Punch: p))).ToList();
        if (pending.Count == 0) return;
        var workDates = await AttendanceWorkDate.ResolveManyAsync(_db, tenantId, pending.Select(x => (x.Unit.ResolvedId, x.Punch.Utc)).ToList(), tz, ct);
        var from = workDates.Min();
        var to = workDates.Max();
        var employeeIds = pending.Select(x => x.Unit.ResolvedId).Distinct().ToList();
        var anyLockPeriod = await _db.AttendanceLockPeriods.AnyAsync(x => x.TenantId == tenantId && x.Status == "Locked" && x.PeriodStart <= to && x.PeriodEnd >= from, ct);
        var anyLockedDay = !anyLockPeriod && await _db.AttendanceDailyRecords.AnyAsync(d => d.TenantId == tenantId && d.IsPayrollLocked
            && d.WorkDate >= from && d.WorkDate <= to && employeeIds.Contains(d.EmployeeId), ct);
        if (!anyLockPeriod && !anyLockedDay) return;

        var verdicts = new Dictionary<(int, DateOnly), bool>();
        for (var i = 0; i < pending.Count; i++)
        {
            var (unit, _) = pending[i];
            if (unit.Error is not null) continue;
            var key = (unit.ResolvedId, workDates[i]);
            if (!verdicts.TryGetValue(key, out var locked))
                verdicts[key] = locked = await IsLocked(tenantId, key.Item1, key.Item2, ct);
            if (locked)
                unit.Fail(AttendancePunchCsv.ColumnTimestamp, $"{workDates[i]:yyyy-MM-dd} is in a payroll-locked period, so punches can't be added to it.");
        }
    }

    /// <summary>
    /// Stores every NEW punch in one idempotent batch (<see cref="SaveRawEventsIdempotentAsync"/>: a unique-index conflict from a concurrent
    /// resend skips just that row). A row that lost such a race becomes a duplicate.
    /// </summary>
    private async Task StoreBulkPunchesAsync(Guid tenantId, IReadOnlyList<BulkUnit> units, string batchReference, RequestContext context, CancellationToken ct)
    {
        var staged = new List<(BulkPunch Punch, AttendanceRawEvent Event)>();
        foreach (var u in units.Where(u => u.Error is null))
            foreach (var p in u.Punches.Where(p => p.State == BulkPunchState.New))
                staged.Add((p, new AttendanceRawEvent
                {
                    TenantId = tenantId, EmployeeId = u.ResolvedId, EmployeeCode = u.ResolvedCode ?? string.Empty,
                    Source = p.Source, PunchTimestampUtc = p.Utc, PunchDirection = p.Direction, LocationName = p.Location,
                    IpAddress = Clean(context.IpAddress), RawPayloadJson = CleanJson(p.PayloadJson), SyncBatchReference = batchReference,
                    VerificationMethod = p.Method, CreatedBy = context.UserId,
                }));
        var stored = await SaveRawEventsIdempotentAsync(staged.Select(s => s.Event).ToList(), ct);
        var storedIds = stored.Select(e => e.Id).ToHashSet();
        foreach (var (punch, ev) in staged)
            if (!storedIds.Contains(ev.Id)) punch.State = BulkPunchState.Duplicate;
        _db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction on a relational database (so the all-or-nothing batch and the advisory lock hold),
    /// taking a per-tenant lock first so two bulk writes of the same punches can't both pass the duplicate check. A preview is read-only and
    /// needs neither. (A punch's unique index cannot arbitrate here: the web/CSV rows have no device, and NULLs are distinct on PostgreSQL.)
    /// </summary>
    private async Task<T> InBulkTransactionAsync<T>(Guid tenantId, bool commit, Func<Task<T>> work, CancellationToken ct)
    {
        if (!commit || !_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
        {
            if (commit) await TakeBulkLockAsync(tenantId, ct);
            return await work();
        }
        var strategy = _db.Database.CreateExecutionStrategy();
        var attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            // A retry after a transient failure starts from nothing: the failed attempt's staged rows are not saved and must not be re-added.
            if (attempt++ > 0) _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            await TakeBulkLockAsync(tenantId, ct);
            var result = await work();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    private Task TakeBulkLockAsync(Guid tenantId, CancellationToken ct)
    {
        if (!(_db.Database.ProviderName ?? string.Empty).Contains("Npgsql", StringComparison.OrdinalIgnoreCase) || _db.Database.CurrentTransaction is null)
            return Task.CompletedTask;
        return AcquirePunchAdvisoryLockAsync($"attendance-bulk-punches:{tenantId:N}", ct);
    }

    private static bool TryLocalToUtc(DateOnly date, TimeOnly time, TimeZoneInfo tz, out DateTime utc, out string? error)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local))
        {
            utc = default;
            error = $"{time:HH\\:mm} on {date:yyyy-MM-dd} does not exist in {tz.Id} (the clocks change then).";
            return false;
        }
        utc = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(local, tz), DateTimeKind.Utc);
        error = null;
        return true;
    }

    private static bool TryParseClock(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact((text ?? string.Empty).Trim(), new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
