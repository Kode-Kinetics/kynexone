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
        string currency = "SAR", bool costAccounting = false)
    {
        var (db, tenantId) = await NewTenantAsync("gl-cc-export");
        db.Companies.Add(new Company
        {
            Id = CompanyA, TenantId = tenantId, LegalNameEn = "Alpha", RegistrationNumber = "7001234567",
            ErpCompanyCode = "SA01", DefaultCurrency = currency, CostAccountingEnabled = costAccounting,
        });
        await db.SaveChangesAsync();
        return (db, tenantId);
    }

    public static FinanceGlEntry Row(
        Guid tenantId, string evt, string dr, string cr, decimal amount, string description = "July payroll",
        string currency = "SAR", string module = "Payroll", Guid? source = null, Guid? company = null,
        string? ccCode = null, string? ccName = null, string? ccSegment = null, Guid? reversalOf = null) => new()
    {
        TenantId = tenantId, CompanyId = company ?? CompanyA, SourceModule = module, SourceEntityId = source ?? RunId,
        SourceEntityRef = module + "-REF", EventType = evt, DebitAccount = dr, CreditAccount = cr, Amount = amount,
        Currency = currency, EntryDate = new DateOnly(2026, 8, 3), Period = Period, Description = description,
        CostCenterId = ccCode is null ? null : Guid.NewGuid(), CostCenterCode = ccCode, CostCenterName = ccName,
        CostCenterGlSegment = ccSegment, ReversalOfEntryId = reversalOf,
    };

    public static readonly Guid RunId = Guid.Parse("11111111-2222-3333-4444-555555555555");

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
}
