using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;
using Zayra.Api.Tests.Ess;

namespace Zayra.Api.Tests.Esign;

/// <summary>Second-train hardening of click-to-sign (CD60 device step-up binding).</summary>
[Collection("EsignP4")]
public class EsignHardeningHttpTests(EsignFixture fx)
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<HttpResponseMessage> DeviceAuthAsync(int employeeId, SignDoc doc, string password, string? sha = null) =>
        await fx.SendAsync(HttpMethod.Post, "/api/auth/step-up/employee-device", fx.HrToken,
            new { employeeId, documentId = doc.Id, documentSha256 = sha ?? doc.Sha256, password });

    private async Task<string> DeviceTokenAsync(EssEmployeeHandle e, SignDoc doc)
    {
        using var auth = await DeviceAuthAsync(e.Id, doc, EsignFixture.Password);
        auth.StatusCode.Should().Be(HttpStatusCode.OK, await auth.Content.ReadAsStringAsync());
        return (await Json(auth)).GetProperty("stepUpToken").GetString()!;
    }

    [Fact]
    public async Task DeviceToken_ForDocumentA_CannotSignDocumentB()  // CD60
    {
        var e = await fx.NewEmployeeAsync();
        var a = await fx.NewDocumentAsync(e); var b = await fx.NewDocumentAsync(e);
        var token = await DeviceTokenAsync(e, a);
        using var other = await fx.SendAsync(HttpMethod.Post, $"/api/employees/{e.Id}/signatures/{b.Id}/sign-on-device", fx.HrToken, EsignFixture.Body(b, "Layla Hassan"), token, Guid.NewGuid().ToString("N"));
        other.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await fx.WithDbAsync(db => db.EmployeePolicyAcknowledgements.IgnoreQueryFilters().CountAsync(x => x.PolicyId == b.Id))).Should().Be(0);
        using var same = await fx.SendAsync(HttpMethod.Post, $"/api/employees/{e.Id}/signatures/{a.Id}/sign-on-device", fx.HrToken, EsignFixture.Body(a, "Layla Hassan"), token, Guid.NewGuid().ToString("N"));
        same.StatusCode.Should().Be(HttpStatusCode.Created, await same.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeviceToken_NeedsDocumentAndTheHashShown_AndRefusesAStaleHash()  // CD60
    {
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e);
        using var noDoc = await fx.SendAsync(HttpMethod.Post, "/api/auth/step-up/employee-device", fx.HrToken, new { employeeId = e.Id, password = EsignFixture.Password });
        noDoc.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var stale = await DeviceAuthAsync(e.Id, doc, EsignFixture.Password, new string('0', 64));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(stale)).GetProperty("code").GetString().Should().Be("document_changed");
    }

    [Fact]
    public async Task DeviceStepUp_NoLogin_And_WrongPassword_LookTheSame()  // CD60
    {
        var withLogin = await fx.NewEmployeeAsync();
        var noLogin = await fx.NewEmployeeAsync();
        await fx.WithDbAsync(async db =>
        {
            db.EmployeeUserAccounts.RemoveRange(db.EmployeeUserAccounts.IgnoreQueryFilters().Where(a => a.EmployeeId == noLogin.Id));
            var emp = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == noLogin.Id); emp.UserAccountId = null;
            await db.SaveChangesAsync();
        });
        var docA = await fx.NewDocumentAsync(withLogin); var docB = await fx.NewDocumentAsync(noLogin);
        using var wrong = await DeviceAuthAsync(withLogin.Id, docA, "definitely-wrong-password");
        using var none = await DeviceAuthAsync(noLogin.Id, docB, "definitely-wrong-password");
        none.StatusCode.Should().Be(wrong.StatusCode).And.Be(HttpStatusCode.Unauthorized);
        (await Json(none)).GetProperty("code").GetString().Should().Be((await Json(wrong)).GetProperty("code").GetString()).And.Be("employee_device_auth_failed");
    }

    [Fact]
    public async Task DeviceFailures_UseTheirOwnCounter_TheEmployeeIsNeverLockedOut()  // CD60
    {
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++) { using var r = await DeviceAuthAsync(e.Id, doc, "wrong-" + i); statuses.Add(r.StatusCode); }
        statuses.Take(5).Should().OnlyContain(s => s == HttpStatusCode.Unauthorized);
        statuses.Skip(5).Should().OnlyContain(s => s == HttpStatusCode.TooManyRequests, "the device flow throttles itself");
        var user = await fx.WithDbAsync(db => db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == e.UserId));
        user.FailedLoginCount.Should().Be(0); user.IsLocked.Should().BeFalse();
        using var signIn = await fx.SendAsync(HttpMethod.Post, "/api/auth/step-up", e.Token, new { purpose = "esign", password = EsignFixture.Password });
        signIn.StatusCode.Should().Be(HttpStatusCode.OK, "the employee can still prove themselves in their own session");
    }
}
