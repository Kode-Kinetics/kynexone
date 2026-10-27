using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Ess;

[CollectionDefinition("EssP2")]
public sealed class EssP2Collection : ICollectionFixture<EssFixture> { }

/// <summary>The employee's side: list, prefill, submit, step-up, rate limit, tenant/identity boundaries.</summary>
[Collection("EssP2")]
public class EssCompletionHttpTests(EssFixture fx)
{
    internal const string Iban = "SA0380000000608010167519";
    internal const string NewIban = "SA4420000001234567891234";

    internal static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    internal Task<HttpResponseMessage> Submit(EssEmployeeHandle e, string section, object? values, bool confirm = false, string? stepUp = null,
        string? notice = EssCompletion.NoticeVersion, object[]? documentIds = null) =>
        fx.SendAsync(HttpMethod.Post, $"/api/ess/completion/{section}/submit", e.Token,
            new { noticeVersion = notice, confirm, values = values ?? new { }, documentIds = documentIds ?? [] }, stepUp);

    internal static object PersonValues(string phone = "0500000001") => new { personalEmail = "me@example.test", phone, maritalStatus = "Single", preferredName = "Sam" };

    [Fact]
    public async Task Ess_Get_ListsOnlyMissingSections()  // AC53, AC54
    {
        var complete = await fx.NewEmployeeAsync("SA", configure: e =>
        {
            e.PersonalEmail = "a@b.co"; e.Phone = "+966500000000"; e.EmergencyContactName = "Ali"; e.EmergencyContactPhone = "+966500000001";
            e.ArabicName = "علي"; e.IdNumber = "1000000008"; e.BankIban = Iban; e.BankName = "SNB";
        });
        using var allDone = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion", complete.Token);
        allDone.StatusCode.Should().Be(HttpStatusCode.OK);
        var doneBody = await Json(allDone);
        doneBody.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("key").GetString())
            .Should().BeEquivalentTo(["documents"], "only the document copy is still missing");
        doneBody.GetProperty("noticeVersion").GetString().Should().Be(EssCompletion.NoticeVersion);

