using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Migrations;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// ERP decisions on REAL Postgres, where the row locks and the partial unique indexes exist.
///
/// <para>THE DEFECT. Per-journal confirm/reject read JournalConfirmationsJson, appended to it and wrote the whole value
/// back with no lock and no concurrency token. Two checkers confirming journals 1 and 2 of one file at the same moment
/// both returned 200, and the second save erased the first confirmation. The double-post guard reads only that JSON, so
/// the next supplementary export re-sent journal 1 — which the ERP had already booked. Now every decision loads the
/// export row FOR UPDATE inside one transaction, so the second waits for the first and appends to what it wrote.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class JournalExportDecisionConcurrencyPostgresTests
{
    private readonly PostgresFixture _fx;
    public JournalExportDecisionConcurrencyPostgresTests(PostgresFixture fx) => _fx = fx;

    private const int Rounds = 5;

    [Fact]
    public async Task TwoSimultaneousPerJournalConfirmations_AreBothRecorded()
    {
        var tenantId = await SeedTenantAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var period = $"2023-{round + 1:D2}";
            await SeedLedgerAsync(tenantId, period, null);
            var exportId = IdOf(await CreateAsync(tenantId, period, Group(tenantId, "finance.gl.manage")));
            List<string> journals;
            await using (var db = _fx.CreateDb())
                journals = Body(await Controller(db, Group(tenantId, "finance.gl.read", GlJournalExportsController.ErpConfirmPermission)).Get(exportId, default))
                    .GetProperty("journals").EnumerateArray().Select(j => j.GetProperty("journalRef").GetString()!).ToList();
            journals.Should().HaveCount(2);

            var results = await RaceAsync(
                () => ConfirmJournalAsync(tenantId, exportId, journals[0], $"ERP-A-{round}"),
                () => ConfirmJournalAsync(tenantId, exportId, journals[1], $"ERP-B-{round}"));

            results.Should().OnlyContain(r => r != null && StatusOf(r) == 200, $"round {round}: both checkers record a different journal");
            await using var check = _fx.CreateDb();
            var export = await check.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == exportId);
            var decisions = JournalExportBuilder.ParseConfirmations(export.JournalConfirmationsJson);
            decisions.Select(d => d.DocumentRef).Should().BeEquivalentTo(journals, $"round {round}: neither confirmation may be lost");
            export.ConfirmedJournalCount.Should().Be(2);
            export.Status.Should().Be(GlJournalExportStatuses.Confirmed);
        }
    }

    [Fact]
    public async Task ACompanyScopedReExport_WhileAGroupExportCarryingUnattributedRowsIsLive_GetsAPrecise409()
    {
        var tenantId = await SeedTenantAsync();
        var companyX = await SeedCompanyAsync(tenantId, "Alpha KSA");
        const string period = "2023-09";
        await SeedLedgerAsync(tenantId, period, companyX);
        await SeedLedgerAsync(tenantId, period, null); // unattributed rows only a group user may fold in

        IActionResult groupExport;
        await using (var db = _fx.CreateDb())
            groupExport = await Controller(db, Group(tenantId, "finance.gl.manage"))
                .Create(period, null, companyX, "SAR", true, false, "generic-csv", default);
        StatusOf(groupExport).Should().Be(201);

        IActionResult companyExport;
        await using (var db = _fx.CreateDb())
            companyExport = await Controller(db, CompanyScoped(tenantId, companyX, "finance.gl.manage"))
                .Create(period, null, companyX, "SAR", false, false, "generic-csv", default);

        StatusOf(companyExport).Should().Be(409);
        Body(companyExport).GetProperty("error").GetString().Should().Be(JournalExportErrors.GroupExportLive,
            "not the misleading 'created at the same moment' of the live-scope index");
        Body(companyExport).GetProperty("message").GetString().Should().Contain("A group finance user must record that result");
        await using var check = _fx.CreateDb();
        (await check.GlJournalExports.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => x.Status).ToListAsync())
            .Should().Equal(GlJournalExportStatuses.Exported);
    }

    [Fact]
    public async Task TheErpCompanyCodeIndex_RefusesACaseVariantDuplicate_ButNotADeletedCompanysCode()
    {
        await using var db = _fx.CreateDb();
        await db.Database.OpenConnectionAsync();
        await using (var ddl = db.Database.GetDbConnection().CreateCommand())
        {
            // The fixture builds the schema from the model; this is the migration's own DDL (IF NOT EXISTS).
            ddl.CommandText = AddErpCodesAndLiveExportIndex.CreateErpCompanyCodeIndex;
            await ddl.ExecuteNonQueryAsync();
        }
        var tenantId = await SeedTenantAsync();
        var deleted = await SeedCompanyAsync(tenantId, "Old Co", "SA01", isDeleted: true);
        _ = deleted;
        await SeedCompanyAsync(tenantId, "Alpha KSA", "SA01");

        var duplicate = async () => await SeedCompanyAsync(tenantId, "Beta Services", "sa01");

        var ex = (await duplicate.Should().ThrowAsync<DbUpdateException>()).Which;
        var pg = ex.InnerException.Should().BeOfType<Npgsql.PostgresException>().Subject;
        pg.SqlState.Should().Be(Npgsql.PostgresErrorCodes.UniqueViolation);
        pg.ConstraintName.Should().Be(AddErpCodesAndLiveExportIndex.ErpCompanyCodeIndexName,
            "OrganizationSetupService maps exactly this constraint to a 409");
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────────

    private async Task<IActionResult> CreateAsync(Guid tenantId, string period, ClaimsPrincipal caller)
    {
        await using var db = _fx.CreateDb();
        return await Controller(db, caller).Create(period, null, null, "SAR", false, false, "generic-csv", default);
    }

    private async Task<IActionResult> ConfirmJournalAsync(Guid tenantId, Guid exportId, string journal, string reference)
    {
        await using var db = _fx.CreateDb();
        return await Controller(db, Group(tenantId, "finance.gl.read", GlJournalExportsController.ErpConfirmPermission))
            .ConfirmJournals(exportId, new ErpJournalConfirmationRequest(new() { new(journal, reference) }), default);
    }

    private static GlJournalExportsController Controller(ZayraDbContext db, ClaimsPrincipal caller)
    {
        var exports = new JournalExportService(db, JournalExportFormatterGoldenTests.AllFormatters());
        return new GlJournalExportsController(db, exports, new PeriodHandoffReconciler(db, exports))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = caller } },
        };
    }

    private async Task<Guid> SeedTenantAsync()
    {
        await using var db = _fx.CreateDb();
        return await PostgresFixture.SeedMinimalTenant(db);
    }

    private async Task<Guid> SeedCompanyAsync(Guid tenantId, string name, string? erpCode = null, bool isDeleted = false)
    {
        await using var db = _fx.CreateDb();
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = name, TradeName = name, CountryCode = "SA", Jurisdiction = "SA",
            RegistrationNumber = $"RC-{Guid.NewGuid():N}"[..20], DefaultCurrency = "SAR", IsActive = true,
            ErpCompanyCode = erpCode, IsDeleted = isDeleted,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company.Id;
    }

    /// <summary>One payroll journal (two one-sided rows) and one loan journal (two-sided) for the period.</summary>
    private async Task SeedLedgerAsync(Guid tenantId, string period, Guid? companyId)
    {
        await using var db = _fx.CreateDb();
        var run = Guid.NewGuid();
        db.FinanceGlEntries.AddRange(
            Row(tenantId, companyId, period, "Payroll", "5000 - Salaries", "", 900m, run),
            Row(tenantId, companyId, period, "Payroll", "", "2100 - Payable", 900m, run),
            Row(tenantId, companyId, period, "Loan", "1400 - Loans", "1000 - Bank", 250m));
        await db.SaveChangesAsync();
    }

    private static FinanceGlEntry Row(Guid tenantId, Guid? companyId, string period, string module, string dr, string cr, decimal amount, Guid? source = null) => new()
    {
        TenantId = tenantId, CompanyId = companyId, SourceModule = module, SourceEntityId = source ?? Guid.NewGuid(), SourceEntityRef = module,
        EventType = module == "Loan" ? "Disbursement" : "Accrual", DebitAccount = dr, CreditAccount = cr, Amount = amount,
        Currency = "SAR", EntryDate = DateOnly.ParseExact(period + "-15", "yyyy-MM-dd"), Period = period, Description = module,
    };

    private static ClaimsPrincipal Group(Guid tenantId, params string[] permissions) => Principal(tenantId, null, permissions);
    private static ClaimsPrincipal CompanyScoped(Guid tenantId, Guid company, params string[] permissions) => Principal(tenantId, company, permissions);

    /// <summary>A fresh user id per principal, so the maker is never the checker (maker/checker by identity).</summary>
    private static ClaimsPrincipal Principal(Guid tenantId, Guid? company, string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "GL Racer"),
        };
        if (company is Guid c)
            claims.Add(new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { c.ToString() } })));
        else
            claims.Add(new Claim("is_group_scope", "true"));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static async Task<IActionResult?[]> RaceAsync(Func<Task<IActionResult>> first, Func<Task<IActionResult>> second)
    {
        var ready = new SemaphoreSlim(0, 2);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IActionResult?> Run(Func<Task<IActionResult>> contender)
        {
            await Task.Yield();
            ready.Release();
            await go.Task;
            try { return await contender(); }
            catch { return null; }
        }
        var a = Task.Run(() => Run(first));
        var b = Task.Run(() => Run(second));
        await ready.WaitAsync();
        await ready.WaitAsync();
        go.SetResult();
        return await Task.WhenAll(a, b);
    }

    private static int StatusOf(IActionResult r) => r switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        ForbidResult => 403,
        _ => throw new InvalidOperationException(r.GetType().Name),
    };

    private static JsonElement Body(IActionResult r) => JsonSerializer.SerializeToElement(((ObjectResult)r).Value);
    private static Guid IdOf(IActionResult r) => Body(r).GetProperty("exportId").GetGuid();
}
