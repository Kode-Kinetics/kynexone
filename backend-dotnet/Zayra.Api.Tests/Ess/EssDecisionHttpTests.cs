using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Ess;

/// <summary>HR's side: the inbox with diffs, accept, request changes, and the bank change that never applies on acceptance.</summary>
[Collection("EssP2")]
public class EssDecisionHttpTests(EssFixture fx)
{
    private EssCompletionHttpTests Employee => new(fx);
    private const string Iban = EssCompletionHttpTests.Iban, NewIban = EssCompletionHttpTests.NewIban;

    private async Task<Guid> SubmitPerson(EssEmployeeHandle e, string phone = "0500000001")
    {
        using var res = await Employee.Submit(e, "person", EssCompletionHttpTests.PersonValues(phone));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await EssCompletionHttpTests.Json(res)).GetProperty("requestId").GetGuid();
    }

    private async Task<Guid> SubmitBank(EssEmployeeHandle e)
    {
        using var res = await Employee.Submit(e, "bank", new { iban = NewIban, bankName = "Riyad Bank" }, stepUp: await fx.StepUpAsync(e));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await EssCompletionHttpTests.Json(res)).GetProperty("requestId").GetGuid();
    }

    [Fact]
    public async Task HrInbox_ShowsDiffMaskedWithoutSensitivePermission()  // AC56
    {
        var e = await fx.NewEmployeeAsync("SA", configure: x => { x.BankIban = Iban; x.BankName = "SNB"; x.Phone = "+966500000099"; });
        var person = await SubmitPerson(e, "0500000001");
        var bank = await SubmitBank(e);

        using var masked = await fx.SendAsync(HttpMethod.Get, "/api/hr/profile-completion/inbox?status=PendingHR", fx.HrNoSensitiveToken);
        masked.StatusCode.Should().Be(HttpStatusCode.OK, await masked.Content.ReadAsStringAsync());
        var text = await masked.Content.ReadAsStringAsync();
        text.Should().NotContain(NewIban).And.NotContain("1234567891234").And.NotContain(Iban).And.NotContain("0608010167");
        var items = JsonDocument.Parse(text).RootElement.EnumerateArray().Where(i => i.GetProperty("employee").GetProperty("id").GetInt32() == e.Id).ToList();
        items.Select(i => i.GetProperty("section").GetString()).Should().BeEquivalentTo(["person", "bank"]);
        var bankItem = items.Single(i => i.GetProperty("section").GetString() == "bank");
        var iban = bankItem.GetProperty("diff").EnumerateArray().Single(d => d.GetProperty("field").GetString() == "iban");
        iban.GetProperty("changed").GetBoolean().Should().BeTrue();
        iban.GetProperty("after").GetString().Should().EndWith("1234").And.Contain("*");
        iban.GetProperty("before").GetString().Should().EndWith("7519").And.Contain("*");
        var phone = items.Single(i => i.GetProperty("section").GetString() == "person").GetProperty("diff").EnumerateArray().Single(d => d.GetProperty("field").GetString() == "phone");
        phone.GetProperty("before").GetString().Should().Be("+966500000099");   // not sensitive: shown as is
        phone.GetProperty("after").GetString().Should().Be("+966500000001");    // normalised E.164
        phone.GetProperty("changed").GetBoolean().Should().BeTrue();
        bankItem.GetProperty("requestId").GetGuid().Should().Be(bank);
        bankItem.GetProperty("goesToFinance").GetBoolean().Should().BeTrue();

        using var clear = await fx.SendAsync(HttpMethod.Get, "/api/hr/profile-completion/inbox", fx.HrToken);
        var clearText = await clear.Content.ReadAsStringAsync();
        clearText.Should().Contain(NewIban).And.Contain(Iban);

        // The legacy HR list must not expose section values to HR without the sensitive permission.
        using var legacy = await fx.SendAsync(HttpMethod.Get, "/api/ess/profile-change-requests", fx.HrNoSensitiveToken);
        (await legacy.Content.ReadAsStringAsync()).Should().NotContain(NewIban).And.NotContain(person.ToString());
    }

    [Fact]
    public async Task HrInbox_OutOfScopeCompany_NotListed()
    {
        var e = await fx.NewEmployeeAsync("SA");
        await SubmitPerson(e);
        using var scoped = await fx.SendAsync(HttpMethod.Get, "/api/hr/profile-completion/inbox", fx.HrScopedOtherToken);
        scoped.StatusCode.Should().Be(HttpStatusCode.OK, await scoped.Content.ReadAsStringAsync());
        JsonDocument.Parse(await scoped.Content.ReadAsStringAsync()).RootElement.EnumerateArray()
            .Any(i => i.GetProperty("employee").GetProperty("id").GetInt32() == e.Id).Should().BeFalse();
        using var group = await fx.SendAsync(HttpMethod.Get, "/api/hr/profile-completion/inbox", fx.HrToken);
        JsonDocument.Parse(await group.Content.ReadAsStringAsync()).RootElement.EnumerateArray()
            .Any(i => i.GetProperty("employee").GetProperty("id").GetInt32() == e.Id).Should().BeTrue();
        // deciding out of scope is the same 404 as a missing request
        var id = (await fx.WithDbAsync(db => db.EmployeeProfileChangeRequests.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == e.Id))).Id;
        using var decide = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{id}/approve", fx.HrScopedOtherToken, new { notes = "x" });
        decide.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Accept_Person_AppliesAndAuditsInOneSaveChanges()  // AC57
    {
        var e = await fx.NewEmployeeAsync("SA");
        var id = await SubmitPerson(e, "0500000007");
        lock (SaveRecorder.Saves) SaveRecorder.Saves.Clear();
        using var approve = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{id}/approve", fx.HrToken, new { notes = "Looks right" });
        approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
        HashSet<string>[] saves; lock (SaveRecorder.Saves) saves = SaveRecorder.Saves.ToArray();
        saves.Count(s => s.Contains(nameof(EmployeeProfileChangeRequest)) && s.Contains(nameof(Employee)) && s.Contains(nameof(EmployeeSelfServiceAuditLog))).Should().Be(1,
            "the decision, the applied values and the audit row land together");
        var emp = await fx.WithDbAsync(db => db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == e.Id));
        emp.Phone.Should().Be("+966500000007"); emp.PersonalEmail.Should().Be("me@example.test"); emp.MaritalStatus.Should().Be("Single");
        var audits = await fx.WithDbAsync(db => db.EmployeeSelfServiceAuditLogs.IgnoreQueryFilters().Where(a => a.EmployeeId == e.Id).Select(a => a.Action).ToListAsync());
        audits.Should().Contain("ess.profile_change.approved");
        // The audit trail names fields and a last-4 hash, never the values.
        var meta = await fx.WithDbAsync(db => db.AuditLogs.IgnoreQueryFilters().Where(a => a.EntityId == id.ToString() && a.Action == "ess.section.accepted").Select(a => a.Metadata).SingleAsync());
        meta.Should().Contain("phone").And.Contain("last4").And.NotContain("0500000007").And.NotContain("me@example.test");
        // a second decision is a conflict
        using var twice = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{id}/approve", fx.HrToken, new { notes = "again" });
        twice.StatusCode.Should().Be(HttpStatusCode.Conflict);
        // the employee now sees the section as accepted
        using var mine = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion/person", e.Token);
        (await EssCompletionHttpTests.Json(mine)).GetProperty("state").GetString().Should().Be(EssCompletion.Accepted);
    }

    [Fact]
    public async Task Accept_SubjectAndSubmitterCannotDecide_AndStrictSodHolds()
    {
        var e = await fx.NewEmployeeAsync("SA");
        var id = await SubmitPerson(e);
        using var self = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{id}/approve", e.Token, new { notes = "me" });
        ((int)self.StatusCode).Should().BeOneOf(400, 403);
        (await fx.WithDbAsync(db => db.EmployeeProfileChangeRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == id))).Status.Should().Be("PendingHR");
    }

    [Fact]
    public async Task Hr_Accept_IdChecksumFail_NeedsOverridePermissionAndReason()
    {
        var e = await fx.NewEmployeeAsync("SA");
        // A pending id request whose number fails the checksum (stored by another path, e.g. before the rule existed).
        var requestId = Guid.NewGuid();
        await fx.WithDbAsync(async db =>
        {
            db.EmployeeProfileChangeRequests.Add(new EmployeeProfileChangeRequest
            {
                Id = requestId, TenantId = fx.TenantId, EmployeeId = e.Id, Section = "id", Status = "PendingHR", Reason = "ESS section: id",
                RequestedChangesJson = """{"nationalId":"1000000009"}""", ContainsSensitiveFields = true, NoticeVersion = "ess-1", CreatedBy = e.UserId,
            });
            await db.SaveChangesAsync();
        });
        using var plain = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{requestId}/approve", fx.HrToken, new { notes = "ok" });
        plain.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await EssCompletionHttpTests.Json(plain)).GetProperty("errors").GetProperty("nationalId")[0].GetString().Should().Be("id_checksum_invalid");
        // an override reason without the permission is refused
        using var noPerm = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{requestId}/approve", fx.HrToken, new { notes = "ok", overrideReason = "Verified against the original card by HR" });
        noPerm.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await EssCompletionHttpTests.Json(noPerm)).GetProperty("code").GetString().Should().Be("override_not_permitted");
        // the permission without a reason is refused
        using var noReason = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{requestId}/approve", fx.HrOverrideToken, new { notes = "ok" });
        noReason.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await fx.WithDbAsync(db => db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == e.Id))).IdNumber.Should().BeEmpty();
        // both: applied, and the waiver is audited with its reason
        using var ok = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{requestId}/approve", fx.HrOverrideToken, new { notes = "ok", overrideReason = "Verified against the original card by HR" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        (await fx.WithDbAsync(db => db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == e.Id))).IdNumber.Should().Be("1000000009");
        (await fx.WithDbAsync(db => db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.Action == "employee.id_checksum_waived" && a.EntityId == e.Id.ToString()))).Should().BeTrue();
    }

    [Fact]
    public async Task Accept_Bank_ActiveEmployee_CreatesEmployeeChangeRequest_NotApplied()  // B7 / CD53
    {
        var e = await fx.NewEmployeeAsync("SA", configure: x => { x.BankIban = Iban; x.BankName = "SNB"; });
        var id = await SubmitBank(e);
        using var approve = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{id}/approve", fx.HrToken, new { notes = "Letter seen" });
        approve.StatusCode.Should().Be(HttpStatusCode.Accepted, await approve.Content.ReadAsStringAsync());
        var body = await EssCompletionHttpTests.Json(approve);
        body.GetProperty("outcome").GetString().Should().Be("AwaitingFinance");
        var changeId = body.GetProperty("changeRequestId").GetGuid();

        var today = await fx.WithDbAsync(db => TenantClock.TodayForAsync(db, fx.TenantId, default));
        var (emp, change, approval) = await fx.WithDbAsync(async db => (
            await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == e.Id),
            await db.EmployeeChangeRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == changeId),
            await db.ApprovalRequests.IgnoreQueryFilters().SingleAsync(x => x.EntityName == nameof(EmployeeChangeRequest) && x.EntityId == changeId.ToString())));
        emp.BankIban.Should().Be(Iban, "HR acceptance never writes the IBAN"); emp.BankName.Should().Be("SNB");
        change.Status.Should().Be("PendingApproval");
        change.EffectiveDate.Should().Be(today.AddDays(1));
        change.SensitiveFields.Split(',').Should().BeEquivalentTo(["bankIban", "bankName"]);
        change.ProposedChangesJson.Should().Contain(NewIban);
        change.RequestedByUserId.Should().Be(fx.HrUserId, "the reviewing HR user is the submitter of the change to Finance, so the existing requester bar covers them");
        approval.RequestedForEmployeeId.Should().Be(e.Id);
        // the employee sees AwaitingFinance, with a masked value
        using var mine = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion/bank", e.Token);
        var section = await EssCompletionHttpTests.Json(mine);
        section.GetProperty("state").GetString().Should().Be(EssCompletion.AwaitingFinance);
        (await mine.Content.ReadAsStringAsync()).Should().NotContain(NewIban).And.NotContain(Iban);
        // and cannot submit another bank change meanwhile
        using var again = await Employee.Submit(e, "bank", new { iban = Iban, bankName = "SNB" }, stepUp: await fx.StepUpAsync(e));
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Accept_Bank_FinanceApproverCannotBeHrReviewerOrEmployee()  // CD39
    {
        var e = await fx.NewEmployeeAsync("SA", configure: x => { x.BankIban = Iban; x.BankName = "SNB"; });
        var id = await SubmitBank(e);
        using var accept = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{id}/approve", fx.HrToken, new { notes = "ok" });
        var changeId = (await EssCompletionHttpTests.Json(accept)).GetProperty("changeRequestId").GetGuid();
        var approvalId = (await fx.WithDbAsync(db => db.ApprovalRequests.IgnoreQueryFilters().SingleAsync(x => x.EntityId == changeId.ToString()))).Id;

        using var byReviewer = await fx.SendAsync(HttpMethod.Post, $"/api/approval-requests/{approvalId}/decisions", fx.HrToken, new { decision = "Approve", comments = "me again" });
        byReviewer.IsSuccessStatusCode.Should().BeFalse("the HR reviewer cannot also be the approver");
        using var byEmployee = await fx.SendAsync(HttpMethod.Post, $"/api/approval-requests/{approvalId}/decisions", e.Token, new { decision = "Approve" });
        byEmployee.IsSuccessStatusCode.Should().BeFalse("the employee cannot approve their own bank change");
        (await fx.WithDbAsync(db => db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == e.Id))).BankIban.Should().Be(Iban);
        // somebody else can decide it
        using var byFinance = await fx.SendAsync(HttpMethod.Post, $"/api/approval-requests/{approvalId}/decisions", fx.FinanceToken, new { decision = "Approve", comments = "Verified with the bank" });
        ((int)byFinance.StatusCode).Should().BeLessThan(500, await byFinance.Content.ReadAsStringAsync());
        byFinance.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Accept_Bank_NonActive_ReopensPayGap_ActivationRefused()  // CD37 / CD45
    {
        var e = await fx.NewEmployeeAsync("SA", status: "Invited");
        // A hire in the pipeline has a payroll profile (what Finance's pay gate reads); the bank details are the only thing missing.
        await fx.WithDbAsync(async db =>
        {
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = fx.TenantId, EmployeeId = e.Id, SalaryCurrency = "SAR" });
            await db.SaveChangesAsync();
        });
        var id = await SubmitBank(e);
        using var approve = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{id}/approve", fx.HrToken, new { notes = "ok" });
        approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
        var emp = await fx.WithDbAsync(db => db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == e.Id));
        emp.BankIban.Should().Be(NewIban);
        (await fx.WithDbAsync(db => db.EmployeeImportGaps.IgnoreQueryFilters().AnyAsync(g => g.EmployeeId == e.Id && g.GapType == "pay:compensationUnapproved" && g.ResolvedAtUtc == null)))
            .Should().BeTrue("Finance has to approve the pay package before the hire can be activated");
        (await fx.WithDbAsync(db => db.EmployeeChangeRequests.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == e.Id))).Should().Be(0);
        // The activation gate reads exactly this: an open gap and no Finance approval of the stored figure.
        (await fx.WithDbAsync(db => Zayra.Api.Infrastructure.Employees.Onboarding.CompensationApprovalGate.IsOpenAsync(db, fx.TenantId, e.Id, default))).Should().BeTrue();
        (await fx.WithDbAsync(db => Zayra.Api.Infrastructure.Employees.Onboarding.CompensationApprovalGate.ApprovedFingerprintAsync(db, fx.TenantId, e.Id, default)))
            .Should().Be(Zayra.Api.Infrastructure.Employees.Onboarding.CompensationApprovalGate.NoApproval);
        (await fx.WithDbAsync(db => db.EmployeePayrollProfiles.IgnoreQueryFilters().SingleAsync(p => p.EmployeeId == e.Id))).Iban.Should().Be(NewIban, "the payroll profile Finance's gate reads carries the figure");
        // the HR reviewer is recorded as a writer of the figures, so cannot be the Finance approver of them
        (await fx.WithDbAsync(db => db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.EntityId == e.Id.ToString() && a.UserId == fx.HrUserId && a.Action == "employee.compensation_written"))).Should().BeTrue();
    }

    [Fact]
    public async Task Reject_Section_ReopensOnlyThatSection_WithNote()  // AC58
    {
        var e = await fx.NewEmployeeAsync("SA");
        var person = await SubmitPerson(e);
        using var emergency = await Employee.Submit(e, "emergency", new { emergencyContactName = "Ali", emergencyContactPhone = "0500000003" });
        emergency.StatusCode.Should().Be(HttpStatusCode.OK);

        using var noNote = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{person}/reject", fx.HrToken, new { notes = "  " });
        noNote.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var tooLong = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{person}/reject", fx.HrToken, new { notes = new string('x', 501) });
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var reject = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{person}/reject", fx.HrToken, new { notes = "Please use your mobile number." });
        reject.StatusCode.Should().Be(HttpStatusCode.OK, await reject.Content.ReadAsStringAsync());

        using var list = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion", e.Token);
        var sections = (await EssCompletionHttpTests.Json(list)).GetProperty("sections").EnumerateArray().ToDictionary(s => s.GetProperty("key").GetString()!);
        sections["person"].GetProperty("state").GetString().Should().Be(EssCompletion.ChangesRequested);
        sections["person"].GetProperty("reviewNote").GetString().Should().Be("Please use your mobile number.");
        sections["emergency"].GetProperty("state").GetString().Should().Be(EssCompletion.Pending, "only the rejected section reopens");
        (await fx.WithDbAsync(db => db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == e.Id))).Phone.Should().BeEmpty();
        // the employee can fix and resubmit just that section
        using var again = await Employee.Submit(e, "person", EssCompletionHttpTests.PersonValues("0500000005"));
        again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());
        (await fx.WithDbAsync(db => db.EmployeeProfileChangeRequests.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == e.Id && x.Section == "person"))).Should().Be(2);
    }

    private async Task<Guid> SubmitId(EssEmployeeHandle e)
    {
        using var res = await Employee.Submit(e, "id", new { nationalId = "1000000008" }, stepUp: await fx.StepUpAsync(e));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await EssCompletionHttpTests.Json(res)).GetProperty("requestId").GetGuid();
    }

    [Fact]
    public async Task Reject_ReturnsTheMaskedReviewShape_ToAReviewerWithoutSensitivePermission()
    {
        var e = await fx.NewEmployeeAsync("SA", configure: x => { x.BankIban = Iban; x.BankName = "SNB"; });
        var id = await SubmitId(e);
        var bank = await SubmitBank(e);
        foreach (var request in new[] { id, bank })
        {
            using var res = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{request}/reject", fx.HrNoSensitiveToken, new { notes = "Please re-check this one." });
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
            var raw = await res.Content.ReadAsStringAsync();
            raw.Should().NotContain("1000000008").And.NotContain(NewIban).And.NotContain(Iban).And.NotContain("requestedChangesJson");
            raw.Should().Contain("Please re-check this one.", "the reject note stays readable");
        }
        // the stored request keeps what the employee typed so they can correct it
        (await fx.WithDbAsync(db => db.EmployeeProfileChangeRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == id))).Status.Should().Be("Rejected");
    }

    [Fact]
    public async Task Reject_WithSensitivePermission_FollowsTheInboxRules()
    {
        var e = await fx.NewEmployeeAsync("SA");
        var id = await SubmitId(e);
        using var res = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{id}/reject", fx.HrToken, new { notes = "Please re-check this one." });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("1000000008").And.NotContain("requestedChangesJson");
    }

    [Fact]
    public async Task Accept_Identity_ReturnsNoFullIdNumber_ToAReviewerWithoutSensitivePermission()
    {
        var e = await fx.NewEmployeeAsync("SA");
        var id = await SubmitId(e);
        using var res = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{id}/approve", fx.HrNoSensitiveToken, new { notes = "ok" });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().NotContain("1000000008").And.NotContain("requestedChangesJson");
    }

    [Fact]
    public async Task HrTrigger_AdditiveSections_AndDefaultsToAllMissing()
    {
        var e = await fx.NewEmployeeAsync("SA", configure: x => { x.PersonalEmail = "a@b.co"; x.Phone = "+966500000000"; });
        await fx.RequestSectionsAsync(e.Id);   // no body: legacy behaviour
        var item = await fx.WithDbAsync(db => db.EmployeeActionItems.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == e.Id));
        item.RequestedSections.Should().BeNull("a body-less request means every missing section, as before");
        using var bad = await fx.SendAsync(HttpMethod.Post, $"/api/employee-completion/{e.Id}", fx.HrToken, new { sections = new[] { "salary" } });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await fx.RequestSectionsAsync(e.Id, "person", "bank");
        (await fx.WithDbAsync(db => db.EmployeeActionItems.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == e.Id))).RequestedSections.Should().Be("person,bank");
    }
}
