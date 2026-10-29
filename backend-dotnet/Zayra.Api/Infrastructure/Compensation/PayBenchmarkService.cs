using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Compensation;

/// <summary>Set-level provenance the importer must supply with the file.</summary>
public sealed record PayBenchmarkImportMeta(
    Guid CompanyId, string Name, string Provider, string LicenceStatement, DateOnly? SurveyDate, string Geography, string Currency, string PayBasis, bool IsSynthetic);

/// <summary>
/// Customer-owned pay benchmarks: import (CSV or XLSX) with provenance, validation, mapping to designations/grades.
///
/// <para><b>No market data ships in product code.</b> Every figure comes from a file the customer imports and states
/// they are licensed to use. A file that carries the line <c>SYNTHETIC — NOT MARKET DATA</c> is forced to
/// <see cref="PayBenchmarkSet.IsSynthetic"/> = true whatever the form says, and the UI labels it everywhere. An import
/// is all-or-nothing: one bad row and nothing is written, every problem is listed by row and field.</para>
/// </summary>
public sealed class PayBenchmarkService(ZayraDbContext db, IAuditService audit)
{
    public const string SyntheticMarker = "SYNTHETIC — NOT MARKET DATA";
    public const int MaxRows = 5000;
    public static readonly IReadOnlyList<string> Columns = ["job_family", "level", "sample_size", "p25", "p50", "p75", "p90"];
    private static readonly string[] OptionalColumns = ["currency", "geography"];

    /// <summary>The licensed percentile tables are for compensation readers and benchmark managers only. A planner sees
    /// just the market position of their own lines, on the worksheet.</summary>
    public static bool CanRead(CompCaller c) => c.Can(CompPermissions.Read) || c.Can(CompPermissions.BenchmarksManage);

    public static string TemplateCsv() => Csv.Template(Columns);

