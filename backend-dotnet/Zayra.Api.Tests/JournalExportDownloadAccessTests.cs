using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostExportHarness;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-D slice 3: who may take the file and what is recorded (T-061, T-065, T-066, Int R20/R21, Acc R2/R13), and the
/// separation-of-duties seam on export creation (AM-14, S-004). The rule WP-B owns (who changed the costing set-up) is a
/// fake here; what is tested is that the export door asks it and refuses on its answer.
/// </summary>
public class JournalExportDownloadAccessTests
{
    private static Guid ActorOf(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static GlJournalExportsController CtrlWithIp(ZayraDbContext db, ClaimsPrincipal caller, IJournalExportSeparationOfDuties? sod = null)
    {
        var exports = new JournalExportService(db, JournalExportFormatterGoldenTests.AllFormatters());
        var ctrl = new GlJournalExportsController(db, exports, new PeriodHandoffReconciler(db, exports), sod);
        var http = new DefaultHttpContext { User = caller };
        http.Connection.RemoteIpAddress = IPAddress.Parse("10.1.2.3");
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    private static async Task<(ZayraDbContext Db, Guid TenantId, Guid ExportId)> ExportedAsync(bool costed = true)
    {
        var (db, tenantId) = await TenantAsync(costAccounting: true);
        SeedChart(db, tenantId);
        db.Add(Row(tenantId, "Accrual", "5000 - Salaries", "", 10_000m, "July payroll", ccCode: costed ? "CC100" : null,
            ccName: costed ? "Operations" : null));
        db.Add(Row(tenantId, "Accrual", "", "2100 - Salaries Payable", 10_000m, "July payroll"));
        db.SaveChanges();
        var id = IdOf(await CreateAsync(db, await CallerAsync(db, tenantId, "Payroll Manager")));
        return (db, tenantId, id);
    }

    private static ClaimsPrincipal OnlyPermissions(Guid tenantId, params string[] permissions) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "Reader"), new Claim("is_group_scope", "true"),
        }.Concat(permissions.Select(p => new Claim("permission", p))), "Test"));

    // ── Download (T-061) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AReadOnlyUser_GetsMetadataAndTotals_ButCannotDownloadOrSeeLines()
    {
        var (db, tenantId, id) = await ExportedAsync();
        var reader = OnlyPermissions(tenantId, "finance.gl.read");

        StatusOf(await CtrlWithIp(db, reader).Download(id, default)).Should().Be(403);
        (await db.AuditLogs.AnyAsync(a => a.Action == "finance.gl.journal_export.downloaded")).Should().BeFalse("a refused download is not a download");

        var get = await CtrlWithIp(db, reader).Get(id, default);
        var body = Body(get);
        body.GetProperty("export").GetProperty("TotalDebits").GetDecimal().Should().Be(10_000m, "totals stay visible to a reader");
        body.GetProperty("lines").ValueKind.Should().Be(JsonValueKind.Null, "line detail (descriptions, cost centres) is for export holders");

        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        Body(await CtrlWithIp(db, maker).Get(id, default)).GetProperty("lines").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task AnExportHolder_Downloads_AndEveryDownloadIsAudited_WithWhoWhatAndHowMuch()
    {
        var (db, tenantId, id) = await ExportedAsync();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var export = await db.GlJournalExports.AsNoTracking().SingleAsync();

        var first = await CtrlWithIp(db, maker).Download(id, default);
        var second = await CtrlWithIp(db, maker).Download(id, default);

        first.Should().BeOfType<FileContentResult>();
        var rows = await db.AuditLogs.AsNoTracking().Where(a => a.Action == "finance.gl.journal_export.downloaded").ToListAsync();
        rows.Should().HaveCount(2, "two downloads, two rows");
        foreach (var row in rows)
        {
            row.EntityId.Should().Be(id.ToString());
            row.UserId.Should().Be(ActorOf(maker));
            row.CompanyId.Should().Be(export.CompanyId);
            row.IpAddress.Should().Be("10.1.2.3");
            var meta = JsonSerializer.Deserialize<JsonElement>(row.Metadata!);
            meta.GetProperty("fileHash").GetString().Should().Be(export.FileHash);
            meta.GetProperty("byteLength").GetInt32().Should().Be(((FileContentResult)first).FileContents.Length);
            meta.GetProperty("actorKind").GetString().Should().Be("user");
            meta.GetProperty("FileName").GetString().Should().Be(export.FileName);
        }
        ((FileContentResult)second).FileContents.Should().Equal(((FileContentResult)first).FileContents);
    }

    [Fact]
    public async Task AnErpApiKeyWithTheReadScope_CanPullTheFile_AndTheAuditNamesTheKey()
    {
        var (db, tenantId, id) = await ExportedAsync();
        var key = new ErpApiKey
        {
            TenantId = tenantId, Name = "SAP pull", Prefix = "kx_live_ab", Scopes = ErpApiKeyScopes.ExportsRead,
            CreatedByUserId = Guid.NewGuid(), CompanyId = null,
        };

        var result = await CtrlWithIp(db, ErpApiKeyPrincipal.Build(key)).Download(id, default);

        result.Should().BeOfType<FileContentResult>("the key's scope is the explicit grant to pull files");
        var meta = JsonSerializer.Deserialize<JsonElement>((await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == "finance.gl.journal_export.downloaded")).Metadata!);
        meta.GetProperty("actorKind").GetString().Should().Be("api_key");
        meta.GetProperty("apiKeyId").GetGuid().Should().Be(key.Id);
        meta.GetProperty("apiKeyCreatedBy").GetGuid().Should().Be(key.CreatedByUserId);
    }

    [Fact]
    public async Task ACompanyScopedExporter_CannotDownloadAnotherCompanysExport_AndAnotherTenantSeesNothing()
    {
        var (db, tenantId, id) = await ExportedAsync();
        var otherCompany = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("permission", "finance.gl.manage"), new Claim("permission", "finance.gl.read"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { CompanyB.ToString() } })),
        }, "Test"));

        StatusOf(await CtrlWithIp(db, otherCompany).Download(id, default)).Should().Be(403);

        var (otherDb, otherTenant) = await TenantAsync();
        // Same database, different tenant claim: the export does not exist for them.
        var stranger = await CallerAsync(db, tenantId, "Payroll Manager");
        var strangerOfAnotherTenant = new ClaimsPrincipal(new ClaimsIdentity(
            stranger.Claims.Where(c => c.Type != "tenant_id").Append(new Claim("tenant_id", otherTenant.ToString())), "Test"));
        (await CtrlWithIp(db, strangerOfAnotherTenant).Download(id, default)).Should().BeOfType<NotFoundResult>();
        (await CtrlWithIp(db, strangerOfAnotherTenant).Get(id, default)).Should().BeOfType<NotFoundResult>();
        (await db.AuditLogs.AnyAsync(a => a.Action == "finance.gl.journal_export.downloaded")).Should().BeFalse();
        otherDb.Should().NotBeNull();
    }

    [Fact]
    public async Task Preview_ReturnsLineSamples_OnlyToExportHolders()
    {
        var (db, tenantId, _) = await ExportedAsync();
        // a fresh month so the preview has rows to show
        db.ChangeTracker.Clear();
        var reader = OnlyPermissions(tenantId, "finance.gl.read");
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");

        var asReader = Body(await CtrlWithIp(db, reader).Preview(Period, null, null, "SAR", false, false, "xero-manual-journal"));
        var asMaker = Body(await CtrlWithIp(db, maker).Preview(Period, null, null, "SAR", false, false, "xero-manual-journal"));

        asReader.GetProperty("sample").ValueKind.Should().Be(JsonValueKind.Null);
        asReader.GetProperty("lineCount").GetInt32().Should().BeGreaterThan(0, "counts and totals remain visible");
        asMaker.GetProperty("sample").GetArrayLength().Should().BeGreaterThan(0);
        asMaker.GetProperty("hasCostCentres").GetBoolean().Should().BeTrue();
    }

    // ── Separation of duties on export creation (AM-14, S-004) ───────────────────────────────────

    private sealed class FakeSod : IJournalExportSeparationOfDuties
    {
        private readonly Guid _barred;
        public List<JournalExportSodRequest> Seen { get; } = new();
        public FakeSod(Guid barred) => _barred = barred;

        public Task<bool> IsBarredAsync(JournalExportSodRequest request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(request.ActorUserId == _barred);
        }
    }

    [Fact]
    public async Task AUserWhoChangedTheCostingSetup_CannotCreateTheExport_EvenAnAdminHoldingEveryPermission()
    {
        var (db, tenantId) = await TenantAsync(costAccounting: true);
        SeedChart(db, tenantId);
        db.Add(Row(tenantId, "Accrual", "5000 - Salaries", "", 10m, ccCode: "CC100", ccName: "Ops"));
        db.Add(Row(tenantId, "Accrual", "", "2100 - Salaries Payable", 10m));
        db.SaveChanges();
        var admin = await CallerAsync(db, tenantId, "Admin");
        var sod = new FakeSod(ActorOf(admin));

        var refused = await CtrlWithIp(db, admin, sod).Create(Period, null, null, "SAR", false, false, "generic-csv", default);

        StatusOf(refused).Should().Be(403);
        Body(refused).GetProperty("error").GetString().Should().Be("separation_of_duties");
        (await db.GlJournalExports.AnyAsync()).Should().BeFalse();
        (await db.FinanceGlEntries.AsNoTracking().AllAsync(e => e.ErpPostingStatus != ErpPostingStatuses.Exported))
            .Should().BeTrue("a refused export stamps nothing");
        (await db.AuditLogs.AnyAsync(a => a.Action == "finance.gl.journal_export.create_refused")).Should().BeTrue();
        sod.Seen.Single().Should().Match<JournalExportSodRequest>(r =>
            r.TenantId == tenantId && r.Period == Period && r.ActorUserId == ActorOf(admin));

        var colleague = await CallerAsync(db, tenantId, "Admin");
        StatusOf(await CtrlWithIp(db, colleague, sod).Create(Period, null, null, "SAR", false, false, "generic-csv", default)).Should().Be(201);
    }

    [Fact]
    public async Task WithNoSodRegistered_NothingChanges()
    {
        var (db, tenantId) = await TenantAsync();
        db.Add(Row(tenantId, "Accrual", "5000 - Salaries", "", 10m));
        db.Add(Row(tenantId, "Accrual", "", "2100 - Salaries Payable", 10m));
        db.SaveChanges();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");

        StatusOf(await CtrlWithIp(db, maker, sod: null).Create(Period, null, null, "SAR", false, false, "generic-csv", default))
            .Should().Be(201, "the seam is optional until the cost-accounting rule is registered");
    }
}
