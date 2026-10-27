using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Application.Esign;
using Zayra.Api.Models;
using Zayra.Api.Tests.Ess;

namespace Zayra.Api.Tests.Esign;

/// <summary>P4 over the real pipeline: sign (ESS and on HR's device), verify, tamper, subject bar, legacy route, no outbound calls.</summary>
[Collection("EsignP4")]
public class EsignHttpTests(EsignFixture fx)
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private static async Task<string> Code(HttpResponseMessage r) => (await Json(r)).GetProperty("code").GetString()!;

    private Task<EmployeePolicyAcknowledgement> AckAsync(Guid id) =>
        fx.WithDbAsync(db => db.EmployeePolicyAcknowledgements.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == id));

    [Fact]
    public async Task Sign_Ess_RecordsAllAuditFields()  // AC70, AC72
    {
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e);
        var before = DateTime.UtcNow;
        using var res = await fx.SignAsync(e, doc, "  layla   HASSAN ");
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var after = DateTime.UtcNow;
        var body = await Json(res);
        var ack = await AckAsync(body.GetProperty("ackId").GetGuid());
        ack.Method.Should().Be("esign_ess");
        ack.UserId.Should().Be(e.UserId); ack.EmployeeId.Should().Be(e.Id); ack.PolicyId.Should().Be(doc.Id); ack.WitnessUserId.Should().BeNull();
        ack.TypedName.Should().Be("layla   HASSAN", "the name is stored as typed, trimmed");
        ack.DocumentSha256.Should().Be(doc.Sha256);
        ack.AcknowledgedAtUtc.Should().BeOnOrAfter(before.AddSeconds(-1)).And.BeOnOrBefore(after.AddSeconds(1));
        ack.IpAddress.Should().NotBeNullOrEmpty(); ack.UserAgent.Should().Be("EsignTest/1.0");
        ack.ConsentVersion.Should().Be(ConsentCatalog.Current); ack.ConsentTextSha256.Should().Be(ConsentCatalog.TextSha256(ConsentCatalog.Current));
        ack.Language.Should().Be("en"); ack.DeviceId.Should().Be("dev-1"); ack.AuthMethod.Should().StartWith("password");
        ack.StepUpJti.Should().NotBeNull(); ack.StepUpAtUtc.Should().NotBeNull();
        ack.ChainSeq.Should().BeGreaterThan(0); ack.PrevHash.Should().HaveLength(64); ack.RecordHash.Should().HaveLength(64);
        ack.CertificateDocumentId.Should().NotBeNull();
        body.GetProperty("recordHash").GetString().Should().Be(ack.RecordHash);
        body.GetProperty("verificationCode").GetString().Should().Be(VerificationCode.From(ack.RecordHash!));
        // the stored evidence verifies against its own hash
        SignatureChain.IsRecordValid(ack.PrevHash!, ack.RecordHash!, new AckEvidence(ack.TenantId, ack.EmployeeId, ack.PolicyId, ack.DocumentSha256!, ack.TypedName!,
            ack.AcknowledgedAtUtc, ack.IpAddress!, ack.UserAgent, ack.UserId!.Value, ack.WitnessUserId, ack.ConsentTextSha256!, ack.Method)).Should().BeTrue();
        // the certificate is a stored, immutable document of the employee
        var cert = await fx.WithDbAsync(db => db.EmployeeDocuments.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == ack.CertificateDocumentId));
        cert.DocumentType.Should().Be(EsignConstants.DocumentType); cert.EmployeeId.Should().Be(e.Id); cert.ContentType.Should().Be("application/pdf");
        fx.Storage.Objects[cert.StorageUrl].Take(5).Should().Equal("%PDF-"u8.ToArray());
        // the document itself is untouched
        fx.Storage.Objects[doc.Key].Should().Equal(doc.Bytes);
    }

    [Fact]
    public async Task ClickSign_RejectsClientTime_Http()
    {
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e);
        var step = await fx.StepUpAsync(e);
        var forged = new { documentSha256 = doc.Sha256, consentVersion = ConsentCatalog.Current, typedName = "Layla Hassan", language = "en", signedAtUtc = "2001-01-01T00:00:00Z", acknowledgedAtUtc = "2001-01-01T00:00:00Z" };
        using var res = await fx.SendAsync(HttpMethod.Post, $"/api/ess/signatures/{doc.Id}/sign", e.Token, forged, step, Guid.NewGuid().ToString("N"));
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        (await AckAsync((await Json(res)).GetProperty("ackId").GetGuid())).AcknowledgedAtUtc.Year.Should().BeGreaterThan(2020);
    }

    [Fact]
    public async Task Sign_NameMismatch_422_AndNothingRecorded()  // AC71
    {
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e);
        using var res = await fx.SignAsync(e, doc, "Somebody Else");
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = await Json(res);
        problem.GetProperty("code").GetString().Should().Be("typed_name_mismatch");
        problem.GetProperty("titleKey").GetString().Should().Be("esign.error.typed_name_mismatch");
        (await fx.WithDbAsync(db => db.EmployeePolicyAcknowledgements.IgnoreQueryFilters().CountAsync(a => a.PolicyId == doc.Id))).Should().Be(0);
        // the Arabic name on the record is accepted too, with diacritics
        using var ok = await fx.SignAsync(e, doc, "لَيْلَى حسن");
        ok.StatusCode.Should().Be(HttpStatusCode.Created, await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sign_WithoutStepUp_Returns401Challenge_StaleOrWrongPurposeToo()
    {
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e);
        using var none = await fx.SendAsync(HttpMethod.Post, $"/api/ess/signatures/{doc.Id}/sign", e.Token, EsignFixture.Body(doc, "Layla Hassan"));
        none.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Code(none)).Should().Be("step_up_required");
        none.Headers.WwwAuthenticate.ToString().Should().Contain("StepUp").And.Contain("esign");
        // a token for a different purpose does not open a signature
        var wrong = await fx.StepUpAsync(e, StepUpPurposes.EssSensitive);
        using var res = await fx.SendAsync(HttpMethod.Post, $"/api/ess/signatures/{doc.Id}/sign", e.Token, EsignFixture.Body(doc, "Layla Hassan"), wrong);
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await fx.WithDbAsync(db => db.EmployeePolicyAcknowledgements.IgnoreQueryFilters().CountAsync(a => a.PolicyId == doc.Id))).Should().Be(0);
    }

    [Fact]
    public async Task Sign_AlteredBytesAfterRequest_409DocumentChanged()  // 3b hash match
    {
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e);
        fx.Storage.Objects[doc.Key] = [.. doc.Bytes, .. "tampered"u8.ToArray()];   // HR replaced the file after the employee opened it
        using var res = await fx.SignAsync(e, doc, "Layla Hassan");
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Code(res)).Should().Be("document_changed");
        (await fx.WithDbAsync(db => db.EmployeePolicyAcknowledgements.IgnoreQueryFilters().CountAsync(a => a.PolicyId == doc.Id))).Should().Be(0);
    }

    [Fact]
    public async Task Sign_StaleConsentVersion_409()
    {
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e);
        using var res = await fx.SignAsync(e, doc, "Layla Hassan", consent: "ack-0");
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Code(res)).Should().Be("consent_version_stale");
    }

    [Fact]
    public async Task Sign_Replay_ReturnsConflictWithExistingAck_AndStepUpIsSingleUse()
    {
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e); var doc2 = await fx.NewDocumentAsync(e);
        var step = await fx.StepUpAsync(e);
        using var first = await fx.SignAsync(e, doc, "Layla Hassan", step);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var ackId = (await Json(first)).GetProperty("ackId").GetGuid();
        // the same document again (a double click, a retry): refused, pointing at the record that exists
        using var again = await fx.SignAsync(e, doc, "Layla Hassan");
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var dup = await Json(again);
        dup.GetProperty("code").GetString().Should().Be("already_signed");
        dup.GetProperty("ackId").GetGuid().Should().Be(ackId);
        // the same step-up proof does not open a second signature
        using var reuse = await fx.SignAsync(e, doc2, "Layla Hassan", step);
        reuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Code(reuse)).Should().Be("step_up_required");
        (await fx.WithDbAsync(db => db.EmployeePolicyAcknowledgements.IgnoreQueryFilters().CountAsync(a => a.PolicyId == doc2.Id))).Should().Be(0);
    }

    [Fact]
    public async Task Verify_Intact_ThenAlteredStoredBytes_Fails_AndFlagsDocument()  // AC74
    {
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e);
        using var signed = await fx.SignAsync(e, doc, "Layla Hassan");
        var ackId = (await Json(signed)).GetProperty("ackId").GetGuid();
        using var ok = await fx.SendAsync(HttpMethod.Get, $"/api/ess/signatures/{ackId}/verify", e.Token);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var good = await Json(ok);
        good.GetProperty("valid").GetBoolean().Should().BeTrue(); good.GetProperty("documentIntact").GetBoolean().Should().BeTrue(); good.GetProperty("chainIntact").GetBoolean().Should().BeTrue();

        fx.Storage.Objects[doc.Key] = [.. doc.Bytes, .. "x"u8.ToArray()];
        using var bad = await fx.SendAsync(HttpMethod.Get, $"/api/employees/{e.Id}/signatures/{ackId}/verify", fx.HrToken);
        bad.StatusCode.Should().Be(HttpStatusCode.OK, "a failed verification is a result, not an error");
        var broken = await Json(bad);
        broken.GetProperty("valid").GetBoolean().Should().BeFalse(); broken.GetProperty("documentIntact").GetBoolean().Should().BeFalse();
        (await fx.WithDbAsync(db => db.EmployeeDocuments.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == doc.Id))).IntegrityFlaggedAtUtc.Should().NotBeNull();
        (await fx.WithDbAsync(db => db.EmployeeSelfServiceAuditLogs.IgnoreQueryFilters().CountAsync(a => a.Action == "esign.integrity_failed"))).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Verify_DoesNotRevealOtherEmployeesRecords()
    {
        var a = await fx.NewEmployeeAsync(); var b = await fx.NewEmployeeAsync("Omar Saleh", "عمر صالح");
        var doc = await fx.NewDocumentAsync(a);
        using var signed = await fx.SignAsync(a, doc, "Layla Hassan");
        var ackId = (await Json(signed)).GetProperty("ackId").GetGuid();
        using var res = await fx.SendAsync(HttpMethod.Get, $"/api/ess/signatures/{ackId}/verify", b.Token);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sign_OnDevice_RecordsHrAndEmployee_EmployeeReauthRequired()  // AC73
    {
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e);
        // HR's session alone is not enough: no employee proof, no signature
        using var bare = await fx.SendAsync(HttpMethod.Post, $"/api/employees/{e.Id}/signatures/{doc.Id}/sign-on-device", fx.HrToken, EsignFixture.Body(doc, "Layla Hassan"));
        bare.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Code(bare)).Should().Be("step_up_required");
        // the employee types their own password on HR's device
        using var auth = await fx.SendAsync(HttpMethod.Post, "/api/auth/step-up/employee-device", fx.HrToken, new { employeeId = e.Id, documentId = doc.Id, documentSha256 = doc.Sha256, password = EsignFixture.Password });
        auth.StatusCode.Should().Be(HttpStatusCode.OK, await auth.Content.ReadAsStringAsync());
        var token = (await Json(auth)).GetProperty("stepUpToken").GetString()!;
        using var res = await fx.SendAsync(HttpMethod.Post, $"/api/employees/{e.Id}/signatures/{doc.Id}/sign-on-device", fx.HrToken, EsignFixture.Body(doc, "Layla Hassan"), token, Guid.NewGuid().ToString("N"));
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var ack = await AckAsync((await Json(res)).GetProperty("ackId").GetGuid());
        ack.Method.Should().Be("esign_hr_device");
        ack.UserId.Should().Be(e.UserId, "the signer is the employee");
        ack.WitnessUserId.Should().Be(fx.HrUserId, "HR is recorded as the person who held the device");
    }

    [Fact]
    public async Task Sign_OnDevice_HrOwnPassword_Rejected()
    {
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e);
        using var res = await fx.SendAsync(HttpMethod.Post, "/api/auth/step-up/employee-device", fx.HrToken, new { employeeId = e.Id, documentId = doc.Id, documentSha256 = doc.Sha256, password = fx.HrPassword });
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var problem = await Json(res);
        problem.GetProperty("code").GetString().Should().Be("employee_device_auth_failed");
        problem.TryGetProperty("stepUpToken", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Sign_OnDevice_TokenForOtherEmployee_Rejected()
    {
        var a = await fx.NewEmployeeAsync(); var b = await fx.NewEmployeeAsync("Omar Saleh", "عمر صالح");
        var docA = await fx.NewDocumentAsync(a); var docB = await fx.NewDocumentAsync(b);
        using var auth = await fx.SendAsync(HttpMethod.Post, "/api/auth/step-up/employee-device", fx.HrToken, new { employeeId = b.Id, documentId = docB.Id, documentSha256 = docB.Sha256, password = EsignFixture.Password });
        var tokenForB = (await Json(auth)).GetProperty("stepUpToken").GetString()!;
        using var res = await fx.SendAsync(HttpMethod.Post, $"/api/employees/{a.Id}/signatures/{docA.Id}/sign-on-device", fx.HrToken, EsignFixture.Body(docA, "Layla Hassan"), tokenForB, Guid.NewGuid().ToString("N"));
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await fx.WithDbAsync(db => db.EmployeePolicyAcknowledgements.IgnoreQueryFilters().CountAsync(x => x.PolicyId == docA.Id))).Should().Be(0);
    }

    [Fact]
    public async Task Hr_CannotSignForEmployee_ViaEssRoute()  // subject bar
    {
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e);
        using var res = await fx.SendAsync(HttpMethod.Post, $"/api/ess/signatures/{doc.Id}/sign", fx.HrToken, EsignFixture.Body(doc, "Layla Hassan"), "anything", Guid.NewGuid().ToString("N"));
        ((int)res.StatusCode).Should().BeInRange(401, 409).And.NotBe(201);
        (await fx.WithDbAsync(db => db.EmployeePolicyAcknowledgements.IgnoreQueryFilters().CountAsync(a => a.PolicyId == doc.Id))).Should().Be(0);
        // the device route refuses HR signing for HR's own record (HR must use ESS for themselves)
    }

    [Fact]
    public async Task Sign_EmployeeCannotSignOthersDocument_404()
    {
        var a = await fx.NewEmployeeAsync(); var b = await fx.NewEmployeeAsync("Omar Saleh", "عمر صالح");
        var docA = await fx.NewDocumentAsync(a);
        using var res = await fx.SignAsync(b, docA, "Omar Saleh");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var consent = await fx.SendAsync(HttpMethod.Get, $"/api/ess/signatures/{docA.Id}/consent?lang=en", b.Token);
        consent.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var file = await fx.SendAsync(HttpMethod.Get, $"/api/ess/signatures/{docA.Id}/document", b.Token);
        file.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var list = await fx.SendAsync(HttpMethod.Get, "/api/ess/signatures", b.Token);
        (await Json(list)).EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Sign_RateLimit_11thPerHour_429()
    {
        var e = await fx.NewEmployeeAsync();
        for (var i = 0; i < EsignConstants.MaxSignsPerHour; i++)
        {
            var d = await fx.NewDocumentAsync(e);
            using var ok = await fx.SignAsync(e, d, "Layla Hassan");
            ok.StatusCode.Should().Be(HttpStatusCode.Created, $"signature {i + 1}: {await ok.Content.ReadAsStringAsync()}");
        }
        var eleventh = await fx.NewDocumentAsync(e);
        using var res = await fx.SignAsync(e, eleventh, "Layla Hassan");
        res.StatusCode.Should().Be((HttpStatusCode)429);
        (await Code(res)).Should().Be("rate_limited");
        res.Headers.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task EssList_ShowsAwaitingThenSigned_AndConsentIsBilingualAndVersioned()
    {
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e); await fx.NewDocumentAsync(e, requireSignature: false);
        using var list = await fx.SendAsync(HttpMethod.Get, "/api/ess/signatures", e.Token);
        var rows = (await Json(list)).EnumerateArray().ToList();
        rows.Should().ContainSingle("only documents HR asked for are listed");
        rows[0].GetProperty("status").GetString().Should().Be("Awaiting");
        rows[0].GetProperty("documentId").GetGuid().Should().Be(doc.Id);
        using var en = await fx.SendAsync(HttpMethod.Get, $"/api/ess/signatures/{doc.Id}/consent?lang=en", e.Token);
        var c = await Json(en);
        c.GetProperty("consentVersion").GetString().Should().Be(ConsentCatalog.Current); c.GetProperty("textSha256").GetString().Should().Be(ConsentCatalog.TextSha256(ConsentCatalog.Current));
        c.GetProperty("text").GetString().Should().Be(ConsentCatalog.Text(ConsentCatalog.Current, "en"));
        c.GetProperty("textAr").GetString().Should().Be(ConsentCatalog.Text(ConsentCatalog.Current, "ar"));
        c.GetProperty("order").EnumerateArray().Select(x => x.GetString()).Should().Equal("en", "ar");
        using var ar = await fx.SendAsync(HttpMethod.Get, $"/api/ess/signatures/{doc.Id}/consent?lang=ar", e.Token);
        (await Json(ar)).GetProperty("order").EnumerateArray().Select(x => x.GetString()).Should().Equal("ar", "en");
        c.GetProperty("documentSha256").GetString().Should().Be(doc.Sha256);
        (await fx.SignAsync(e, doc, "Layla Hassan")).StatusCode.Should().Be(HttpStatusCode.Created);
        using var after = await fx.SendAsync(HttpMethod.Get, "/api/ess/signatures", e.Token);
        (await Json(after)).EnumerateArray().Single().GetProperty("status").GetString().Should().Be("Signed");
    }

    [Fact]
    public async Task HrRequest_RefusesNonPdfRestrictedAndNeedsBothPermissions()
    {
        var e = await fx.NewEmployeeAsync();
        var plain = await fx.NewDocumentAsync(e, requireSignature: false);
        using var noDocs = await fx.SendAsync(HttpMethod.Post, $"/api/employees/{e.Id}/signature-requests", fx.HrNoDocsToken, new { documentId = plain.Id });
        noDocs.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var ok = await fx.SendAsync(HttpMethod.Post, $"/api/employees/{e.Id}/signature-requests", fx.HrToken, new { documentId = plain.Id });
        ok.StatusCode.Should().Be(HttpStatusCode.Created, await ok.Content.ReadAsStringAsync());
        (await fx.WithDbAsync(db => db.EmployeeDocuments.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == plain.Id))).SignatureRequiredAtUtc.Should().NotBeNull();

        var notPdf = await fx.NewDocumentAsync(e, bytes: "PK\u0003\u0004 not a pdf"u8.ToArray(), requireSignature: false);
        using var np = await fx.SendAsync(HttpMethod.Post, $"/api/employees/{e.Id}/signature-requests", fx.HrToken, new { documentId = notPdf.Id });
        np.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity); (await Code(np)).Should().Be("document_not_signable");
        var restricted = await fx.NewDocumentAsync(e, requireSignature: false, type: RestrictedEmployeeDocumentTypes.QiwaEvidence);
        using var rs = await fx.SendAsync(HttpMethod.Post, $"/api/employees/{e.Id}/signature-requests", fx.HrToken, new { documentId = restricted.Id });
        rs.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var other = await fx.NewEmployeeAsync("Omar Saleh", "عمر صالح");
        using var wrongEmployee = await fx.SendAsync(HttpMethod.Post, $"/api/employees/{other.Id}/signature-requests", fx.HrToken, new { documentId = plain.Id });
        wrongEmployee.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SignedDocumentAndCertificate_CannotBeEditedOrArchived()
    {
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e);
        using var signed = await fx.SignAsync(e, doc, "Layla Hassan");
        var ack = await AckAsync((await Json(signed)).GetProperty("ackId").GetGuid());
        foreach (var id in new[] { doc.Id, ack.CertificateDocumentId!.Value })
        {
            using var put = await fx.SendAsync(HttpMethod.Put, $"/api/employees/{e.Id}/documents/{id}", fx.HrToken, new { notes = "edited" });
            put.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a signed document is evidence");
        }
        // archive needs employees.approve, which this HR lacks (403); the guard itself is proven by the service-level call below
        using var scope = fx.Host.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<Zayra.Api.Application.Employees.IEmployeeManagementService>();
        foreach (var id in new[] { doc.Id, ack.CertificateDocumentId!.Value })
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ArchiveDocumentAsync(fx.TenantId, e.Id, id, new Zayra.Api.Application.Auth.RequestContext("127.0.0.1", "test", fx.HrUserId, fx.TenantId), default));
    }

    [Fact]
    public async Task Signed_PdfAndCertificate_ServedAsAttachmentNosniff_SameAuthorisationAsDocuments()
    {
        var e = await fx.NewEmployeeAsync(); var other = await fx.NewEmployeeAsync("Omar Saleh", "عمر صالح");
        var doc = await fx.NewDocumentAsync(e);
        using var signed = await fx.SignAsync(e, doc, "Layla Hassan");
        var ackId = (await Json(signed)).GetProperty("ackId").GetGuid();
        var certId = (await AckAsync(ackId)).CertificateDocumentId!.Value;
        using var pdf = await fx.SendAsync(HttpMethod.Get, $"/api/ess/signatures/{doc.Id}/document", e.Token);
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        pdf.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        pdf.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        pdf.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await pdf.Content.ReadAsByteArrayAsync()).Should().Equal(doc.Bytes);
        using var cert = await fx.SendAsync(HttpMethod.Get, $"/api/ess/signatures/{ackId}/certificate", e.Token);
        cert.StatusCode.Should().Be(HttpStatusCode.OK);
        cert.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        cert.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        // another employee gets nothing; HR gets it through the same route and permissions as every employee document
        (await fx.SendAsync(HttpMethod.Get, $"/api/ess/signatures/{ackId}/certificate", other.Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var hrCert = await fx.SendAsync(HttpMethod.Get, $"/api/employees/documents/{certId}/download", fx.HrToken);
        hrCert.StatusCode.Should().Be(HttpStatusCode.OK);
        hrCert.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        (await fx.SendAsync(HttpMethod.Get, $"/api/employees/documents/{certId}/download", fx.HrNoDocsToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task LegacyAcknowledge_UnchangedForNonRequiredDocs_409SignatureRequiredOtherwise()
    {
        var e = await fx.NewEmployeeAsync();
        var plain = await fx.NewDocumentAsync(e, requireSignature: false, type: "Company Policy");
        var required = await fx.NewDocumentAsync(e, requireSignature: true, type: "Company Policy");
        using var ok = await fx.SendAsync(HttpMethod.Post, $"/api/ess/policies/{plain.Id}/acknowledge", e.Token);
        ok.StatusCode.Should().Be(HttpStatusCode.Created, "the old click-acknowledge keeps working for documents that do not need a signature");
        var legacy = await fx.WithDbAsync(db => db.EmployeePolicyAcknowledgements.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.PolicyId == plain.Id));
        legacy.Method.Should().Be("legacy_click"); legacy.RecordHash.Should().BeNull();
        using var blocked = await fx.SendAsync(HttpMethod.Post, $"/api/ess/policies/{required.Id}/acknowledge", e.Token);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await Json(blocked);
        body.GetProperty("code").GetString().Should().Be("signature_required");
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("messageAr").GetString().Should().MatchRegex(@"\p{IsArabic}");
        (await fx.WithDbAsync(db => db.EmployeePolicyAcknowledgements.IgnoreQueryFilters().CountAsync(a => a.PolicyId == required.Id))).Should().Be(0);
    }

    [Fact]
    public async Task Mobile_PolicyList_StillReturnsSameShape()
    {
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e, requireSignature: true, type: "Company Policy", fileName: "policy.pdf");
        using var res = await fx.SendAsync(HttpMethod.Get, "/api/ess/policies", e.Token);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = (await Json(res)).EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == doc.Id);
        row.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["id", "documentType", "fileName", "expiryDate", "approvalStatus"]);
    }

    [Fact]
    public async Task Sign_MakesNoOutboundHttp_And_NoNewPackageReferences()  // AC75
    {
        int Count() { lock (RecordingHandler.Calls) return RecordingHandler.Calls.Count; }
        var e = await fx.NewEmployeeAsync();
        var doc = await fx.NewDocumentAsync(e);
        var before = Count();
        (await fx.SignAsync(e, doc, "Layla Hassan")).StatusCode.Should().Be(HttpStatusCode.Created);
        using var verify = await fx.SendAsync(HttpMethod.Get, "/api/ess/signatures", e.Token);
        Count().Should().Be(before, "signing, certifying and verifying call no external service");

        // the dependency allow-list: P4 adds no package (no signature vendor, no PDF merger)
        var csproj = File.ReadAllText(EsignUnitTests.FindUp("backend-dotnet/Zayra.Api/Zayra.Api.csproj"));
        var packages = Regex.Matches(csproj, "<PackageReference Include=\"([^\"]+)\"").Select(m => m.Groups[1].Value).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        packages.Should().Equal(AllowedPackages, "P4 is built-in: no vendor SDK and no new dependency");
    }

    private static readonly string[] AllowedPackages = ["AWSSDK.S3", "DocumentFormat.OpenXml", "MailKit", "Microsoft.AspNetCore.Authentication.JwtBearer", "Microsoft.AspNetCore.DataProtection.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Design", "Microsoft.Extensions.Caching.StackExchangeRedis", "Npgsql.EntityFrameworkCore.PostgreSQL", "OpenTelemetry.Exporter.OpenTelemetryProtocol", "OpenTelemetry.Extensions.Hosting", "OpenTelemetry.Instrumentation.AspNetCore", "OpenTelemetry.Instrumentation.Http", "OpenTelemetry.Instrumentation.Runtime", "PdfPig", "QuestPDF", "SkiaSharp", "SkiaSharp.NativeAssets.Linux.NoDependencies", "StackExchange.Redis", "Swashbuckle.AspNetCore", "System.IO.Packaging", "System.Security.Cryptography.Pkcs", "System.Security.Cryptography.Xml"];
}