    public async Task<IReadOnlyList<PayBenchmarkSetDto>> ListAsync(CompCaller caller, Guid? companyId, CancellationToken ct)
    {
        if (!CanRead(caller)) throw new CompForbiddenException("forbidden", "Reading benchmarks needs a compensation permission.");
        var q = db.PayBenchmarkSets.AsNoTracking().Where(s => s.TenantId == caller.TenantId);
        if (companyId is Guid id) q = q.Where(s => s.CompanyId == id);
        var sets = await q.OrderByDescending(s => s.IsActive).ThenByDescending(s => s.SurveyDate).ToListAsync(ct);
        var ids = sets.Select(s => s.Id).ToList();
        var mapped = await db.PayBenchmarkRows.AsNoTracking().Where(r => ids.Contains(r.SetId) && (r.DesignationId != null || r.GradeId != null))
            .GroupBy(r => r.SetId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return sets.Select(s => Dto(s, mapped.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<(PayBenchmarkSetDto Set, IReadOnlyList<PayBenchmarkRowDto> Rows)> GetAsync(CompCaller caller, Guid setId, CancellationToken ct)
    {
        if (!CanRead(caller)) throw new CompForbiddenException("forbidden", "Reading benchmarks needs a compensation permission.");
        var set = await db.PayBenchmarkSets.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == caller.TenantId && s.Id == setId, ct)
            ?? throw new CompNotFoundException("Benchmark set not found.");
        var rows = await db.PayBenchmarkRows.AsNoTracking().Where(r => r.SetId == set.Id).OrderBy(r => r.JobFamily).ThenBy(r => r.Level).ToListAsync(ct);
        return (Dto(set, rows.Count(r => r.DesignationId != null || r.GradeId != null)),
            rows.Select(r => new PayBenchmarkRowDto(r.Id, r.JobFamily, r.Level, r.SampleSize, r.P25, r.P50, r.P75, r.P90, r.DesignationId, r.GradeId)).ToList());
    }

    public async Task<PayBenchmarkImportResultDto> ImportAsync(CompCaller caller, PayBenchmarkImportMeta meta, string fileName, byte[] content, CancellationToken ct)
    {
        RequireManage(caller);
        var issues = new List<PayBenchmarkImportIssueDto>();
        void Fail(string field, string message) => issues.Add(new(0, field, message));

        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == caller.TenantId && c.Id == meta.CompanyId, ct);
        if (company is null) Fail("companyId", "Choose a company you have access to.");
        var name = (meta.Name ?? "").Trim();
        var provider = (meta.Provider ?? "").Trim();
        var licence = (meta.LicenceStatement ?? "").Trim();
        var geography = CountryCodeStandard.NormalizeToIso2(meta.Geography);
        var currency = (meta.Currency ?? "").Trim().ToUpperInvariant();
        if (name.Length is 0 or > 200) Fail("name", "Name the set (up to 200 characters).");
        if (provider.Length is 0 or > 200) Fail("provider", "Name the survey provider or source.");
        if (licence.Length is < 10 or > 2000) Fail("licenceStatement", "State the licence or usage right that lets you use these figures (at least 10 characters).");
        if (meta.SurveyDate is not DateOnly surveyDate || surveyDate > DateOnly.FromDateTime(DateTime.UtcNow)) Fail("surveyDate", "Give the survey date (not in the future).");
        if (geography is null) Fail("geography", "Give the country the survey covers (ISO code such as SA).");
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper)) Fail("currency", "Give the survey currency as a three-letter code.");
        if (!PayBenchmarkBases.All.Contains(meta.PayBasis)) Fail("payBasis", "Choose what the percentiles measure: MonthlyBasic or MonthlyGross.");
        if (content.Length == 0) Fail("file", "The file is empty.");
        if (content.Length > 5 * 1024 * 1024) Fail("file", "The file is larger than 5 MB.");

        List<Dictionary<string, string>> rows = [];
        var syntheticInFile = false;
        if (content.Length > 0 && content.Length <= 5 * 1024 * 1024)
        {
            try { (rows, syntheticInFile) = Read(fileName, content); }
            catch (Exception ex) when (ex is CsvShapeException or InvalidDataException or FormatException or OpenXmlPackageException or InvalidOperationException or System.Xml.XmlException or IOException)
            { Fail("file", $"The file could not be read: {ex.Message}"); }
        }
        if (issues.Count == 0)
        {
            var header = rows.Count > 0 ? rows[0].Keys.ToList() : [];
            var missing = Columns.Where(c => !header.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            if (rows.Count == 0) Fail("file", "The file has no data rows.");
            else if (missing.Count > 0) Fail("file", $"Missing column(s): {string.Join(", ", missing)}. Download the template.");
            var unknown = header.Where(h => !Columns.Concat(OptionalColumns).Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0) Fail("file", $"Unknown column(s): {string.Join(", ", unknown)}.");
            if (rows.Count > MaxRows) Fail("file", $"At most {MaxRows} rows per file.");
        }
        var parsed = new List<PayBenchmarkRow>();
        if (issues.Count == 0)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var rowNo = i + 2; // header is row 1
                var family = r["job_family"].Trim();
                var level = r["level"].Trim();
                if (family.Length is 0 or > 120) issues.Add(new(rowNo, "job_family", "Job family is required (up to 120 characters)."));
                if (level.Length is 0 or > 60) issues.Add(new(rowNo, "level", "Level is required (up to 60 characters)."));
                if (!int.TryParse(r["sample_size"].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sample) || sample <= 0)
                    issues.Add(new(rowNo, "sample_size", "Sample size must be a whole number above 0."));
                var p = new decimal[4];
                var names = new[] { "p25", "p50", "p75", "p90" };
                for (var k = 0; k < 4; k++)
                    if (!decimal.TryParse(r[names[k]].Trim(), NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out p[k]) || p[k] <= 0 || decimal.Round(p[k], 2) != p[k] || p[k] > 999_999_999m)
                        issues.Add(new(rowNo, names[k], $"{names[k].ToUpperInvariant()} must be a positive amount with at most 2 decimals and no thousands separator."));
                if (p.All(x => x > 0) && !(p[0] <= p[1] && p[1] <= p[2] && p[2] <= p[3]))
                    issues.Add(new(rowNo, "p25", "Percentiles must rise: P25 ≤ P50 ≤ P75 ≤ P90."));
                if (r.TryGetValue("currency", out var rc) && rc.Trim().Length > 0 && !string.Equals(rc.Trim(), currency, StringComparison.OrdinalIgnoreCase))
                    issues.Add(new(rowNo, "currency", $"Row currency {rc.Trim()} differs from the set currency {currency}; one file holds one currency."));
                if (r.TryGetValue("geography", out var rg) && rg.Trim().Length > 0 && CountryCodeStandard.NormalizeToIso2(rg) != geography)
                    issues.Add(new(rowNo, "geography", $"Row geography {rg.Trim()} differs from the set geography {geography}."));
                parsed.Add(new PayBenchmarkRow { JobFamily = family, Level = level, SampleSize = sample, P25 = p[0], P50 = p[1], P75 = p[2], P90 = p[3] });
            }
            foreach (var dup in parsed.Select((row, index) => (row, index)).GroupBy(x => (x.row.JobFamily.ToUpperInvariant(), x.row.Level.ToUpperInvariant())).Where(g => g.Count() > 1))
                issues.Add(new(dup.Skip(1).First().index + 2, "job_family", $"{dup.First().row.JobFamily} / {dup.First().row.Level} appears more than once."));
        }
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (issues.Count == 0 && await db.PayBenchmarkSets.AnyAsync(s => s.TenantId == caller.TenantId && s.CompanyId == meta.CompanyId && s.SourceFileSha256 == sha, ct))
            Fail("file", "This exact file was already imported for this company.");
        if (issues.Count > 0) return new PayBenchmarkImportResultDto(false, null, issues, 0);

