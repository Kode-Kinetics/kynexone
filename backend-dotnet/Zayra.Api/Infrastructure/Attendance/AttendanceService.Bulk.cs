using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>Another bulk attendance write holds the tenant's bulk lock and did not release it in time. The caller retries (HTTP 409).</summary>
public class AttendanceBusyException(string? message = null) : InvalidOperationException(message ?? AttendanceService.BulkBusyMessage);

/// <summary>
/// Bulk punches: the CSV import, its dry run and mass entry for a selection of employees. All three go through ONE judging routine
/// (<see cref="EvaluateBulkAsync"/>) and ONE save, so a file cannot be judged one way in the preview and another for real, and mass
/// entry can never write something the import would have refused.
///
/// <para>Every guard of the single-punch paths holds per row: no punch for the caller's own record (I1); only employees inside the
/// caller's data scope, an out-of-scope row reading exactly like an unknown code (I2); active employees only (I3); the server's
/// verification labels are reserved (I4); nothing in a payroll-locked period (I5); nothing more than five minutes ahead of now (I6).</para>
///
/// <para><b>Idempotency (I7).</b> A web/CSV punch has no device, and NULLs are distinct in the punch unique index, so the index cannot
/// stop a duplicate. The writers take turns on ONE per-tenant advisory lock instead: a bulk write takes it EXCLUSIVE, every single-punch
/// writer (events/push, punch/*) takes it SHARED as well as its own per-punch lock. A bulk write therefore sees every committed punch
/// when it checks for duplicates and nothing can slip in between the check and the insert.</para>
/// </summary>
public partial class AttendanceService
{
    /// <summary>The advisory-lock identity of a tenant's bulk punch writes: EXCLUSIVE for import and mass entry, SHARED for every single-punch writer.</summary>
    internal static string BulkLockKey(Guid tenantId) => $"attendance-bulk-punches:{tenantId:N}";

    public const string BulkBusyMessage = "Another bulk attendance operation is running for this company; try again in a moment.";

    /// <summary>How far ahead of the tenant's now a bulk punch may be.</summary>
    internal const int BulkMaxFutureMinutes = 5;
    public const int BulkMaxEmployees = 500;
    public const int BulkMaxDays = 31;
    /// <summary>The most punches one mass entry writes (employees x days x in/out).</summary>
    public const int BulkMaxPunches = 5000;
    /// <summary>A bulk punch may be at most this many days before the tenant's today; and no earlier than year 2000.</summary>
    public const int BulkMaxPastDays = 400;
    internal const int MinPunchYear = 2000;
    internal const int MaxPunchYear = 2100;
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

    /// <summary>Test seam: an import under a CHOSEN batch id, the way a retried attempt re-runs under the id it first chose.</summary>
    internal Task<AttendanceImportResult> ImportCsvWithBatchIdAsync(Guid tenantId, ImportAttendanceRequest request, AttendanceBulkAuthority authority,
        RequestContext context, Guid batchId, CancellationToken ct) =>
        RunImportAsync(tenantId, request, authority, context, commit: true, ct, batchId);

    /// <summary>Test seam: replaces the tenant bulk lock (so a test can make it time out without PostgreSQL).</summary>
    internal Func<Guid, CancellationToken, Task>? BulkLockOverride { get; set; }

    /// <summary>Test seam: stands in for the work-date resolution of a batch of punches (null = use the real one).</summary>
    internal Func<TimeZoneInfo, IReadOnlyList<(int EmployeeId, DateTime PunchUtc)>, IReadOnlyList<DateOnly>?>? WorkDateResolverOverride { get; set; }

