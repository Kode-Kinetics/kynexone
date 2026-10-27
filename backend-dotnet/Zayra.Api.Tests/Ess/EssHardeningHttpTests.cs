using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Ess;

/// <summary>Second-train hardening of the ESS completion surface (CD58 HR inbox gates).</summary>
[Collection("EssP2")]
public class EssHardeningHttpTests(EssFixture fx)
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF");

    private async Task<(EssEmployeeHandle Employee, Guid DocId)> EmployeeWithUploadAsync(string kind)
    {
        var e = await fx.NewEmployeeAsync("SA");
        var form = new MultipartFormDataContent { { new StringContent("documents"), "section" }, { new StringContent(kind), "kind" } };
        var file = new ByteArrayContent(Pdf);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "scan.pdf");
        using var res = await fx.SendRawAsync(new HttpRequestMessage(HttpMethod.Post, "/api/ess/completion/documents") { Content = form }, e.Token);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (e, JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task HrInboxDownload_NeedsDocumentsPermission_AndSensitiveForIdentityFiles()  // CD58
    {
        var (_, id) = await EmployeeWithUploadAsync("national_id_copy");
        var url = $"/api/hr/profile-completion/documents/{id}/download";
        using (var noDocs = await fx.SendAsync(HttpMethod.Get, url, fx.HrNoDocumentsToken)) noDocs.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using (var noSens = await fx.SendAsync(HttpMethod.Get, url, fx.HrNoSensitiveToken)) noSens.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using (var ok = await fx.SendAsync(HttpMethod.Get, url, fx.HrToken)) ok.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HrInbox_HidesDocumentIds_WithoutSensitivePermission()  // CD58
    {
        var (e, id) = await EmployeeWithUploadAsync("national_id_copy");
        using var submit = await fx.SendAsync(HttpMethod.Post, "/api/ess/completion/documents/submit", e.Token,
            new { noticeVersion = EssCompletion.NoticeVersion, confirm = false, values = new { }, documentIds = new[] { id } });
        submit.StatusCode.Should().Be(HttpStatusCode.OK, await submit.Content.ReadAsStringAsync());
        using var masked = await fx.SendAsync(HttpMethod.Get, "/api/hr/profile-completion/inbox?section=documents", fx.HrNoSensitiveToken);
        var text = await masked.Content.ReadAsStringAsync();
        text.Should().NotContain(id.ToString());
        var item = JsonDocument.Parse(text).RootElement.EnumerateArray().Single(i => i.GetProperty("employee").GetProperty("id").GetInt32() == e.Id);
        item.GetProperty("documents").GetArrayLength().Should().Be(1);   // the file is still listed, only its id is withheld
        using var clear = await fx.SendAsync(HttpMethod.Get, "/api/hr/profile-completion/inbox?section=documents", fx.HrToken);
        (await clear.Content.ReadAsStringAsync()).Should().Contain(id.ToString());
    }

    [Fact]
    public async Task HrInboxView_WithSensitiveValues_IsAudited()  // CD58
    {
        var e = await fx.NewEmployeeAsync("SA");
        using var sub = await fx.SendAsync(HttpMethod.Post, "/api/ess/completion/person/submit", e.Token, new { noticeVersion = EssCompletion.NoticeVersion, values = new { personalEmail = "a@b.co", phone = "0500000001" } });
        sub.StatusCode.Should().Be(HttpStatusCode.OK, await sub.Content.ReadAsStringAsync());
        using var view = await fx.SendAsync(HttpMethod.Get, "/api/hr/profile-completion/inbox", fx.HrToken);
        view.StatusCode.Should().Be(HttpStatusCode.OK);
        (await fx.WithDbAsync(db => db.EmployeeSelfServiceAuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EmployeeId == e.Id && a.Action == "ess.completion.inbox_viewed_by_hr" && a.UserId == fx.HrUserId))).Should().BeGreaterThan(0);
    }

    // ── CD59: the bank change needs a Finance step ──────────────────────────────────────────────────────────

    private async Task<Guid> AcceptBankAsync()
    {
        var e = await fx.NewEmployeeAsync("SA", configure: x => { x.BankIban = EssCompletionHttpTests.Iban; x.BankName = "SNB"; });
        using var sub = await new EssCompletionHttpTests(fx).Submit(e, "bank", new { iban = EssCompletionHttpTests.NewIban, bankName = "Riyad Bank" }, stepUp: await fx.StepUpAsync(e));
        sub.StatusCode.Should().Be(HttpStatusCode.OK, await sub.Content.ReadAsStringAsync());
        var id = (await EssCompletionHttpTests.Json(sub)).GetProperty("requestId").GetGuid();
        using var accept = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{id}/approve", fx.HrToken, new { notes = "ok" });
        accept.StatusCode.Should().Be(HttpStatusCode.Accepted, await accept.Content.ReadAsStringAsync());
        var changeId = (await EssCompletionHttpTests.Json(accept)).GetProperty("changeRequestId").GetGuid();
        e.Should().NotBeNull();
        BankEmployee = e;
        return (await fx.WithDbAsync(db => db.ApprovalRequests.IgnoreQueryFilters().SingleAsync(x => x.EntityId == changeId.ToString()))).Id;
    }

    private string LastDecision = "";
    private EssEmployeeHandle BankEmployee { get; set; } = null!;

    private async Task<bool> DecideAsync(Guid approvalId, string token)
    {
        using var res = await fx.SendAsync(HttpMethod.Post, $"/api/approval-requests/{approvalId}/decisions", token, new { decision = "Approve", comments = "checked" });
        LastDecision = await res.Content.ReadAsStringAsync();
        return res.IsSuccessStatusCode;
    }

    [Fact]
    public async Task Bank_ActiveEmployee_WorkflowHasAFinalFinanceStep_OnlyAPayrollApproverCanDecide()  // CD59
    {
        var approvalId = await AcceptBankAsync();
        var steps = await fx.WithDbAsync(async db =>
        {
            var wf = await db.ApprovalWorkflows.IgnoreQueryFilters().Include(w => w.Steps).SingleAsync(w => w.Id == db.ApprovalRequests.IgnoreQueryFilters().Where(r => r.Id == approvalId).Select(r => r.WorkflowId).First());
            return wf.Steps.OrderBy(s => s.StepOrder).ToList();
        });
        steps.Last().ApproverRole.Should().Be("Finance Approver");
        steps.Last().IsFinalStep.Should().BeTrue();

        (await DecideAsync(approvalId, fx.FinanceToken)).Should().BeTrue();          
        // A manager step with nobody routed is skipped or escalated to the HR queue; either way a second, different HR decider clears whatever HR step remains.
        if ((await fx.WithDbAsync(db => db.ApprovalRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == approvalId))).CurrentApproverRole == "HR Manager")
            (await DecideAsync(approvalId, fx.HrSecondApproverToken)).Should().BeTrue(LastDecision);
        (await DecideAsync(approvalId, fx.HrToken)).Should().BeFalse("the HR acceptor is never a decider");
        (await DecideAsync(approvalId, fx.FinanceNoPayrollToken)).Should().BeFalse("the Finance step needs payroll.approve");
        (await DecideAsync(approvalId, BankEmployee.Token)).Should().BeFalse("the subject never decides");
        (await fx.WithDbAsync(db => db.ApprovalRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == approvalId))).Status.Should().Be("Pending");
        (await DecideAsync(approvalId, fx.FinanceApproverToken)).Should().BeTrue();
        (await fx.WithDbAsync(db => db.ApprovalRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == approvalId))).Status.Should().Be("Approved");
    }

    [Fact]
    public async Task Bank_Notification_SaysWhoApproves()  // CD59
    {
        await AcceptBankAsync();
        var bodies = await fx.WithDbAsync(db => db.EmployeeNotifications.IgnoreQueryFilters().Where(n => n.EmployeeId == BankEmployee.Id && n.NotificationType == "EssBankChange").Select(n => n.Body).ToListAsync());
        bodies.Should().Contain(b => b.Contains("Finance approver") && b.Contains("manager") && b.Contains("HR manager"));
    }
}
