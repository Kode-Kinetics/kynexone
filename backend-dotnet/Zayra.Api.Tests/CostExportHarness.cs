using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests;

/// <summary>
/// Shared arrange helpers for the cost-centre / scale journal-export tests (WP-D). Mirrors the harness in
/// <c>JournalExportDoublePostTests</c> (EF in-memory, seeded role bundles, the real controller) without touching it.
/// </summary>
internal static class CostExportHarness
{
    public const string Period = "2026-07";
    public static readonly Guid CompanyA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    public static readonly Guid CompanyB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    public static async Task<(ZayraDbContext Db, Guid TenantId)> TenantAsync(
        string currency = "SAR", bool costAccounting = false, string? erpCode = "SA01")
    {
        var (db, tenantId) = await NewTenantAsync("gl-cc-export");
        db.Companies.Add(new Company
        {
            Id = CompanyA, TenantId = tenantId, LegalNameEn = "Alpha", RegistrationNumber = "7001234567",
            ErpCompanyCode = erpCode, DefaultCurrency = currency, CostAccountingEnabled = costAccounting,
        });
        // The ledger guard only accepts a cost centre that exists, is active and belongs to the posting company.
        db.CostCenters.Add(new CostCenter
        {
            Id = Cc100, TenantId = tenantId, CompanyId = CompanyA, Code = "CC100", Name = "Operations", GlSegment = "6100-OPS", IsActive = true,
        });
        await db.SaveChangesAsync();
        return (db, tenantId);
    }

    /// <summary>The chart the exporter classifies sides with: 5xxx Expense, 2xxx Liability, 1xxx Asset (tenant defaults).</summary>
    public static void SeedChart(ZayraDbContext db, Guid tenantId, string? rule5000 = null)
    {
        foreach (var (code, name, type) in new[]
        {
            ("5000", "Salaries", "Expense"), ("5200", "Bonus Expense", "Expense"), ("2100", "Salaries Payable", "Liability"),
            ("2300", "Bonus Payable", "Liability"), ("1400", "Employee Loans", "Asset"), ("1000", "Bank", "Asset"),
        })
            db.GlAccounts.Add(new GlAccount
            {
                TenantId = tenantId, Code = code, Name = name, AccountType = type,
                CostCenterRule = code == "5000" && rule5000 is not null ? rule5000 : "Optional",
            });
        db.SaveChanges();
    }

    public static FinanceGlEntry Row(
        Guid tenantId, string evt, string dr, string cr, decimal amount, string description = "July payroll",
        string currency = "SAR", string module = "Payroll", Guid? source = null, Guid? company = null,
        string? ccCode = null, string? ccName = null, string? ccSegment = null, Guid? reversalOf = null) => new()
    {
        TenantId = tenantId, CompanyId = company ?? CompanyA, SourceModule = module, SourceEntityId = source ?? RunId,
        SourceEntityRef = module + "-REF", EventType = evt, DebitAccount = dr, CreditAccount = cr, Amount = amount,
        Currency = currency, EntryDate = new DateOnly(2026, 8, 3), Period = Period, Description = description,
        CostCenterId = ccCode is null ? null : ccCode == "CC100" ? Cc100 : Guid.NewGuid(), CostCenterCode = ccCode, CostCenterName = ccName,
        CostCenterGlSegment = ccSegment, ReversalOfEntryId = reversalOf,
    };

    public static readonly Guid RunId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>Company A's seeded cost centre; ledger rows that name "CC100" point at it.</summary>
    public static readonly Guid Cc100 = Guid.Parse("cccccccc-0000-0000-0000-000000000100");

    public static GlJournalExportsController Ctrl(ZayraDbContext db, ClaimsPrincipal caller)
    {
        var exports = new JournalExportService(db, Zayra.Api.Tests.JournalExportFormatterGoldenTests.AllFormatters());
        return Bind(new GlJournalExportsController(db, exports, new PeriodHandoffReconciler(db, exports)), caller);
    }

    public static async Task<IActionResult> CreateAsync(
        ZayraDbContext db, ClaimsPrincipal maker, string format = "generic-csv", string? currency = "SAR")
    {
        var result = await Ctrl(db, maker).Create(Period, null, null, currency, false, false, format, default);
        db.ChangeTracker.Clear();
        return result;
    }

    public static JsonElement Body(IActionResult r) => JsonSerializer.SerializeToElement(((ObjectResult)r).Value);
    public static Guid IdOf(IActionResult r) => Body(r).GetProperty("exportId").GetGuid();

    public static async Task<byte[]> DownloadAsync(ZayraDbContext db, Guid tenantId, Guid exportId, string role = "Payroll Manager")
    {
        db.ChangeTracker.Clear();
        var file = (FileContentResult)await Ctrl(db, await CallerAsync(db, tenantId, role)).Download(exportId, default);
        return file.FileContents;
    }

    public static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    /// <summary>A formatter-ready document built the way the service builds one (Expand + Verify), single company.</summary>
    public static JournalExportDocument Doc(IReadOnlyList<FinanceGlEntry> entries, string currency = "SAR", string company = "SA01")
    {
        var lines = JournalExportBuilder.Expand(entries, company);
        return new JournalExportDocument
        {
            TenantId = Guid.Parse("99999999-9999-9999-9999-999999999999"), TenantName = string.Empty,
            CompanyId = CompanyA, CompanyCode = company, CompanyName = string.Empty, Period = Period, PayrollRunId = null,
            Currency = currency, GeneratedAtUtc = new DateTime(2026, 8, 3, 9, 30, 0, DateTimeKind.Utc),
            Lines = lines, ExportId = Zayra.Api.Tests.JournalExportFormatterGoldenTests.ExportId, Kind = JournalExportKinds.Full,
        };
    }

    /// <summary>RFC-4180 reader (quoted cells may hold commas, quotes and line breaks) — the strict re-parse the corpus tests need.</summary>
    public static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>(); }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}