        var fresh = await fx.NewEmployeeAsync("PK");
        using var res = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion", fresh.Token);
        var body = await Json(res);
        body.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("key").GetString()).Should().Equal(EssSectionKeys.All);
        body.GetProperty("sections").EnumerateArray().All(s => s.GetProperty("state").GetString() == EssCompletion.Missing).Should().BeTrue();
        body.GetProperty("progress").GetProperty("total").GetInt32().Should().Be(6);
        body.GetProperty("progress").GetProperty("done").GetInt32().Should().Be(0);
        // The expatriate is asked for an Iqama, not a national id.
        var id = body.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "id");
        id.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString()).Should().Equal("iqamaNumber", "iqamaExpiry", "borderNumber", "passportNumber", "passportExpiry");
        id.GetProperty("requiresStepUp").GetBoolean().Should().BeTrue();
        // unknown section
        using var unknown = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion/payroll", fresh.Token);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ess_Identity_FollowsTheNationalityClass_Saudi_Gcc_Expat()
    {
        async Task<string[]> IdFields(EssEmployeeHandle e)
        {
            using var res = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion/id", e.Token);
            return (await Json(res)).GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString()!).ToArray();
        }
        (await IdFields(await fx.NewEmployeeAsync("SA"))).Should().Equal("nationalId");
        (await IdFields(await fx.NewEmployeeAsync("AE"))).Should().Equal("gccNationalId", "gccIdExpiry", "passportNumber", "passportExpiry");
        (await IdFields(await fx.NewEmployeeAsync("PK"))).Should().Equal("iqamaNumber", "iqamaExpiry", "borderNumber", "passportNumber", "passportExpiry");

        // A GCC national sends their own national ID and passport; HR accepting it writes the nationality's column, not the Iqama.
        var ae = await fx.NewEmployeeAsync("AE");
        var step = await fx.StepUpAsync(ae);
        using var wrong = await Submit(ae, "id", new { iqamaNumber = "2000000006" }, stepUp: step);
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var ok = await Submit(ae, "id", new { gccNationalId = "784199012345671", gccIdExpiry = "2030-05-01", passportNumber = "N1234567", passportExpiry = "2031-01-31" }, stepUp: step);
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        var requestId = (await Json(ok)).GetProperty("requestId").GetGuid();
        using var accept = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{requestId}/approve", fx.HrToken, new { notes = "Seen" });
        accept.StatusCode.Should().Be(HttpStatusCode.OK, await accept.Content.ReadAsStringAsync());
        var emp = await fx.WithDbAsync(db => db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == ae.Id));
        emp.EmiratesId.Should().Be("784199012345671"); emp.EmiratesIdExpiryDate.Should().Be(new DateOnly(2030, 5, 1));
        emp.PassportNumber.Should().Be("N1234567"); emp.PassportExpiryDate.Should().Be(new DateOnly(2031, 1, 31));
        emp.IqamaNumber.Should().BeEmpty();
        // the HR diff shows the expiry as a date (not masked) so the screen can add its Hijri equivalent
        var again = await fx.NewEmployeeAsync("PK");
        using var pk = await Submit(again, "id", new { iqamaNumber = "2000000006", iqamaExpiry = "2027-02-02" }, stepUp: await fx.StepUpAsync(again));
        pk.StatusCode.Should().Be(HttpStatusCode.OK, await pk.Content.ReadAsStringAsync());
        using var inbox = await fx.SendAsync(HttpMethod.Get, "/api/hr/profile-completion/inbox?section=id", fx.HrNoSensitiveToken);
        var diff = JsonDocument.Parse(await inbox.Content.ReadAsStringAsync()).RootElement.EnumerateArray()
            .Single(i => i.GetProperty("employee").GetProperty("id").GetInt32() == again.Id).GetProperty("diff").EnumerateArray()
            .Single(d => d.GetProperty("field").GetString() == "iqamaExpiry");
        diff.GetProperty("after").GetString().Should().Be("2027-02-02");
    }

    [Fact]
    public async Task Ess_Get_NeverReturnsFullIbanOrId()  // 3a disclosure
    {
        var e = await fx.NewEmployeeAsync("SA", configure: x => { x.BankIban = Iban; x.BankName = "SNB"; x.IdNumber = "1000000008"; });
        await fx.RequestSectionsAsync(e.Id, "id", "bank");
        using var res = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion", e.Token);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await res.Content.ReadAsStringAsync();
        text.Should().NotContain("0608010167").And.NotContain(Iban).And.NotContain("1000000008");
        text.Should().Contain("7519").And.Contain("0008");
        var bank = (await Json(res)).GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "bank");
        bank.GetProperty("state").GetString().Should().Be(EssCompletion.Confirm, "a present value HR asked to be confirmed");
        // the section endpoint and the submit response mask the same way
        using var one = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion/bank", e.Token);
        (await one.Content.ReadAsStringAsync()).Should().NotContain("0608010167");
        // no id/token column or route exists for this flow (AC54): the employee list has no employee id parameter and no link token.
        text.ToLowerInvariant().Should().NotContain("token").And.NotContain("link");
    }

    [Fact]
    public async Task Ess_Submit_WithoutStepUp_Returns401Challenge()  // 3a spoofing
    {
        var e = await fx.NewEmployeeAsync("SA");
        using var none = await Submit(e, "bank", new { iban = Iban, bankName = "SNB" });
        none.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        none.Headers.WwwAuthenticate.ToString().Should().Contain("StepUp").And.Contain("ess.sensitive");
        var problem = await Json(none);
        problem.GetProperty("code").GetString().Should().Be("step_up_required");
        problem.GetProperty("titleKey").GetString().Should().Be("ess.completion.error.step_up_required");
        problem.GetProperty("status").GetInt32().Should().Be(401);

        // a token for another user, or garbage, is the same challenge
        using var garbage = await Submit(e, "id", new { nationalId = "1000000008" }, stepUp: "not.a.token");
        garbage.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var other = await fx.NewEmployeeAsync("SA");
        var othersToken = await fx.StepUpAsync(other);
        using var wrongUser = await Submit(e, "id", new { nationalId = "1000000008" }, stepUp: othersToken);
        wrongUser.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await fx.WithDbAsync(db => db.EmployeeProfileChangeRequests.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == e.Id))).Should().Be(0);

        // the right proof is accepted; non-sensitive sections never need one
        var mine = await fx.StepUpAsync(e);
        using var ok = await Submit(e, "bank", new { iban = Iban, bankName = "SNB" }, stepUp: mine);
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        using var person = await Submit(e, "person", PersonValues());
        person.StatusCode.Should().Be(HttpStatusCode.OK, await person.Content.ReadAsStringAsync());
        var row = await fx.WithDbAsync(db => db.EmployeeProfileChangeRequests.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == e.Id && x.Section == "bank"));
        row.StepUpAtUtc.Should().NotBeNull(); row.NoticeVersion.Should().Be(EssCompletion.NoticeVersion); row.ContainsSensitiveFields.Should().BeTrue();
    }

    [Fact]
    public async Task StepUpEndpoint_WrongPassword_Refused_AndCountsTowardLockout()
    {
        var e = await fx.NewEmployeeAsync("SA");
        for (var i = 0; i < 4; i++)
        {
            using var bad = await fx.SendAsync(HttpMethod.Post, "/api/auth/step-up", e.Token, new { purpose = "ess.sensitive", password = "wrong" });
            bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await Json(bad)).GetProperty("code").GetString().Should().Be("invalid_credentials");
        }
        using var unknownPurpose = await fx.SendAsync(HttpMethod.Post, "/api/auth/step-up", e.Token, new { purpose = "root", password = EssFixture.Password });
        unknownPurpose.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await fx.WithDbAsync(db => db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == e.UserId))).FailedLoginCount.Should().Be(4);
        using var fifth = await fx.SendAsync(HttpMethod.Post, "/api/auth/step-up", e.Token, new { purpose = "ess.sensitive", password = "wrong" });
        fifth.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // locked now: even the right password is refused (and the locked account's session is cut at the door, with no body)
        using var locked = await fx.SendAsync(HttpMethod.Post, "/api/auth/step-up", e.Token, new { purpose = "ess.sensitive", password = EssFixture.Password });
        locked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await fx.WithDbAsync(db => db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == e.UserId))).IsLocked.Should().BeTrue();
        // anonymous callers cannot reach it at all
        using var anon = await fx.SendAsync(HttpMethod.Post, "/api/auth/step-up", null, new { purpose = "ess.sensitive", password = EssFixture.Password });
        anon.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ess_Submit_PendingSection_ReadOnly409()  // AC59
    {
        var e = await fx.NewEmployeeAsync("SA");
        using var first = await Submit(e, "person", PersonValues());
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        (await Json(first)).GetProperty("state").GetString().Should().Be(EssCompletion.Pending);
        using var second = await Submit(e, "person", PersonValues("0500000002"));
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(second)).GetProperty("code").GetString().Should().Be("section_pending");
        using var list = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion/person", e.Token);
        (await Json(list)).GetProperty("state").GetString().Should().Be(EssCompletion.Pending);
        (await fx.WithDbAsync(db => db.EmployeeProfileChangeRequests.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == e.Id))).Should().Be(1);
        // nothing was applied to the record while pending
        (await fx.WithDbAsync(db => db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == e.Id))).Phone.Should().BeEmpty();
    }

    [Fact]
    public async Task Ess_Submit_PrefillConfirmOnly_NoValue()  // AC55
    {
        var e = await fx.NewEmployeeAsync("SA", configure: x => { x.PersonalEmail = "keep@example.test"; x.Phone = "+966500000009"; });
        await fx.RequestSectionsAsync(e.Id, "person");
        using var get = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion/person", e.Token);
        (await Json(get)).GetProperty("state").GetString().Should().Be(EssCompletion.Confirm);
        using var confirm = await Submit(e, "person", null, confirm: true);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK, await confirm.Content.ReadAsStringAsync());
        var row = await fx.WithDbAsync(db => db.EmployeeProfileChangeRequests.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == e.Id && x.Section == "person"));
        var stored = JsonDocument.Parse(row.RequestedChangesJson).RootElement;
        stored.GetProperty("phone").GetString().Should().Be("+966500000009");
        stored.GetProperty("personalEmail").GetString().Should().Be("keep@example.test");

        // Confirm without anything to confirm is refused: there is no value to carry.
        var empty = await fx.NewEmployeeAsync("SA");
        using var nothing = await Submit(empty, "person", null, confirm: true);
        nothing.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(nothing)).GetProperty("code").GetString().Should().Be("validation_failed");
        // and a confirm that also sends values is ambiguous
        using var both = await Submit(empty, "person", PersonValues(), confirm: true);
        both.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ess_Submit_ValidationAndWhitelist()  // 3a tampering, AC61
    {
        var e = await fx.NewEmployeeAsync("SA");
        using var unknown = await Submit(e, "person", new { phone = "0500000001", personalEmail = "a@b.co", status = "Active", grade = "G9" });
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var p = await Json(unknown);
        p.GetProperty("code").GetString().Should().Be("unknown_field");
        p.GetProperty("errors").GetProperty("status")[0].GetString().Should().Be("unknown_field");
        using var noNotice = await Submit(e, "person", PersonValues(), notice: null);
        noNotice.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(noNotice)).GetProperty("code").GetString().Should().Be("notice_required");
        using var badEmail = await Submit(e, "person", new { personalEmail = "nope", phone = "0500000001" });
        badEmail.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(badEmail)).GetProperty("errors").GetProperty("personalEmail")[0].GetString().Should().Be("email_invalid");
        var step = await fx.StepUpAsync(e);
        using var badId = await Submit(e, "id", new { nationalId = "1000000009" }, stepUp: step);
        badId.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(badId)).GetProperty("errors").GetProperty("nationalId")[0].GetString().Should().Be("id_checksum_invalid");
        using var badIban = await Submit(e, "bank", new { iban = "SA03800000006080101675", bankName = "SNB" }, stepUp: step);
        badIban.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var longName = await Submit(e, "arabicName", new { arabicName = new string('ع', 151) });
        longName.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var latin = await Submit(e, "arabicName", new { arabicName = "Ali" });
        latin.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var unknownSection = await Submit(e, "salary", new { amount = 1 });
        unknownSection.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await fx.WithDbAsync(db => db.EmployeeProfileChangeRequests.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == e.Id))).Should().Be(0);
    }

    [Fact]
    public async Task Ess_OtherEmployeeIdInBody_Ignored_And_HrRouteWithoutScope_403()  // AC63
    {
        var me = await fx.NewEmployeeAsync("SA");
        var victim = await fx.NewEmployeeAsync("SA");
        using var res = await fx.SendAsync(HttpMethod.Post, "/api/ess/completion/person/submit", me.Token,
            new { employeeId = victim.Id, noticeVersion = EssCompletion.NoticeVersion, confirm = false, values = new { personalEmail = "me@example.test", phone = "0500000001", employeeId = victim.Id } });
        // employeeId at the top level is ignored; employeeId inside values is an unknown field.
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var ok = await Submit(me, "person", PersonValues());
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await fx.WithDbAsync(db => db.EmployeeProfileChangeRequests.IgnoreQueryFilters().Where(x => x.Section == "person" && (x.EmployeeId == me.Id || x.EmployeeId == victim.Id)).ToListAsync());
        rows.Should().ContainSingle().Which.EmployeeId.Should().Be(me.Id);

        // HR routes: an employee cannot open the inbox; HR without write cannot either; unauthenticated is 401.
        using var asEmployee = await fx.SendAsync(HttpMethod.Get, "/api/hr/profile-completion/inbox", me.Token);
        asEmployee.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var asFinance = await fx.SendAsync(HttpMethod.Get, "/api/hr/profile-completion/inbox", fx.FinanceToken);
        asFinance.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var anon = await fx.SendAsync(HttpMethod.Get, "/api/hr/profile-completion/inbox", null);
        anon.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // An HR user has no employee link, so the ESS list is "not linked", never someone else's data.
        using var hrEss = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion", fx.HrToken);
        ((int)hrEss.StatusCode).Should().BeOneOf(403, 409);
    }

    [Fact]
    public async Task Ess_SubmitRateLimit_6thPerDay_429()  // 3a DoS
    {
        var e = await fx.NewEmployeeAsync("SA");
        await fx.WithDbAsync(async db =>
        {
            for (var i = 0; i < EssCompletion.MaxSubmissionsPerDay; i++)
                db.EmployeeProfileChangeRequests.Add(new EmployeeProfileChangeRequest
                {
                    TenantId = fx.TenantId, EmployeeId = e.Id, Section = "emergency", Status = "Rejected", Reason = "x", RequestedChangesJson = "{}",
                    CreatedAtUtc = DateTime.UtcNow.AddHours(-1 - i),
                });
            // older than a day does not count
            db.EmployeeProfileChangeRequests.Add(new EmployeeProfileChangeRequest
            {
                TenantId = fx.TenantId, EmployeeId = e.Id, Section = "emergency", Status = "Rejected", Reason = "x", RequestedChangesJson = "{}", CreatedAtUtc = DateTime.UtcNow.AddHours(-30),
            });
            await db.SaveChangesAsync();
        });
        using var sixth = await Submit(e, "person", PersonValues());
        sixth.StatusCode.Should().Be((HttpStatusCode)429);
        (await Json(sixth)).GetProperty("code").GetString().Should().Be("rate_limited");
        sixth.Headers.Contains("Retry-After").Should().BeTrue();
        // another employee is unaffected
        var other = await fx.NewEmployeeAsync("SA");
        using var fine = await Submit(other, "person", PersonValues());
        fine.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ess_Submit_AfterAcceptance_Locked409_UntilHrRequestsAgain()
    {
        var e = await fx.NewEmployeeAsync("SA");
        using var sub = await Submit(e, "emergency", new { emergencyContactName = "Ali", emergencyContactPhone = "0500000003" });
        sub.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await Json(sub)).GetProperty("requestId").GetGuid();
        using var approve = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{id}/approve", fx.HrToken, new { notes = "ok" });
        approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
        using var again = await Submit(e, "emergency", new { emergencyContactName = "Omar", emergencyContactPhone = "0500000004" });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(again)).GetProperty("code").GetString().Should().Be("section_locked");
        await fx.RequestSectionsAsync(e.Id, "emergency");
        using var reopened = await Submit(e, "emergency", new { emergencyContactName = "Omar", emergencyContactPhone = "0500000004" });
        reopened.StatusCode.Should().Be(HttpStatusCode.OK, await reopened.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ess_NotLinked_And_StaleEligibility()
    {
        var e = await fx.NewEmployeeAsync("SA");
        await fx.WithDbAsync(async db =>
        {
            var emp = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == e.Id);
            emp.Status = "Terminated";
            await db.SaveChangesAsync();
        });
        using var read = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion", e.Token);
        read.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(read)).GetProperty("code").GetString().Should().Be("ess_not_linked");
        using var submit = await Submit(e, "person", PersonValues());
        submit.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(submit)).GetProperty("code").GetString().Should().Be("ess_not_linked");
        (await fx.WithDbAsync(db => db.EmployeeProfileChangeRequests.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == e.Id))).Should().Be(0);
    }
}
