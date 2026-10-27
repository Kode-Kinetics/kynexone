using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostExportHarness;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests;

/// <summary>Recording the ERP result of a whole file refuses over-long text with a bilingual 422, never a 500.</summary>
public class ErpDecisionInputLimitsTests
{
    private static async Task<(Zayra.Api.Data.ZayraDbContext Db, Guid TenantId, Guid ExportId, System.Security.Claims.ClaimsPrincipal Checker)> ArrangeAsync()
    {
        var (db, tenantId) = await TenantAsync(costAccounting: false);
        SeedChart(db, tenantId);
        db.Add(Row(tenantId, "Accrual", "5000 - Salaries", "", 10_000m));
        db.Add(Row(tenantId, "Accrual", "", "2100 - Salaries Payable", 10_000m));
        db.SaveChanges();
        var id = IdOf(await CreateAsync(db, await CallerAsync(db, tenantId, "Payroll Manager")));
        return (db, tenantId, id, await CallerAsync(db, tenantId, "Finance Approver"));
    }

    private static void ShouldBeBilingual422(IActionResult r)
    {
        StatusOf(r).Should().Be(422);
        var body = JsonSerializer.Serialize(((ObjectResult)r).Value);
        body.Should().Contain("\"message\"").And.Contain("\"messageAr\"");
    }

    [Fact]
    public async Task AnOverLongDocumentNumber_IsRefused_AndTheFileStaysUndecided()
    {
        var (db, _, id, checker) = await ArrangeAsync();

        ShouldBeBilingual422(await Ctrl(db, checker).Confirm(id, new ErpImportConfirmationRequest(new string('9', 121)), default));

        (await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == id)).Status.Should().Be(GlJournalExportStatuses.Exported);
    }

    [Fact]
    public async Task ADocumentNumberOfExactlyTheColumnLimit_IsAccepted()
    {
        var (db, _, id, checker) = await ArrangeAsync();

        StatusOf(await Ctrl(db, checker).Confirm(id, new ErpImportConfirmationRequest(new string('9', 120)), default)).Should().Be(200);
    }

    [Fact]
    public async Task OverLongNotes_AreRefused()
    {
        var (db, _, id, checker) = await ArrangeAsync();

        ShouldBeBilingual422(await Ctrl(db, checker).Confirm(id, new ErpImportConfirmationRequest("SAP-1", null, new string('n', 1001)), default));

        (await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == id)).Status.Should().Be(GlJournalExportStatuses.Exported);
    }

    [Fact]
    public async Task AnOverLongRejectionReason_IsRefused()
    {
        var (db, _, id, checker) = await ArrangeAsync();

        ShouldBeBilingual422(await Ctrl(db, checker).Reject(id, new ErpImportRejectionRequest(new string('r', 1001)), default));

        (await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == id)).Status.Should().Be(GlJournalExportStatuses.Exported);
    }
}