        var set = new PayBenchmarkSet
        {
            TenantId = caller.TenantId, CompanyId = meta.CompanyId, Name = name, Provider = provider, LicenceStatement = licence,
            SurveyDate = meta.SurveyDate!.Value, Geography = geography!, Currency = currency, PayBasis = meta.PayBasis,
            IsSynthetic = meta.IsSynthetic || syntheticInFile, SourceFileName = Path.GetFileName(fileName ?? "benchmarks.csv")[..Math.Min(Path.GetFileName(fileName ?? "benchmarks.csv").Length, 260)],
            SourceFileSha256 = sha, RowCount = parsed.Count, ImportedByUserId = caller.UserId, ImportedByName = caller.UserName,
        };
        db.PayBenchmarkSets.Add(set);
        // Carry each family × level's mapping forward from the most recent earlier import for this company.
        var priorMaps = (await (from row in db.PayBenchmarkRows.AsNoTracking()
                                join s in db.PayBenchmarkSets.AsNoTracking() on row.SetId equals s.Id
                                where s.TenantId == caller.TenantId && s.CompanyId == meta.CompanyId && (row.DesignationId != null || row.GradeId != null)
                                select new { row.JobFamily, row.Level, row.DesignationId, row.GradeId, s.ImportedAtUtc }).ToListAsync(ct))
            .GroupBy(x => (x.JobFamily.ToUpperInvariant(), x.Level.ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.ImportedAtUtc).First());
        var carried = 0;
        foreach (var row in parsed)
        {
            row.TenantId = caller.TenantId; row.CompanyId = meta.CompanyId; row.SetId = set.Id;
            if (priorMaps.TryGetValue((row.JobFamily.ToUpperInvariant(), row.Level.ToUpperInvariant()), out var prior))
            { row.DesignationId = prior.DesignationId; row.GradeId = prior.GradeId; carried++; }
            db.PayBenchmarkRows.Add(row);
        }
        audit.Stage("compensation.benchmark.imported", nameof(PayBenchmarkSet), set.Id.ToString(), caller.Context,
            JsonSerializer.Serialize(new { set.Name, set.Provider, set.SurveyDate, set.Geography, set.Currency, set.PayBasis, set.IsSynthetic, set.SourceFileSha256, set.RowCount, carried }));
        await db.SaveChangesAsync(ct);
        return new PayBenchmarkImportResultDto(true, Dto(set, carried), [], carried);
    }

    public async Task<PayBenchmarkRowDto> MapRowAsync(CompCaller caller, Guid rowId, MapBenchmarkRowRequest request, CancellationToken ct)
    {
        RequireManage(caller);
        var row = await db.PayBenchmarkRows.FirstOrDefaultAsync(r => r.TenantId == caller.TenantId && r.Id == rowId, ct) ?? throw new CompNotFoundException("Benchmark row not found.");
        if (request.DesignationId is Guid d && !await db.Designations.AnyAsync(x => x.TenantId == caller.TenantId && x.Id == d, ct)) throw new CompRuleException("unknown_designation", "Designation not found.");
        if (request.GradeId is Guid g && !await db.Grades.AnyAsync(x => x.TenantId == caller.TenantId && x.Id == g && !x.IsDeleted, ct)) throw new CompRuleException("unknown_grade", "Grade not found.");
        row.DesignationId = request.DesignationId;
        row.GradeId = request.GradeId;
        audit.Stage("compensation.benchmark.mapped", nameof(PayBenchmarkRow), row.Id.ToString(), caller.Context,
            JsonSerializer.Serialize(new { row.SetId, row.JobFamily, row.Level, row.DesignationId, row.GradeId }));
        await db.SaveChangesAsync(ct);
        return new PayBenchmarkRowDto(row.Id, row.JobFamily, row.Level, row.SampleSize, row.P25, row.P50, row.P75, row.P90, row.DesignationId, row.GradeId);
    }

    public async Task<PayBenchmarkSetDto> RetireAsync(CompCaller caller, Guid setId, CancellationToken ct)
    {
        RequireManage(caller);
        var set = await db.PayBenchmarkSets.FirstOrDefaultAsync(s => s.TenantId == caller.TenantId && s.Id == setId, ct) ?? throw new CompNotFoundException("Benchmark set not found.");
        if (set.IsActive) { set.IsActive = false; set.RetiredAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct); }
        await audit.WriteAsync("compensation.benchmark.retired", nameof(PayBenchmarkSet), set.Id.ToString(), caller.Context, null, ct);
        return Dto(set, await db.PayBenchmarkRows.CountAsync(r => r.SetId == set.Id && (r.DesignationId != null || r.GradeId != null), ct));
    }

    private static void RequireManage(CompCaller caller)
    {
        if (!caller.Can(CompPermissions.BenchmarksManage)) throw new CompForbiddenException("forbidden", "Importing and mapping benchmarks needs the benchmark manager permission.");
    }

    private static PayBenchmarkSetDto Dto(PayBenchmarkSet s, int mapped) => new(s.Id, s.CompanyId, s.Name, s.Provider, s.LicenceStatement, s.SurveyDate, s.Geography,
        s.Currency, s.PayBasis, s.IsSynthetic, MeritBasisLoader.IsStale(s.SurveyDate, DateOnly.FromDateTime(DateTime.UtcNow)), s.SourceFileName, s.SourceFileSha256,
        s.RowCount, s.IsActive, s.ImportedByName, s.ImportedAtUtc, mapped);

    // ── readers ──

    /// <summary>Rows keyed by lower-case header, plus whether the file declares itself synthetic. Lines (CSV) or first-column
    /// cells (XLSX) starting with '#' are comments.</summary>
    internal static (List<Dictionary<string, string>> Rows, bool Synthetic) Read(string fileName, byte[] content)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        List<List<string>> table;
        if (ext == ".xlsx") table = ReadXlsx(content);
        else if (ext is ".csv" or ".txt" or "")
        {
            var text = Encoding.UTF8.GetString(content).TrimStart('﻿');
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var synthetic = lines.Any(l => l.TrimStart().StartsWith('#') && l.Contains(SyntheticMarker, StringComparison.OrdinalIgnoreCase));
            var data = string.Join('\n', lines.Where(l => !l.TrimStart().StartsWith('#')));
            var parsed = Csv.Parse(data).Select(r => r.ToDictionary(kv => kv.Key.Trim().ToLowerInvariant(), kv => kv.Value, StringComparer.OrdinalIgnoreCase)).ToList();
            return (parsed, synthetic);
        }
        else throw new InvalidDataException("Upload a .csv or .xlsx file.");

        var marker = table.Any(r => r.Count > 0 && r[0].TrimStart().StartsWith('#') && string.Join(' ', r).Contains(SyntheticMarker, StringComparison.OrdinalIgnoreCase));
        var body = table.Where(r => r.Count > 0 && !r[0].TrimStart().StartsWith('#') && r.Any(c => c.Trim().Length > 0)).ToList();
        if (body.Count == 0) return ([], marker);
        var headers = body[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var rows = body.Skip(1).Select(r => headers.Select((h, i) => (h, v: i < r.Count ? r[i] : "")).ToDictionary(x => x.h, x => x.v, StringComparer.OrdinalIgnoreCase)).ToList();
        return (rows, marker);
    }

    /// <summary>Most columns a benchmark sheet may use (the template has 7, plus 2 optional).</summary>
    public const int MaxColumns = 16;
    /// <summary>Cap on any decompressed XML part (a zip bomb inflates a small upload into gigabytes).</summary>
    public const long MaxCharactersInPart = 2_000_000;

    /// <summary>
    /// Reads the first sheet with a SAX reader: parts are capped when decompressed, a cell beyond column P refuses the file
    /// (no padded XFD1 sheets), and reading stops as soon as there are more than <see cref="MaxRows"/> data rows.
    /// </summary>
    private static List<List<string>> ReadXlsx(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var doc = SpreadsheetDocument.Open(stream, false, new OpenSettings { MaxCharactersInPart = MaxCharactersInPart });
        var wb = doc.WorkbookPart ?? throw new InvalidDataException("The workbook has no sheets.");
        var sheet = wb.Workbook.Sheets?.Elements<Sheet>().FirstOrDefault() ?? throw new InvalidDataException("The workbook has no sheets.");
        var part = (WorksheetPart)wb.GetPartById(sheet.Id!);
        var shared = wb.SharedStringTablePart?.SharedStringTable.Elements<SharedStringItem>().Select(s => s.InnerText).ToList() ?? [];
        var result = new List<List<string>>();
        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement) continue;
            var row = (Row)reader.LoadCurrentElement()!;
            var cells = new List<string>();
            foreach (var cell in row.Elements<Cell>())
            {
                var index = ColumnIndex(cell.CellReference?.Value, cells.Count);
                if (index >= MaxColumns) throw new InvalidDataException($"The sheet uses column {cell.CellReference?.Value}; a benchmark file has at most {MaxColumns} columns.");
                while (cells.Count < index) cells.Add("");
                var raw = cell.CellValue?.InnerText ?? cell.InlineString?.InnerText ?? "";
                cells.Add(cell.DataType?.Value == CellValues.SharedString && int.TryParse(raw, out var si) && si >= 0 && si < shared.Count ? shared[si] : raw);
            }
            result.Add(cells);
            if (result.Count > MaxRows + 50) throw new InvalidDataException($"At most {MaxRows} rows per file.");
        }
        return result;
    }

    private static int ColumnIndex(string? reference, int fallback)
    {
        if (string.IsNullOrEmpty(reference)) return fallback;
        var n = 0;
        foreach (var ch in reference.TakeWhile(char.IsLetter))
        {
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            if (n > 16_384) break;
        }
        return Math.Max(0, n - 1);
    }
}
