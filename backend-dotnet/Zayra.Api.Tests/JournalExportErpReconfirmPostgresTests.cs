using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostExportHarness;

namespace Zayra.Api.Tests;

/// <summary>
/// Final ledger review follow-up: re-confirming a ledger row the ERP already holds under one document number with a DIFFERENT number is refused by the
/// ledger's own evidence trigger (M2) at commit. The endpoints answer it as a clean 409 with an English and an Arabic message, not a raw database error,
/// and nothing is changed.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class JournalExportErpReconfirmPostgresTests
{
    private readonly PostgresFixture _fx;
    public JournalExportErpReconfirmPostgresTests(PostgresFixture fx) => _fx = fx;

    private static ClaimsPrincipal Caller(Guid tenantId, params string[] permissions) => new(new ClaimsIdentity(new[]
    {
        new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.Name, "Caller"), new Claim("is_group_scope", "true"),
    }.Concat(permissions.Select(p => new Claim("permission", p))), "Test"));

    private async Task<(ZayraDbContext Db, Guid TenantId, ClaimsPrincipal Checker, Guid ExportId)> WorldAsync()
    {
        var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        db.Companies.Add(new Company { Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Alpha", RegistrationNumber = "R-" + Guid.NewGuid().ToString("N")[..8], ErpCompanyCode = "SA01", DefaultCurrency = "SAR" });
        await db.SaveChangesAsync();
        var company = await db.Companies.AsNoTracking().SingleAsync(c => c.TenantId == tenantId);
        foreach (var e in new[] { Row(tenantId, "Accrual", "5000 - Salaries", "", 10m, company: company.Id), Row(tenantId, "Accrual", "", "2100 - Salaries Payable", 10m, company: company.Id) })
            db.FinanceGlEntries.Add(e);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var maker = Caller(tenantId, "finance.gl.read", "finance.gl.manage", "finance.gl.export");
        var checker = Caller(tenantId, "finance.gl.read", "finance.erp.confirm");
        var created = await Ctrl(db, maker).Create(Period, null, null, "SAR", false, false, "generic-csv", default);
        db.ChangeTracker.Clear();
        ((ObjectResult)created).StatusCode.Should().Be(201, JsonSerializer.Serialize(((ObjectResult)created).Value));
        return (db, tenantId, checker, IdOf(created));
    }

    private static async Task AssertCleanConflict(IActionResult result)
    {
        await Task.CompletedTask;
        var obj = result.Should().BeOfType<ConflictObjectResult>().Subject;
        var body = JsonSerializer.SerializeToElement(obj.Value);
        body.GetProperty("error").GetString().Should().Be("erp_document_conflict");
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        JsonSerializer.Serialize(obj.Value).Should().NotContain("finance_gl_entries", "no database text reaches the caller");
    }

    [Fact]
    public async Task ConfirmingWholeFile_WithADifferentNumber_ForRowsTheErpHolds_IsA409()
    {
        var (db, tenantId, checker, exportId) = await WorldAsync();
        (await Ctrl(db, checker).Confirm(exportId, new ErpImportConfirmationRequest("ERP-A", null, null), default)).Should().BeOfType<OkObjectResult>();
        // The export row is reset out of band (support repair); its ledger rows stay Posted under ERP-A.
        await db.Database.ExecuteSqlRawAsync($"UPDATE gl_journal_exports SET status = 'Exported', erp_document_number = NULL WHERE id = '{exportId}'");
        db.ChangeTracker.Clear();

        await AssertCleanConflict(await Ctrl(db, checker).Confirm(exportId, new ErpImportConfirmationRequest("ERP-B", null, null), default));

        db.ChangeTracker.Clear();
        (await db.FinanceGlEntries.AsNoTracking().Where(e => e.TenantId == tenantId).Select(e => e.ErpDocumentNumber).Distinct().ToListAsync()).Should().Equal("ERP-A");
    }

    [Fact]
    public async Task ConfirmingAJournal_WithADifferentNumber_ForRowsTheErpHolds_IsA409()
    {
        var (db, tenantId, checker, exportId) = await WorldAsync();
        db.ChangeTracker.Clear();
        var refs = JsonSerializer.SerializeToElement(((ObjectResult)await Ctrl(db, checker).Get(exportId, default)).Value)
            .GetProperty("journals").EnumerateArray().Select(j => j.GetProperty("journalRef").GetString()!).ToList();
        (await Ctrl(db, checker).ConfirmJournals(exportId, new ErpJournalConfirmationRequest(refs.Select(r => new ErpJournalConfirmationItem(r, "ERP-A", null)).ToList()), default))
            .Should().BeOfType<OkObjectResult>();
        await db.Database.ExecuteSqlRawAsync($"UPDATE gl_journal_exports SET status = 'Exported', journal_confirmations_json = '[]'::json WHERE id = '{exportId}'");
        db.ChangeTracker.Clear();

        await AssertCleanConflict(await Ctrl(db, checker).ConfirmJournals(exportId,
            new ErpJournalConfirmationRequest(refs.Select(r => new ErpJournalConfirmationItem(r, "ERP-B", null)).ToList()), default));

        db.ChangeTracker.Clear();
        (await db.FinanceGlEntries.AsNoTracking().Where(e => e.TenantId == tenantId).Select(e => e.ErpDocumentNumber).Distinct().ToListAsync()).Should().Equal("ERP-A");
    }
}