    private async Task<AttendanceImportResult> RunImportAsync(Guid tenantId, ImportAttendanceRequest request, AttendanceBulkAuthority authority,
        RequestContext context, bool commit, CancellationToken ct, Guid? batchIdOverride = null)
    {
        var tz = await ResolveTenantTimeZoneAsync(tenantId, ct);
        var file = AttendancePunchCsv.Parse(request.CsvContent, tz);
        var fileName = string.IsNullOrWhiteSpace(request.FileName) ? "attendance.csv" : request.FileName.Trim();
        var batchId = batchIdOverride ?? Guid.NewGuid();

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
            // A retry after a transient failure whose first attempt in fact committed (the acknowledgement was lost): the batch is
            // already stored, so its outcome is returned instead of a primary-key conflict.
            if (commit && await StoredImportResultAsync(tenantId, batchId, tz, ct) is { } stored) return stored;
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
                        TenantId = tenantId, ImportBatchId = batchId, RowNumber = u.RowNumber, RawRow = Cap(u.RawRow, 500), ErrorMessage = u.Error!,
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
                failed.Take(BulkMaxReportedErrors).Select(u => new AttendanceImportRowError(u.RowNumber, u.ErrorColumn, u.Error!, Cap(u.RawRow, 500))).ToList());
        }, ct);
    }

    /// <summary>The outcome of an import batch that is already stored (its error rows keep their message and raw row; the column is not persisted).</summary>
    private async Task<AttendanceImportResult?> StoredImportResultAsync(Guid tenantId, Guid batchId, TimeZoneInfo tz, CancellationToken ct)
    {
        var batch = await _db.AttendanceImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.TenantId == tenantId && b.Id == batchId, ct);
        if (batch is null) return null;
        var errors = await _db.AttendanceImportErrors.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.ImportBatchId == batchId).OrderBy(e => e.RowNumber).Take(BulkMaxReportedErrors)
            .Select(e => new AttendanceImportRowError(e.RowNumber, null, e.ErrorMessage, e.RawRow)).ToListAsync(ct);
        return new AttendanceImportResult(batch.Id, batch.Status, batch.FileName, batch.TotalRows, batch.ImportedRows, batch.ImportedRows,
            Math.Max(0, batch.TotalRows - batch.ImportedRows - batch.FailedRows), batch.FailedRows, tz.Id, errors);
    }

    public Task<BulkAttendanceEntryResult> BulkEntryAsync(Guid tenantId, BulkAttendanceEntryRequest request, AttendanceBulkAuthority authority,
        RequestContext context, CancellationToken ct) =>
        RunBulkEntryAsync(tenantId, request, authority, context, ct, Guid.NewGuid().ToString());

    /// <summary>Test seam: mass entry under a CHOSEN batch reference (see <see cref="ImportCsvWithBatchIdAsync"/>).</summary>
    internal Task<BulkAttendanceEntryResult> BulkEntryWithBatchAsync(Guid tenantId, BulkAttendanceEntryRequest request, AttendanceBulkAuthority authority,
        RequestContext context, string batchRef, CancellationToken ct) =>
        RunBulkEntryAsync(tenantId, request, authority, context, ct, batchRef);

    private async Task<BulkAttendanceEntryResult> RunBulkEntryAsync(Guid tenantId, BulkAttendanceEntryRequest request, AttendanceBulkAuthority authority,
        RequestContext context, CancellationToken ct, string batchRef)
    {
        var ids = (request.EmployeeIds ?? Array.Empty<int>()).Distinct().ToList();
        if (ids.Count == 0) throw new InvalidOperationException("Choose at least one employee.");
        if (ids.Count > BulkMaxEmployees)
            throw new InvalidOperationException($"Mass entry is limited to {BulkMaxEmployees} employees at a time and this selection has {ids.Count}. Split it into smaller groups.");
        if (request.FromDate == default || request.ToDate == default) throw new InvalidOperationException("Choose a from date and a to date.");
        if (request.FromDate > request.ToDate) throw new InvalidOperationException("From date must be on or before To date.");
        var days = request.ToDate.DayNumber - request.FromDate.DayNumber + 1;
        if (days > BulkMaxDays)
            throw new InvalidOperationException($"Mass entry is limited to {BulkMaxDays} days at a time and this range has {days}. Enter the rest as a second range.");
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length == 0) throw new InvalidOperationException("A reason is required for mass entry. Say why these punches are being entered by hand.");
        if (reason.Length > BulkMaxReasonLength) throw new InvalidOperationException($"The reason is limited to {BulkMaxReasonLength} characters.");
        // At least one of the two. An Out alone is the everyday fix for a forgotten punch-out.
        if (string.IsNullOrWhiteSpace(request.InTime) && string.IsNullOrWhiteSpace(request.OutTime))
            throw new InvalidOperationException("Enter an in time or an out time (or both).");
        TimeOnly? inTime = null;
        if (!string.IsNullOrWhiteSpace(request.InTime))
        {
            if (!TryParseClock(request.InTime, out var parsedIn)) throw new InvalidOperationException("In time must be a time of day such as 08:00, or left empty.");
            inTime = parsedIn;
        }
        TimeOnly? outTime = null;
        if (!string.IsNullOrWhiteSpace(request.OutTime))
        {
            if (!TryParseClock(request.OutTime, out var parsedOut)) throw new InvalidOperationException("Out time must be a time of day such as 17:00, or left empty.");
            outTime = parsedOut;
        }
        if (inTime is not null && outTime == inTime)
            throw new InvalidOperationException("The out time is the same as the in time. Use a different out time (an earlier one means the next morning), or leave it empty to enter only the in punch.");
        var perDay = (inTime is null ? 0 : 1) + (outTime is null ? 0 : 1);
        var totalPunches = ids.Count * days * perDay;
        if (totalPunches > BulkMaxPunches)
            throw new InvalidOperationException(
                $"Mass entry is limited to {BulkMaxPunches.ToString("N0", CultureInfo.InvariantCulture)} punches at a time. This request is {ids.Count.ToString("N0", CultureInfo.InvariantCulture)} employees x {days} days x {perDay} punch{(perDay == 1 ? "" : "es")} a day = {totalPunches.ToString("N0", CultureInfo.InvariantCulture)}. Choose fewer employees or days, or split it into smaller requests.");
        var method = Clean(request.VerificationMethod, "Manual");
        if (ReservedVerificationLabels.Violates(method, null)) throw new AttendanceRefusalException(AttendanceRefusals.VerificationLabelReserved);
        var location = Clean(request.LocationName);

        var tz = await ResolveTenantTimeZoneAsync(tenantId, ct);
        var today = TenantTimeZone.LocalDate(tz, _clock.GetUtcNow().UtcDateTime);
        if (request.FromDate.Year < MinPunchYear || request.FromDate < today.AddDays(-BulkMaxPastDays))
            throw new InvalidOperationException($"The from date is more than {BulkMaxPastDays} days in the past. Mass entry covers the last {BulkMaxPastDays} days; older attendance is corrected through a correction request.");
        if (request.ToDate.Year > MaxPunchYear)
            throw new InvalidOperationException("The to date is not a plausible date.");
        var payload = JsonSerializer.Serialize(new { reason, bulkEntry = true, batch = batchRef });

        var units = new List<BulkUnit>();
        var ordinal = 0;
        foreach (var id in ids)
            for (var date = request.FromDate; date <= request.ToDate; date = date.AddDays(1))
            {
                var unit = new BulkUnit { RowNumber = ++ordinal, EmployeeId = id, Date = date, Planned = perDay };
                // An out time at or before the in time is the next morning: an overnight shift. With no in time there is nothing to cross.
                var outDate = outTime is { } later && inTime is { } earlier && later <= earlier ? date.AddDays(1) : date;
                DateTime inUtc = default, outUtc = default;
                string? timeError = null;
                var readable = inTime is not { } inAt || TryLocalToUtc(date, inAt, tz, out inUtc, out timeError);
                if (readable && outTime is { } outAt) readable = TryLocalToUtc(outDate, outAt, tz, out outUtc, out timeError);
                if (!readable) unit.ParseError = timeError;
                else
                {
                    if (inTime is not null)
                        unit.Punches.Add(new BulkPunch { Direction = "In", Utc = inUtc, Location = location, Method = method, Source = BulkEntrySource, PayloadJson = payload });
                    if (outTime is not null)
                        unit.Punches.Add(new BulkPunch { Direction = "Out", Utc = outUtc, Location = location, Method = method, Source = BulkEntrySource, PayloadJson = payload });
                }
                units.Add(unit);
            }

        return await InBulkTransactionAsync(tenantId, commit: true, async () =>
        {
            if (await StoredBulkEntryResultAsync(tenantId, batchRef, ct) is { } stored) return stored;   // a retry whose first attempt committed
            await EvaluateBulkAsync(tenantId, units, authority, tz, ct);
            await StoreBulkPunchesAsync(tenantId, units, batchRef, context, ct);
            var created = units.Sum(u => u.Punches.Count(p => p.State == BulkPunchState.New));
            var duplicates = units.Sum(u => u.Punches.Count(p => p.State == BulkPunchState.Duplicate));
            var failedUnits = units.Where(u => u.Error is not null).ToList();
            var failed = failedUnits.Sum(u => u.Planned);
            var errors = failedUnits.Take(BulkMaxReportedErrors).Select(u => new BulkAttendanceEntryError(u.ResolvedCode ?? $"#{u.EmployeeId}", u.Date!.Value, u.Error!)).ToList();
            await Audit(tenantId, context, BulkEntryAuditAction, "AttendanceRawEvent", batchRef,
                JsonSerializer.Serialize(new
                {
                    employees = ids.Count, from = request.FromDate, to = request.ToDate, inTime = request.InTime, outTime = request.OutTime,
                    method, reason, requested = units.Sum(u => u.Planned), created, duplicates, failed,
                    employeeIds = ids.OrderBy(x => x).ToList(),
                    errors = errors.Take(BulkReplayErrors).ToList(),
                }), ct);
            await _db.SaveChangesAsync(ct);
            return new BulkAttendanceEntryResult(units.Sum(u => u.Planned), created, duplicates, failed, errors);
        }, ct);
    }

    private const string BulkEntryAuditAction = "attendance.bulk_entry.completed";
    /// <summary>How many refused employee-days the audit row keeps, so a retried request can answer with them.</summary>
    private const int BulkReplayErrors = 100;

    private async Task<BulkAttendanceEntryResult?> StoredBulkEntryResultAsync(Guid tenantId, string batchRef, CancellationToken ct)
    {
        var audit = await _db.AttendanceAuditLogs.AsNoTracking()
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Action == BulkEntryAuditAction && a.EntityId == batchRef, ct);
        if (audit is null) return null;
        using var doc = JsonDocument.Parse(audit.MetadataJson);
        var root = doc.RootElement;
        var errors = root.TryGetProperty("errors", out var list)
            ? list.Deserialize<List<BulkAttendanceEntryError>>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new()
            : new List<BulkAttendanceEntryError>();
        return new BulkAttendanceEntryResult(root.GetProperty("requested").GetInt32(), root.GetProperty("created").GetInt32(),
            root.GetProperty("duplicates").GetInt32(), root.GetProperty("failed").GetInt32(), errors);
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
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var latest = nowUtc.AddMinutes(BulkMaxFutureMinutes);
        var earliestDay = TenantTimeZone.LocalDate(tz, nowUtc).AddDays(-BulkMaxPastDays);
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
                if (p.Utc.Year < MinPunchYear || TenantTimeZone.LocalDate(tz, p.Utc) < earliestDay)
                {
                    u.Fail(AttendancePunchCsv.ColumnTimestamp, $"This date is more than {BulkMaxPastDays} days in the past, so it can't be entered here. Older attendance is corrected through a correction request.");
                    break;
                }
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
    /// payroll-locked period (AttendancePayrollLock) fails its unit. The lock rules are loaded ONCE for the range
    /// (<see cref="AttendancePayrollLock.SnapshotAsync"/>) and decided in memory, with the same answers as the per-day check.
    /// </summary>
    private async Task RejectLockedDaysAsync(Guid tenantId, IReadOnlyList<BulkUnit> live, TimeZoneInfo tz, CancellationToken ct)
    {
        var pending = live.SelectMany(u => u.Punches.Where(p => !p.OnRecord).Select(p => (Unit: u, Punch: p))).ToList();
        if (pending.Count == 0) return;
        var workDates = await ResolveWorkDatesAsync(tenantId, pending, tz, ct);
        var known = pending.Select((x, i) => (x.Unit, Date: workDates[i])).Where(x => x.Unit.Error is null && x.Date is not null).ToList();
        if (known.Count == 0) return;
        var snapshot = await AttendancePayrollLock.SnapshotAsync(_db, tenantId, known.Min(x => x.Date!.Value), known.Max(x => x.Date!.Value),
            known.Select(x => x.Unit.ResolvedId).Distinct().ToList(), ct);
        foreach (var (unit, date) in known)
            if (unit.Error is null && snapshot.IsLocked(unit.ResolvedId, date!.Value))
                unit.Fail(AttendancePunchCsv.ColumnTimestamp, $"{date.Value:yyyy-MM-dd} is in a payroll-locked period, so punches can't be added to it.");
    }

    /// <summary>
    /// The work date of each pending punch, in order (null where it could not be worked out: that row's unit is failed). One batch query;
    /// if the batch throws, each punch is resolved on its own, so one bad row can never fail the whole file.
    /// </summary>
    private async Task<IReadOnlyList<DateOnly?>> ResolveWorkDatesAsync(Guid tenantId, List<(BulkUnit Unit, BulkPunch Punch)> pending, TimeZoneInfo tz, CancellationToken ct)
    {
        var punches = pending.Select(x => (x.Unit.ResolvedId, x.Punch.Utc)).ToList();
        async Task<IReadOnlyList<DateOnly>> Resolve(IReadOnlyList<(int EmployeeId, DateTime PunchUtc)> batch) =>
            WorkDateResolverOverride?.Invoke(tz, batch) ?? await AttendanceWorkDate.ResolveManyAsync(_db, tenantId, batch, tz, ct);
        try { return (await Resolve(punches)).Select(d => (DateOnly?)d).ToList(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            var dates = new List<DateOnly?>();
            foreach (var (unit, punch) in pending)
            {
                try { dates.Add((await Resolve(new[] { (unit.ResolvedId, punch.Utc) }))[0]); }
                catch (Exception inner) when (inner is ArgumentException or InvalidOperationException or OverflowException)
                {
                    unit.Fail(AttendancePunchCsv.ColumnTimestamp, "This time could not be placed on a work day. Check the date.");
                    dates.Add(null);
                }
            }
            return dates;
        }
    }

    /// <summary>
    /// Stores every NEW punch in one save. The tenant bulk lock (held EXCLUSIVE since before the duplicate check, to the end of the
    /// transaction) is what keeps a concurrent writer from inserting the same punch, so there is no unique-index race to recover from here.
    /// </summary>
    private async Task StoreBulkPunchesAsync(Guid tenantId, IReadOnlyList<BulkUnit> units, string batchReference, RequestContext context, CancellationToken ct)
    {
        var events = new List<AttendanceRawEvent>();
        foreach (var u in units.Where(u => u.Error is null))
            foreach (var p in u.Punches.Where(p => p.State == BulkPunchState.New))
                events.Add(new AttendanceRawEvent
                {
                    TenantId = tenantId, EmployeeId = u.ResolvedId, EmployeeCode = u.ResolvedCode ?? string.Empty,
                    Source = p.Source, PunchTimestampUtc = p.Utc, PunchDirection = p.Direction, LocationName = p.Location,
                    IpAddress = Clean(context.IpAddress), RawPayloadJson = CleanJson(p.PayloadJson), SyncBatchReference = batchReference,
                    VerificationMethod = p.Method, CreatedBy = context.UserId,
                });
        if (events.Count > 0)
        {
            _db.AttendanceRawEvents.AddRange(events);
            await _db.SaveChangesAsync(ct);
        }
        _db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction on a relational database (so the all-or-nothing batch and the advisory lock hold),
    /// taking the tenant's bulk lock EXCLUSIVE first. A preview is read-only and needs neither.
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

    /// <summary>How long a bulk write waits for another to finish before answering "busy" (409).</summary>
    internal static readonly TimeSpan BulkLockWait = TimeSpan.FromSeconds(5);
    /// <summary>How long a single punch waits for a running bulk write (it holds the lock for seconds, not minutes).</summary>
    internal static readonly TimeSpan SinglePunchLockWait = TimeSpan.FromSeconds(20);

    private Task TakeBulkLockAsync(Guid tenantId, CancellationToken ct)
    {
        if (BulkLockOverride is { } over) return over(tenantId, ct);
        return AcquireTenantBulkLockAsync(tenantId, exclusive: true, BulkLockWait, null, ct);
    }

    /// <summary>
    /// The tenant's bulk punch lock, transaction-scoped: EXCLUSIVE for a bulk write, SHARED for a single punch. A try-lock polled for at most
    /// <paramref name="wait"/>, never an unbounded wait: a stuck holder must not pile up every request behind it. PostgreSQL only.
    /// </summary>
    internal async Task AcquireTenantBulkLockAsync(Guid tenantId, bool exclusive, TimeSpan wait, string? busyMessage, CancellationToken ct)
    {
        if (!(_db.Database.ProviderName ?? string.Empty).Contains("Npgsql", StringComparison.OrdinalIgnoreCase) || _db.Database.CurrentTransaction is null) return;
        var key = BulkLockKey(tenantId);
        var deadline = DateTime.UtcNow + wait;
        while (!await _db.Database.SqlQuery<bool>(
            $"SELECT (CASE WHEN {exclusive} THEN pg_try_advisory_xact_lock(hashtextextended({key}, 0)) ELSE pg_try_advisory_xact_lock_shared(hashtextextended({key}, 0)) END) AS \"Value\"")
            .SingleAsync(ct))
        {
            if (DateTime.UtcNow >= deadline) throw new AttendanceBusyException(busyMessage);
            await Task.Delay(100, ct);
        }
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

    private static string Cap(string text, int max) => text.Length <= max ? text : text[..max];
}
