using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.AdvanceTestKit;

namespace Zayra.Api.Tests;

/// <summary>LON-07 (detail redaction, list clamps, scoped audit) and LON-10 (payroll "Closed" counts as settled).</summary>
public class AdvanceReadScopeTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static async Task<Guid> SeedActiveWithEvidenceAsync(Zayra.Api.Data.ZayraDbContext db, Fx f, string status = "Active")
    {
        var id = await SeedPendingAsync(db, f, 2_000m);
        var adv = await db.SalaryAdvances.SingleAsync(x => x.Id == id);
        adv.Status = status; adv.ApprovedAmount = 2_000m; adv.OutstandingBalance = status == "Closed" ? 0m : 2_000m;
        adv.TotalRepaid = status == "Closed" ? 2_000m : 0m;
        db.AdvanceAuditLogs.Add(new AdvanceAuditLog
        {
            TenantId = f.TenantId, AdvanceId = id, Action = "AdvanceApproved",
            OldValuesJson = "{\"Status\":\"Pending\"}", NewValuesJson = "{\"ApprovedAmount\":2000}",
        });
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = f.TenantId, SourceModule = "Advance", SourceEntityId = id, SourceEntityRef = adv.AdvanceNumber,
            EventType = "Disbursement", DebitAccount = "1410", CreditAccount = "1000", Amount = 2_000m, Currency = "SAR",
            EntryDate = DateOnly.FromDateTime(DateTime.UtcNow), Period = DateTime.UtcNow.ToString("yyyy-MM"),
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static JsonElement Body(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task Get_ByALineManagerWithLoansRead_HidesGlEntries_AndRedactsAuditValues()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var id = await SeedActiveWithEvidenceAsync(db, f);

        var result = await Advances(db, f, Guid.NewGuid(), new[] { "Manager" }, perms: new[] { "loans.read" }).Get(id, Ct);

        var body = Body(result);
        Assert.Equal(0, body.GetProperty("glEntries").GetArrayLength());
        var log = Assert.Single(body.GetProperty("auditLogs").EnumerateArray().ToList());
        Assert.Equal(string.Empty, log.GetProperty("NewValuesJson").GetString());
        Assert.Equal(string.Empty, log.GetProperty("OldValuesJson").GetString());
        // Stored evidence is not edited by the redaction.
        Assert.Contains("2000", (await db.AdvanceAuditLogs.AsNoTracking().SingleAsync()).NewValuesJson);
    }

    [Fact]
    public async Task Get_ByFinance_SeesGlEntriesAndAuditValues()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var id = await SeedActiveWithEvidenceAsync(db, f);

        var result = await Advances(db, f, Guid.NewGuid(), new[] { "Finance" }, perms: new[] { "loans.read" }).Get(id, Ct);

        var body = Body(result);
        Assert.Equal(1, body.GetProperty("glEntries").GetArrayLength());
        Assert.Contains("2000", body.GetProperty("auditLogs")[0].GetProperty("NewValuesJson").GetString());
    }

    [Fact]
    public async Task Get_ByAScopedUserOutsideTheirScope_IsForbidden()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var id = await SeedActiveWithEvidenceAsync(db, f);

        var result = await Advances(db, f, Guid.NewGuid(), new[] { "Finance" }, new FixedScope(new[] { f.EmployeeId + 999 }),
            new[] { "loans.read" }).Get(id, Ct);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task AuditReport_ForAScopedReader_ExcludesOutOfScopeAdvancesAndTheirJournals()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        await SeedActiveWithEvidenceAsync(db, f);

        var body = Body(await Advances(db, f, Guid.NewGuid(), new[] { "HR Manager" }, new FixedScope(new[] { f.EmployeeId + 999 }))
            .AuditReport(Ct));

        Assert.Equal(0, body.GetProperty("TotalAdvances").GetInt32());
        Assert.Equal(0, body.GetProperty("GlEntriesCount").GetInt32());
    }

    [Fact]
    public async Task AuditReport_CountsAPayrollClosedAdvanceAsSettled()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        await SeedActiveWithEvidenceAsync(db, f, status: "Closed");

        var body = Body(await Advances(db, f, Guid.NewGuid(), new[] { "Finance" }).AuditReport(Ct));

        Assert.Equal(1, body.GetProperty("SettledAdvances").GetInt32());
        Assert.Equal(0, body.GetProperty("ActiveAdvances").GetInt32());
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(1, 100_000)]
    public async Task List_ClampsPageAndPageSize(int page, int pageSize)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        for (var i = 0; i < 3; i++) await SeedPendingAsync(db, f, 1_000m);

        var body = Body(await Advances(db, f, Guid.NewGuid(), new[] { "Finance" }, perms: new[] { "loans.read" })
            .List(null, null, page, pageSize, Ct));

        Assert.Equal(3, body.GetProperty("total").GetInt32());
        Assert.InRange(body.GetProperty("items").GetArrayLength(), 1, 200);
    }
}
