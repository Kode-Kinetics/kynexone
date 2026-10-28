using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Controllers;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Ess;

[Collection("EssP2")]
public class EssUploadAndSecurityHttpTests(EssFixture fx)
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    private HttpRequestMessage UploadRequest(string section, string kind, byte[] bytes, string fileName = "scan.pdf", string contentType = "application/pdf")
    {
        var form = new MultipartFormDataContent { { new StringContent(section), "section" }, { new StringContent(kind), "kind" } };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        return new HttpRequestMessage(HttpMethod.Post, "/api/ess/completion/documents") { Content = form };
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> Upload(EssEmployeeHandle e, string section, string kind, byte[] bytes,
        string fileName = "scan.pdf", string contentType = "application/pdf", string? stepUp = null)
    {
        var res = await fx.SendRawAsync(UploadRequest(section, kind, bytes, fileName, contentType), e.Token, stepUp);
        var text = await res.Content.ReadAsStringAsync();
        return (res, text.Length > 0 && text.TrimStart().StartsWith('{') ? JsonDocument.Parse(text).RootElement : default);
    }

    [Fact]
    public async Task Upload_RejectsMismatchedMagicBytes()  // AC60
    {
        var e = await fx.NewEmployeeAsync("SA");
        var (res, body) = await Upload(e, "documents", "national_id_copy", Png, "id.pdf", "application/pdf");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("code").GetString().Should().Be("upload_rejected");
        body.GetProperty("errors").GetProperty("file")[0].GetString().Should().Be("type_mismatch");
        var (exe, _) = await Upload(e, "documents", "national_id_copy", Encoding.ASCII.GetBytes("MZ\u0090\u0000not a pdf"), "x.pdf", "application/pdf");
        exe.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var (kind, kindBody) = await Upload(e, "documents", "payslip", Pdf);
        kind.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        kindBody.GetProperty("errors").GetProperty("kind")[0].GetString().Should().Be("invalid_format");
        (await fx.WithDbAsync(db => db.EmployeeDocuments.IgnoreQueryFilters().CountAsync(d => d.EmployeeId == e.Id))).Should().Be(0);
    }

    [Fact]
    public async Task Upload_RejectsOversize5MB()
    {
        var e = await fx.NewEmployeeAsync("SA");
        var big = new byte[(int)EssCompletion.MaxFileBytes + 1];
        Array.Copy(Pdf, big, Pdf.Length);
        var (res, body) = await Upload(e, "documents", "national_id_copy", big);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("errors").GetProperty("file")[0].GetString().Should().Be("too_large");
        var atLimit = new byte[(int)EssCompletion.MaxFileBytes];
        Array.Copy(Pdf, atLimit, Pdf.Length);
        var (ok, _) = await Upload(e, "documents", "national_id_copy", atLimit);
        ok.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Upload_Rejects4thAttachment()
    {
        var e = await fx.NewEmployeeAsync("SA");
        for (var i = 0; i < EssCompletion.MaxAttachmentsPerSection; i++)
        {
            var (r, _) = await Upload(e, "documents", "national_id_copy", Pdf, $"scan{i}.pdf");
            r.StatusCode.Should().Be(HttpStatusCode.Created);
        }
        var (fourth, body) = await Upload(e, "documents", "passport_copy", Pdf, "fourth.pdf");
        fourth.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("errors").GetProperty("file")[0].GetString().Should().Be("too_many");
        // a different section has its own allowance
        var step = await fx.StepUpAsync(e);
        var (bank, _) = await Upload(e, "bank", "bank_letter", Pdf, "letter.pdf", stepUp: step);
        bank.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Upload_BankLetter_NeedsStepUp()
    {
        var e = await fx.NewEmployeeAsync("SA");
        var (res, body) = await Upload(e, "bank", "bank_letter", Pdf, "letter.pdf");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        body.GetProperty("code").GetString().Should().Be("step_up_required");
        (await fx.WithDbAsync(db => db.EmployeeDocuments.IgnoreQueryFilters().CountAsync(d => d.EmployeeId == e.Id))).Should().Be(0);
    }

    [Fact]
    public async Task Upload_ServesAsAttachmentWithNosniff()
    {
        var e = await fx.NewEmployeeAsync("SA");
        var (res, body) = await Upload(e, "documents", "national_id_copy", Pdf, "../../evil name.pdf");
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        body.GetProperty("scanStatus").GetString().Should().Be("clean");
        var id = body.GetProperty("id").GetGuid();
        var doc = await fx.WithDbAsync(db => db.EmployeeDocuments.IgnoreQueryFilters().SingleAsync(d => d.Id == id));
        doc.SourceSection.Should().Be("documents"); doc.ScanStatus.Should().Be("clean"); doc.ApprovalStatus.Should().Be("Pending");
        doc.FileName.Should().NotContain("/").And.NotContain("..");
        using var download = await fx.SendAsync(HttpMethod.Get, $"/api/ess/documents/{id}/download", e.Token);
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        download.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        (await download.Content.ReadAsByteArrayAsync()).Should().Equal(Pdf);
    }

    [Fact]
    public async Task Upload_AvQuarantineBlocksDownload()
    {
        var e = await fx.NewEmployeeAsync("SA");
        fx.Scanner.Verdict = ScanVerdict.Quarantined;
        try
        {
            var (res, body) = await Upload(e, "documents", "national_id_copy", Pdf);
            res.StatusCode.Should().Be(HttpStatusCode.Created);
            body.GetProperty("scanStatus").GetString().Should().Be("quarantined");
            var id = body.GetProperty("id").GetGuid();
            using var own = await fx.SendAsync(HttpMethod.Get, $"/api/ess/documents/{id}/download", e.Token);
            own.StatusCode.Should().Be(HttpStatusCode.Conflict);
            using var hr = await fx.SendAsync(HttpMethod.Get, $"/api/hr/profile-completion/documents/{id}/download", fx.HrToken);
            hr.StatusCode.Should().Be(HttpStatusCode.Conflict);
            // ...and neither does the general employee-documents download that HR uses from the employee workspace
            using var general = await fx.SendAsync(HttpMethod.Get, $"/api/employees/documents/{id}/download", fx.HrDocumentsToken);
            general.StatusCode.Should().Be(HttpStatusCode.Conflict);
            // A quarantined file cannot be attached to a submission.
            using var attach = await fx.SendAsync(HttpMethod.Post, "/api/ess/completion/documents/submit-probe", e.Token);
            attach.StatusCode.Should().Be(HttpStatusCode.NotFound);
            using var sub = await fx.SendAsync(HttpMethod.Post, "/api/ess/completion/documents/submit", e.Token,
                new { noticeVersion = EssCompletion.NoticeVersion, confirm = false, values = new { }, documentIds = new[] { id } });
            sub.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally { fx.Scanner.Verdict = ScanVerdict.Clean; }
    }

    [Fact]
    public async Task Upload_VisibleOnlyToOwnerAndAuthorisedHr()
    {
        var e = await fx.NewEmployeeAsync("SA");
        var (_, body) = await Upload(e, "documents", "national_id_copy", Pdf);
        var id = body.GetProperty("id").GetGuid();
        var colleague = await fx.NewEmployeeAsync("SA");
        using var other = await fx.SendAsync(HttpMethod.Get, $"/api/ess/documents/{id}/download", colleague.Token);
        other.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var otherHr = await fx.SendAsync(HttpMethod.Get, $"/api/hr/profile-completion/documents/{id}/download", colleague.Token);
        otherHr.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var hr = await fx.SendAsync(HttpMethod.Get, $"/api/hr/profile-completion/documents/{id}/download", fx.HrToken);
        hr.StatusCode.Should().Be(HttpStatusCode.OK);
        hr.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        hr.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        using var scoped = await fx.SendAsync(HttpMethod.Get, $"/api/hr/profile-completion/documents/{id}/download", fx.HrScopedOtherToken);
        scoped.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var finance = await fx.SendAsync(HttpMethod.Get, $"/api/hr/profile-completion/documents/{id}/download", fx.FinanceToken);
        finance.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var missing = await fx.SendAsync(HttpMethod.Get, $"/api/hr/profile-completion/documents/{Guid.NewGuid()}/download", fx.HrToken);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Documents_Section_StaysListedAfterUpload_AndHrAcceptsTheFiles()  // AC53/AC57
    {
        var e = await fx.NewEmployeeAsync("PK");
        var (up, body) = await Upload(e, "documents", "iqama_copy", Pdf, "iqama.pdf");
        up.StatusCode.Should().Be(HttpStatusCode.Created);
        var docId = body.GetProperty("id").GetGuid();
        // An uploaded copy is not "held" until HR accepts it: after a page reload the section is still asked for, with the file shown.
        using (var list = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion/documents", e.Token))
        {
            var section = await EssCompletionHttpTests.Json(list);
            section.GetProperty("state").GetString().Should().Be(EssCompletion.Missing);
            section.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).Should().Equal(docId);
        }
        using var empty = await fx.SendAsync(HttpMethod.Post, "/api/ess/completion/documents/submit", e.Token, new { noticeVersion = EssCompletion.NoticeVersion, confirm = false, values = new { }, documentIds = Array.Empty<Guid>() });
        empty.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var sent = await fx.SendAsync(HttpMethod.Post, "/api/ess/completion/documents/submit", e.Token, new { noticeVersion = EssCompletion.NoticeVersion, confirm = false, values = new { }, documentIds = new[] { docId } });
        sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        var requestId = (await EssCompletionHttpTests.Json(sent)).GetProperty("requestId").GetGuid();
        // the file travels with the request into HR's inbox
        using var inbox = await fx.SendAsync(HttpMethod.Get, "/api/hr/profile-completion/inbox?section=documents", fx.HrToken);
        JsonDocument.Parse(await inbox.Content.ReadAsStringAsync()).RootElement.EnumerateArray()
            .Single(i => i.GetProperty("requestId").GetGuid() == requestId).GetProperty("documents").EnumerateArray()
            .Select(d => d.GetProperty("id").GetGuid()).Should().Equal(docId);
        using var accept = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{requestId}/approve", fx.HrToken, new { notes = "Seen" });
        accept.StatusCode.Should().Be(HttpStatusCode.OK, await accept.Content.ReadAsStringAsync());
        (await fx.WithDbAsync(db => db.EmployeeDocuments.IgnoreQueryFilters().SingleAsync(d => d.Id == docId))).ApprovalStatus.Should().Be("Approved");
        using var after = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion/documents", e.Token);
        (await EssCompletionHttpTests.Json(after)).GetProperty("state").GetString().Should().Be(EssCompletion.Accepted);
    }

    [Fact]
    public async Task SecurityHeaders_EssRoutes_DenyFraming()
    {
        var e = await fx.NewEmployeeAsync("SA");
        foreach (var path in new[] { "/api/ess/completion", "/api/ess/completion/person" })
        {
            using var res = await fx.SendAsync(HttpMethod.Get, path, e.Token);
            res.StatusCode.Should().Be(HttpStatusCode.OK, path);
            res.Headers.GetValues("X-Frame-Options").Should().Contain("DENY", path);
            res.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("frame-ancestors 'none'", path);
            res.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff", path);
        }
    }

    [Fact]
    public async Task Ess_AllCompletionResponses_NoStore()
    {
        var e = await fx.NewEmployeeAsync("SA", configure: x => { x.BankIban = EssCompletionHttpTests.Iban; });
        var responses = new List<(string Name, HttpResponseMessage Res)>
        {
            ("list", await fx.SendAsync(HttpMethod.Get, "/api/ess/completion", e.Token)),
            ("section", await fx.SendAsync(HttpMethod.Get, "/api/ess/completion/bank", e.Token)),
            ("unknown", await fx.SendAsync(HttpMethod.Get, "/api/ess/completion/nope", e.Token)),
            ("submit-challenge", await fx.SendAsync(HttpMethod.Post, "/api/ess/completion/bank/submit", e.Token, new { noticeVersion = "ess-1", values = new { iban = "x" } })),
            ("submit", await fx.SendAsync(HttpMethod.Post, "/api/ess/completion/person/submit", e.Token, new { noticeVersion = "ess-1", values = new { personalEmail = "a@b.co", phone = "0500000001" } })),
            ("submit-conflict", await fx.SendAsync(HttpMethod.Post, "/api/ess/completion/person/submit", e.Token, new { noticeVersion = "ess-1", values = new { personalEmail = "a@b.co", phone = "0500000001" } })),
            ("upload", await fx.SendRawAsync(UploadRequest("documents", "national_id_copy", Pdf), e.Token)),
            ("stepup", await fx.SendAsync(HttpMethod.Post, "/api/auth/step-up/purpose", e.Token, new { purpose = "ess.sensitive", password = EssFixture.Password })),
            ("hr-inbox", await fx.SendAsync(HttpMethod.Get, "/api/hr/profile-completion/inbox", fx.HrToken)),
        };
        foreach (var (name, res) in responses)
        {
            res.Headers.CacheControl.Should().NotBeNull(name);
            var cc = res.Headers.CacheControl!.ToString();
            cc.Should().Contain("no-store", name).And.Contain("no-cache", name);
            res.Dispose();
        }
    }

    [Fact]
    public async Task EssCompletion_MakesNoOutboundHttp()  // AC62
    {
        lock (RecordingHandler.Calls) RecordingHandler.Calls.Clear();
        var e = await fx.NewEmployeeAsync("SA");
        // The flow really ran (a 404 would make "no outbound calls" trivially true).
        using (var list = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion", e.Token)) list.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var sub = await fx.SendAsync(HttpMethod.Post, "/api/ess/completion/person/submit", e.Token, new { noticeVersion = "ess-1", values = new { personalEmail = "a@b.co", phone = "0500000001" } })) sub.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var up = await fx.SendRawAsync(UploadRequest("documents", "national_id_copy", Pdf), e.Token)) up.StatusCode.Should().Be(HttpStatusCode.Created);
        using (var inbox = await fx.SendAsync(HttpMethod.Get, "/api/hr/profile-completion/inbox", fx.HrToken)) inbox.StatusCode.Should().Be(HttpStatusCode.OK);
        lock (RecordingHandler.Calls) RecordingHandler.Calls.Should().BeEmpty();
        // and the controllers that serve the flow take no HTTP client and no AI service
        foreach (var type in new[] { typeof(EssCompletionController), typeof(HrProfileCompletionController), typeof(StepUpController) })
            type.GetConstructors().Single().GetParameters().Select(p => p.ParameterType.Name)
                .Should().NotContain(n => n.Contains("HttpClient") || n.Contains("Ai", StringComparison.Ordinal) || n.Contains("Llm") || n.Contains("Gateway"), type.Name);
    }

    [Fact]
    public async Task Mobile_PutProfileChangeRequest_StillRejectsBankAndIdKeys_AndLegacyShapesUnchanged()
    {
        var e = await fx.NewEmployeeAsync("SA");
        foreach (var key in new[] { "bankIban", "iban", "nationalId", "iqamaNumber", "idNumber", "salary", "status" })
        {
            using var res = await fx.SendAsync(HttpMethod.Put, "/api/ess/profile-change-request", e.Token,
                new { reason = "x", changes = new Dictionary<string, string> { [key] = "1" } });
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest, key);
        }
        using var ok = await fx.SendAsync(HttpMethod.Put, "/api/ess/profile-change-request", e.Token,
            new { reason = "Phone changed", changes = new Dictionary<string, string> { ["phone"] = "0500000042" } });
        ok.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await EssCompletionHttpTests.Json(ok);
        // every property the app already reads is still there with the same meaning
        foreach (var name in new[] { "id", "tenantId", "employeeId", "requestedChangesJson", "reason", "status", "containsSensitiveFields", "createdAtUtc", "createdBy", "decidedAtUtc", "decidedBy" })
            body.TryGetProperty(name, out _).Should().BeTrue(name);
        body.GetProperty("status").GetString().Should().Be("PendingHR");
        body.GetProperty("reason").GetString().Should().Be("Phone changed");
        // the new columns are additive and null for a legacy request
        body.GetProperty("section").ValueKind.Should().Be(JsonValueKind.Null);
        // the legacy request is still approvable through the legacy switch and applies
        using var approve = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{body.GetProperty("id").GetGuid()}/approve", fx.HrToken, new { notes = "ok" });
        approve.StatusCode.Should().Be(HttpStatusCode.OK);
        (await fx.WithDbAsync(db => db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == e.Id))).Phone.Should().Be("0500000042");
        // other mobile routes still answer with their usual shape
        using var policies = await fx.SendAsync(HttpMethod.Get, "/api/ess/policies", e.Token);
        policies.StatusCode.Should().Be(HttpStatusCode.OK);
        using var docs = await fx.SendAsync(HttpMethod.Get, "/api/ess/documents", e.Token);
        docs.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LegacyEmployeeCompletionRoutes_ResponseShapeUnchanged()
    {
        var e = await fx.NewEmployeeAsync("SA");
        using var hrRequest = await fx.SendAsync(HttpMethod.Post, $"/api/employee-completion/{e.Id}", fx.HrToken);
        hrRequest.StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var res in new[] { hrRequest, await fx.SendAsync(HttpMethod.Get, "/api/ess/employee-completion", e.Token) })
        {
            var names = (await EssCompletionHttpTests.Json(res)).EnumerateObject().Select(p => p.Name).ToArray();
            names.Should().BeEquivalentTo(["requested", "status", "selfServiceAvailable", "requestId", "changes", "profile", "reviewNote"]);
        }
        using var submit = await fx.SendAsync(HttpMethod.Post, "/api/ess/employee-completion/profile", e.Token, new { changes = new { phone = "0509999999" } });
        submit.StatusCode.Should().Be(HttpStatusCode.OK);
        var state = await EssCompletionHttpTests.Json(submit);
        state.GetProperty("status").GetString().Should().Be("PendingHR");
        // the legacy request carries no section and is read as person + emergency by the new list
        var row = await fx.WithDbAsync(db => db.EmployeeProfileChangeRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == state.GetProperty("requestId").GetGuid()));
        row.Section.Should().BeNull();
        using var list = await fx.SendAsync(HttpMethod.Get, "/api/ess/completion", e.Token);
        var sections = (await EssCompletionHttpTests.Json(list)).GetProperty("sections").EnumerateArray().ToDictionary(s => s.GetProperty("key").GetString()!, s => s.GetProperty("state").GetString());
        sections["person"].Should().Be(EssCompletion.Pending);
        sections["emergency"].Should().Be(EssCompletion.Pending);
    }

    [Fact]
    public void AllP2ProblemTitleKeys_HaveEnAndAr()
    {
        var file = FindUp("frontend/src/i18n/onboardingP2.ts");
        var text = File.ReadAllText(file);
        var split = text.IndexOf("export const onboardingP2Ar", StringComparison.Ordinal);
        split.Should().BeGreaterThan(0, "the file exports an English and an Arabic block");
        var en = text[..split]; var ar = text[split..];
        var keys = EssProblemCodes.All.Select(c => $"ess.completion.error.{c}")
            .Concat(EssSectionKeys.All.SelectMany(k => new[] { "title", "purpose", "hint" }.Select(p => $"ess.completion.section.{k}.{p}"))).ToArray();
        keys.Should().NotBeEmpty();
        foreach (var key in keys)
        {
            en.Should().Contain($"'{key}'", $"EN: {key}");
            var m = Regex.Match(ar, "'" + Regex.Escape(key) + @"':\s*'([^']*)'");
            m.Success.Should().BeTrue($"AR: {key}");
            m.Groups[1].Value.Should().MatchRegex(@"\p{IsArabic}", $"AR text for {key} is Arabic");
        }
        // every field label key the registry emits is translated both ways
        foreach (var f in EssSectionKeys.All.SelectMany(k => new NationalityClass?[] { null, NationalityClass.Saudi, NationalityClass.Expat }.SelectMany(c => ProfileSectionRegistry.FieldsFor(k, c))).Select(f => f.LabelKey).Distinct())
        {
            en.Should().Contain($"'{f}'"); ar.Should().Contain($"'{f}'");
        }
    }

    private static string FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relative);
    }
}
