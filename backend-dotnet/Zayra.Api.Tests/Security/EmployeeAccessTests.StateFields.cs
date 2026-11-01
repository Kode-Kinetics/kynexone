using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Auth;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The access card's two independent facts, <c>loginEnabled</c> and <c>passwordConfigured</c>, read from the data and
/// not from the merged <c>state</c>, so the profile can show three separate rows truthfully.
/// </summary>
public sealed partial class EmployeeAccessTests
{
    [Fact]
    public async Task StateFields_NoLogin_BothFalse()
    {
        var w = await SeedAsync();
        var none = await AddEmployeeAsync(w, "");
        var card = (await GetAsync(w, none, w.HrOfficerId))!;
        card.State.Should().Be(EmployeeAccessStates.WaitingForWorkEmail);
        (card.LoginEnabled, card.PasswordConfigured).Should().Be((false, false));
    }

    [Fact]
    public async Task StateFields_StagedLogin_IsNotEnabled_AndHasNoPassword()
    {
        var w = await SeedAsync();
        var id = await AddStagedAsync(w, $"sf.staged@{w.Domain}");
        var card = (await GetAsync(w, id, w.HrOfficerId))!;
        card.State.Should().Be(EmployeeAccessStates.NotStarted);
        (card.LoginEnabled, card.PasswordConfigured).Should().Be((false, false));
    }

    [Fact]
    public async Task StateFields_CodeGivenBeforeAnyPassword_LoginStillStagedPasswordNot()
    {
        var w = await SeedAsync();
        var id = await AddStagedAsync(w, $"sf.code@{w.Domain}");
        await IssueAsync(w, w.HrOfficerId, [id]);
        var card = (await GetAsync(w, id, w.HrOfficerId))!;
        card.State.Should().Be(EmployeeAccessStates.CodeGiven);
        // The login is only switched on when the employee redeems the code (it takes no seat before), so it is not enabled yet.
        card.LoginEnabled.Should().BeFalse();
        card.PasswordConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task StateFields_AfterRedeem_BothTrue_AndAfterReset_PasswordStaysConfigured()
    {
        var w = await SeedAsync();
        var email = $"sf.reset@{w.Domain}";
        var id = await AddStagedAsync(w, email);
        await RedeemAsync(w, email, (await IssueAsync(w, w.HrOfficerId, [id])).Issued.Single().Code!);
        var active = (await GetAsync(w, id, w.HrOfficerId))!;
        active.State.Should().Be(EmployeeAccessStates.Active);
        (active.LoginEnabled, active.PasswordConfigured).Should().Be((true, true));

        await IssueAsync(w, w.HrManagerId, [id], canReset: true);
        var reset = (await GetAsync(w, id, w.HrOfficerId))!;
        reset.State.Should().Be(EmployeeAccessStates.CodeGiven, "the merged state still says a code is live");
        (reset.LoginEnabled, reset.PasswordConfigured).Should().Be((true, true), "the old password still works until the code is redeemed");

        // The reset code lapses unredeemed: state falls back to not_started, but the password is still set.
        await using (var db = _fx.CreateDb())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE employee_user_accounts SET welcome_code_expires_at_utc = now() - interval '1 minute' WHERE employee_id = {id}");
        var lapsed = (await GetAsync(w, id, w.HrOfficerId))!;
        (lapsed.LoginEnabled, lapsed.PasswordConfigured).Should().Be((true, true));
    }

    [Fact]
    public async Task StateFields_Stopped_KeepsPasswordTruthful_AndLoginFollowsTheLogin()
    {
        var w = await SeedAsync();
        var email = $"sf.stopped@{w.Domain}";
        var withPassword = await AddStagedAsync(w, email);
        await RedeemAsync(w, email, (await IssueAsync(w, w.HrOfficerId, [withPassword])).Issued.Single().Code!);
        var neverSet = await AddStagedAsync(w, $"sf.stopped2@{w.Domain}");

        await UpdateEmployeeAsync(withPassword, e => e.Status = "Suspended");
        await UpdateEmployeeAsync(neverSet, e => e.Status = "Suspended");

        var a = (await GetAsync(w, withPassword, w.HrOfficerId))!;
        a.State.Should().Be(EmployeeAccessStates.Stopped);
        a.PasswordConfigured.Should().BeTrue("stopping access does not unset the password");
        a.LoginEnabled.Should().BeTrue("the login itself is untouched by an employment hold");
        var b = (await GetAsync(w, neverSet, w.HrOfficerId))!;
        b.State.Should().Be(EmployeeAccessStates.Stopped);
        (b.LoginEnabled, b.PasswordConfigured).Should().Be((false, false));

        // A login switched off by an administrator is not enabled, whatever the merged state says.
        var off = await AddStagedAsync(w, $"sf.off@{w.Domain}");
        await using (var db = _fx.CreateDb())
        {
            var link = await db.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == off);
            (await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == link.UserId)).Status = "Suspended";
            await db.SaveChangesAsync();
        }
        var c = (await GetAsync(w, off, w.HrOfficerId))!;
        c.State.Should().Be(EmployeeAccessStates.Stopped);
        c.LoginEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task StateFields_Blocked_BothFalse_AndTheListCarriesTheSameValues()
    {
        var w = await SeedAsync();
        var blocked = await AddEmployeeAsync(w, "incomplete-address");
        var card = (await GetAsync(w, blocked, w.HrOfficerId))!;
        card.State.Should().Be(EmployeeAccessStates.Blocked);
        (card.LoginEnabled, card.PasswordConfigured).Should().Be((false, false));

        var email = $"sf.list@{w.Domain}";
        var id = await AddStagedAsync(w, email);
        await RedeemAsync(w, email, (await IssueAsync(w, w.HrOfficerId, [id])).Issued.Single().Code!);
        await IssueAsync(w, w.HrManagerId, [id], canReset: true);
        await using var db = _fx.CreateDb();
        var items = await EmployeeAccessStates.DecorateAsync(db, w.TenantId,
            [new Zayra.Api.Controllers.EmployeeListItemDto(id, "C", "N", "", "", "", "", null, "Active", 0, null, null, "", 0),
             new Zayra.Api.Controllers.EmployeeListItemDto(blocked, "C2", "N2", "", "", "", "", null, "Active", 0, null, null, "", 0)], default);
        var reset = items.Single(i => i.Id == id);
        (reset.AccessState, reset.LoginEnabled, reset.PasswordConfigured).Should().Be((EmployeeAccessStates.CodeGiven, true, true));
        var b = items.Single(i => i.Id == blocked);
        (b.LoginEnabled, b.PasswordConfigured).Should().Be((false, false));
    }

    [Fact]
    public async Task StateFields_AreBooleansOnly_NothingSensitiveIsAdded()
    {
        var w = await SeedAsync();
        var email = $"sf.leak@{w.Domain}";
        var id = await AddStagedAsync(w, email);
        var code = (await IssueAsync(w, w.HrOfficerId, [id])).Issued.Single().Code!;
        var json = JsonSerializer.Serialize(await GetAsync(w, id, w.HrOfficerId), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("loginEnabled").ValueKind.Should().Be(JsonValueKind.False);
        root.GetProperty("passwordConfigured").ValueKind.Should().Be(JsonValueKind.False);
        json.Should().NotContain(code);
        root.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).Should().NotContain(n => n.Contains("hash") || n == "code" || n.Contains("secret"));
    }
}
