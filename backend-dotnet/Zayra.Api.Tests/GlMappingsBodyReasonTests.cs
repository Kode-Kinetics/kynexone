using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Seed;
using static Zayra.Api.Tests.CostAccountingKit;

namespace Zayra.Api.Tests;

/// <summary>
/// The mapping-save reason travels in the JSON body (<c>{ "reason": "...", "mappings": [...] }</c>). The query-string reason and the legacy bare
/// array body keep working for one release; when both a body reason and a query reason are present, the body wins.
/// </summary>
public class GlMappingsBodyReasonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly Guid Acct = Guid.NewGuid();

    [Fact]
    public void ABareArrayBody_StillBinds_WithNoReason()
    {
        var body = JsonSerializer.Deserialize<GlMappingsBody>($$"""[{"driverKey":"EARN:BASIC","accountId":"{{Acct}}"}]""", Web)!;
        body.Reason.Should().BeNull();
        body.Mappings.Should().ContainSingle().Which.Should().Be(new GlMappingRequest("EARN:BASIC", Acct));
    }

    [Fact]
    public void AnObjectBody_BindsTheReasonAndTheMappings()
    {
        var body = JsonSerializer.Deserialize<GlMappingsBody>($$"""{"reason":" Quarterly review ","mappings":[{"driverKey":"EARN:BASIC","accountId":"{{Acct}}","functionalClass":"SELLING"}]}""", Web)!;
        body.Reason.Should().Be(" Quarterly review ");
        body.Mappings.Should().ContainSingle().Which.FunctionalClass.Should().Be("SELLING");
    }

    private static async Task<string?> SavedReason(ZayraDbContext db, Guid user) =>
        JsonDocument.Parse((await db.AuditLogs.AsNoTracking().SingleAsync(a => a.UserId == user && a.Action == "finance.gl.mapping.saved")).Metadata!)
            .RootElement.GetProperty("reason").GetString();

    [Fact]
    public async Task ABodyReason_SatisfiesTheReasonRequirement_AndIsAudited()
    {
        var (db0, conn) = NewDb(); await using var _ = conn; await using var __ = db0;
        var tid = Guid.NewGuid(); await GlDriverSeeder.SeedTenantDefaultsAsync(db0, tid, default); await db0.SaveChangesAsync();
        var co = await AddCompany(db0, tid, true); var a = await AddAccount(db0, tid, co.Id, "5001", "Expense");
        var user = Guid.NewGuid();

        var res = await Gl(db0, tid, ManagePerms, uid: user).SetMappings(co.Id,
            new() { Reason = " Reviewed with finance ", Mappings = { new GlMappingRequest("EARN:BASIC", a.Id) } }, default);

        Status(res).Should().Be(200);
        (await SavedReason(db0, user)).Should().Be("Reviewed with finance");
    }

    [Fact]
    public async Task WhenBothAreSent_TheBodyReasonWins_AndABlankBodyReasonFallsBackToTheQuery()
    {
        var (db0, conn) = NewDb(); await using var _ = conn; await using var __ = db0;
        var tid = Guid.NewGuid(); await GlDriverSeeder.SeedTenantDefaultsAsync(db0, tid, default); await db0.SaveChangesAsync();
        var co = await AddCompany(db0, tid, true); var a = await AddAccount(db0, tid, co.Id, "5001", "Expense");
        var bodyWins = Guid.NewGuid(); var fallback = Guid.NewGuid();

        Status(await Gl(db0, tid, ManagePerms, uid: bodyWins).SetMappings(co.Id,
            new() { Reason = "from body", Mappings = { new GlMappingRequest("EARN:BASIC", a.Id) } }, default, "from query")).Should().Be(200);
        Status(await Gl(db0, tid, ManagePerms, uid: fallback).SetMappings(co.Id,
            new() { Reason = "   ", Mappings = { new GlMappingRequest("EARN:BASIC", a.Id), new GlMappingRequest("EARN:HOUSING", a.Id) } }, default, "from query")).Should().Be(200);

        (await SavedReason(db0, bodyWins)).Should().Be("from body");
        (await SavedReason(db0, fallback)).Should().Be("from query");
    }

    [Fact]
    public async Task NoReasonAnywhere_WithCostingOn_IsStillRefused()
    {
        var (db0, conn) = NewDb(); await using var _ = conn; await using var __ = db0;
        var tid = Guid.NewGuid(); await GlDriverSeeder.SeedTenantDefaultsAsync(db0, tid, default); await db0.SaveChangesAsync();
        var co = await AddCompany(db0, tid, true); var a = await AddAccount(db0, tid, co.Id, "5001", "Expense");

        Status(await Gl(db0, tid, ManagePerms).SetMappings(co.Id, new() { new GlMappingRequest("EARN:BASIC", a.Id) }, default)).Should().Be(422);
    }
}
