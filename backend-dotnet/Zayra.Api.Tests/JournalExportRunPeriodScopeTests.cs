using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;
using Zayra.Api.Infrastructure.Finance;
using static Zayra.Api.Tests.CostAccountingKit;

namespace Zayra.Api.Tests;

/// <summary>
/// The period and company a journal export is checked and built for come from the payroll run when a run id is given.
/// A client period that disagrees with the run is refused; the separation-of-duties check sees the period exported.
/// </summary>
public class JournalExportRunPeriodScopeTests
{
    private static async Task<(ZayraDbContext Db, System.Data.Common.DbConnection Conn, Guid Tid, Company Co, PayrollRun Run, Guid User)> WorldAsync()
    {
        var (db, conn) = NewDb();
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "T", Slug = $"t-{tid:N}"[..12], IsActive = true });
        await db.SaveChangesAsync();
        var co = await AddCompany(db, tid, enabled: true);
        var user = Guid.NewGuid();
        // The user changed the set-up first; the September run was locked afterwards.
        db.AuditLogs.Add(new AuditLog { TenantId = tid, CompanyId = co.Id, UserId = user, Action = "finance.gl.mapping.saved", EntityName = "GlMapping", CreatedAtUtc = DateTime.UtcNow.AddHours(-2) });
        await db.SaveChangesAsync();
        var run = await AddRun(db, tid, co.Id, "Locked", 2026, 9, lockedAt: DateTime.UtcNow.AddHours(-1));
        return (db, conn, tid, co, run, user);
    }

    private static GlJournalExportsController Ctrl(ZayraDbContext db, Guid tid, Guid user)
    {
        var exports = new JournalExportService(db, JournalExportFormatterGoldenTests.AllFormatters());
        var c = new GlJournalExportsController(db, exports, new PeriodHandoffReconciler(db, exports), new CostAccountingJournalExportSeparationOfDuties(db));
        return Attach(c, tid, new[] { "finance.gl.read", "finance.gl.manage", "finance.gl.export" }, null, user);
    }

    [Theory]
    [InlineData("2026-09")]
    [InlineData("2099-12")]
    [InlineData(null)]
    public async Task AUserWhoChangedTheSetup_IsRefused_ForTheRunsOwnPeriod_WhateverPeriodIsSent(string? sent)
    {
        var (db, conn, tid, co, run, user) = await WorldAsync(); await using var _ = conn; await using var __ = db;

        var result = await Ctrl(db, tid, user).Create(sent, run.Id, co.Id, "SAR", false, false, "generic-csv", default);

        // A period that disagrees with the run is refused as such (422); otherwise the SoD refusal (403) applies.
        Status(result).Should().Be(sent == "2099-12" ? 422 : 403);
        (await db.GlJournalExports.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task APeriodThatDoesNotMatchTheRun_IsRefusedWithBothLanguages()
    {
        var (db, conn, tid, co, run, _) = await WorldAsync(); await using var _c = conn; await using var __ = db;

        var result = await Ctrl(db, tid, Guid.NewGuid()).Create("2099-12", run.Id, co.Id, "SAR", false, false, "generic-csv", default);

        Status(result).Should().Be(422);
        var body = System.Text.Json.JsonSerializer.Serialize(((ObjectResult)result).Value);
        body.Should().Contain("\"message\"").And.Contain("\"messageAr\"");
        (await db.GlJournalExports.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task APeriodOnlyRequest_ChecksTheRealPeriod()
    {
        var (db, conn, tid, co, _, user) = await WorldAsync(); await using var _c = conn; await using var __ = db;

        var result = await Ctrl(db, tid, user).Create("2026-09", null, co.Id, "SAR", false, false, "generic-csv", default);

        Status(result).Should().Be(403);
    }

    [Fact]
    public async Task TheSodAdapter_UsesTheRunsPeriod_EvenWhenTheRequestCarriesAnotherOne()
    {
        var (db, conn, tid, co, run, user) = await WorldAsync(); await using var _c = conn; await using var __ = db;
        var sod = new CostAccountingJournalExportSeparationOfDuties(db);

        (await sod.IsBarredAsync(new JournalExportSodRequest(tid, co.Id, "2099-12", run.Id, user, null), default)).Should().BeTrue();
        (await sod.IsBarredAsync(new JournalExportSodRequest(tid, null, "2099-12", run.Id, user, null), default)).Should().BeTrue();
    }
}
