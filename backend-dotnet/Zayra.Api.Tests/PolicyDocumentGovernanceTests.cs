using System.Text;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.AI;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.AI;

namespace Zayra.Api.Tests;

public sealed class PolicyDocumentGovernanceTests
{
    private sealed class NeverLlm : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
            => throw new InvalidOperationException("No provider call is allowed in these tests.");
    }
    private sealed class Recorder : IAiCallRecorder
    {
        public Task RecordAsync(AiCallRecord record, CancellationToken ct) => Task.CompletedTask;
    }
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static PolicyDocumentService Service(ZayraDbContext db) => new(db, new NeverLlm(),
        new AiOptions("none", "", "", "", "", "", 4096, true, false), new Recorder(), NullLogger<PolicyDocumentService>.Instance);

    [Theory]
    [InlineData("handbook.doc", "binary")]
    [InlineData("handbook.exe", "policy")]
    [InlineData("empty.txt", " \n \t ")]
    [InlineData("nul.txt", "policy\0bad")]
    public void ParserRefusesUnsupportedOrEmptyContent(string filename, string content)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(content));
        Action parse = () => PolicyTextParser.Extract(input, filename, "text/plain");
        parse.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ParserEnforcesTextLimit()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', PolicyTextParser.MaxTextCharacters + 1)));
        Action parse = () => PolicyTextParser.Extract(input, "policy.txt", "text/plain");
        parse.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ParserReadsWordParagraphsWithoutLosingWordBoundaries()
    {
        using var input = new MemoryStream();
        using (var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(input,
                   DocumentFormat.OpenXml.WordprocessingDocumentType.Document, true))
        {
            var main = word.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                new DocumentFormat.OpenXml.Wordprocessing.Body(
                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                        new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Annual leave is thirty days."))),
                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                        new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Leave is prorated from joining.")))));
            main.Document.Save();
        }
        input.Position = 0;
        PolicyTextParser.Extract(input, "policy.docx", "application/octet-stream").Text
            .Should().Be("Annual leave is thirty days.\nLeave is prorated from joining.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParserReadsTextPdfAndRefusesImageOnlyPdf(bool hasText)
    {
        // A valid minimal PDF, exercising the actual PdfPig parser without another renderer.
        var commands = hasText ? "BT /F1 12 Tf 72 720 Td (Annual leave is thirty days.) Tj ET" : "0 0 50 50 re S";
        var objects = new[] {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {commands.Length} >>\nstream\n{commands}\nendstream" };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var value in objects)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{offsets.Count} 0 obj\n{value}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(pdf.ToString()));
        if (hasText)
            PolicyTextParser.Extract(input, "policy.pdf", "application/pdf").Text.Should().Contain("Annual leave is thirty days.");
        else
        {
            Action parse = () => PolicyTextParser.Extract(input, "scan.pdf", "application/pdf");
            parse.Should().Throw<InvalidDataException>().WithMessage("*No readable text*");
        }
    }

    [Fact]
    public async Task UploadIsDraftAndTextReconstructsTheHashedVersion()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid(); var user = Guid.NewGuid();
        var svc = Service(db);
        var text = new string('a', 799) + "🌍 Annual leave policy\r\n" + new string('a', 1700) + ". End of policy.";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var doc = await svc.UploadAsync(tenant, user, input, "policy.txt", "text/plain", default);
        doc.Status.Should().Be("Ready");
        doc.PublicationStatus.Should().Be("Draft");
        var own = new PolicyReadScope(true, true, [], user);
        var loaded = await svc.TextAsync(tenant, doc.Id, own, default);
        loaded!.Text.Should().Be(text.Replace("\r\n", "\n"));
        using var again = new MemoryStream(Encoding.UTF8.GetBytes(loaded.Text));
        PolicyTextParser.Extract(again, "policy.txt", "text/plain").ContentSha256.Should().Be(doc.ContentSha256);
        (await svc.TextAsync(tenant, doc.Id, own with { UserId = Guid.NewGuid() }, default)).Should().BeNull();
        (await svc.ListAsync(tenant, new(false, true, [], user), default)).Should().BeEmpty();
    }

    [Fact]
    public async Task EmployeeRetrievalExcludesOtherTenantsCompaniesDraftsWithdrawnFutureAndExpiredBeforeLlm()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid(); var company = Guid.NewGuid();
        PolicyDocument Add(Guid tid, Guid cid, string publication, DateTime? from, DateTime? to, string content)
        {
            var doc = new PolicyDocument { TenantId = tid, CompanyId = cid, OriginalName = content, Status = "Ready",
                PublicationStatus = publication, EffectiveFromUtc = from, EffectiveToUtc = to, ContentSha256 = new string('a',64) };
            db.PolicyDocuments.Add(doc);
            db.DocumentChunks.Add(new() { TenantId = tid, Document = doc, DocumentId = doc.Id, Content = "Annual leave " + content });
            return doc;
        }
        var now = DateTime.UtcNow;
        var allowed = Add(tenant, company, "Published", now.AddDays(-1), null, "PUBLIC");
        Add(Guid.NewGuid(), company, "Published", now.AddDays(-1), null, "OTHER TENANT");
        Add(tenant, Guid.NewGuid(), "Published", now.AddDays(-1), null, "OTHER COMPANY");
        Add(tenant, company, "Draft", now.AddDays(-1), null, "DRAFT");
        Add(tenant, company, "Withdrawn", now.AddDays(-1), null, "WITHDRAWN");
        Add(tenant, company, "Published", now.AddDays(1), null, "FUTURE");
        Add(tenant, company, "Published", now.AddDays(-2), now.AddDays(-1), "EXPIRED");
        await db.SaveChangesAsync();
        var scope = new PolicyReadScope(false, false, [company], Guid.NewGuid());
        var svc = Service(db);
        (await svc.ListAsync(tenant, scope, default)).Should().ContainSingle().Which.Id.Should().Be(allowed.Id);
        var answer = await svc.AskAsync(tenant, scope.UserId, "Employee", "annual leave policy", scope, default);
        answer.Citations.Should().ContainSingle().Which.DocumentId.Should().Be(allowed.Id);
        answer.Citations[0].VersionHash.Should().Be(new string('a',64));
        answer.Answer.Should().Contain("PUBLIC").And.NotContain("OTHER").And.NotContain("DRAFT");
    }

    [Fact]
    public async Task ExhaustedQuotaReturnsExcerptsWithoutCallingConfiguredOllama()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid(); var company = Guid.NewGuid();
        var doc = new PolicyDocument { TenantId = tenant, CompanyId = company, OriginalName = "Approved", Status = "Ready",
            PublicationStatus = "Published", EffectiveFromUtc = DateTime.UtcNow.AddDays(-1), ContentSha256 = new string('a', 64) };
        db.PolicyDocuments.Add(doc);
        db.DocumentChunks.Add(new() { TenantId = tenant, Document = doc, DocumentId = doc.Id, Content = "Annual leave allowance is thirty days." });
        db.TenantAiUsages.Add(new() { TenantId = tenant, YearMonth = DateTime.UtcNow.Year * 100 + DateTime.UtcNow.Month, TokensUsed = 50_000 });
        await db.SaveChangesAsync();
        var svc = new PolicyDocumentService(db, new NeverLlm(),
            new AiOptions("ollama", "test-model", "", "", "https://ollama.test", "", 4096, true, false), new Recorder(), NullLogger<PolicyDocumentService>.Instance);
        var result = await svc.AskAsync(tenant, Guid.NewGuid(), "Employee", "annual leave allowance", new(false, false, [company], null), default);
        result.Provider.Should().Be("fallback");
        result.DegradedReason.Should().Contain("monthly AI allowance");
        result.Citations.Should().ContainSingle();
    }

    [Fact]
    public async Task SoftDeletionRetainsSourceAndPublishedDocumentsRequireWithdrawal()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid(); var user = Guid.NewGuid(); var svc = Service(db);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("Annual leave policy source."));
        var uploaded = await svc.UploadAsync(tenant, user, input, "policy.txt", "text/plain", default);
        (await svc.DeleteAsync(tenant, uploaded.Id, default)).Should().BeTrue();
        (await db.DocumentChunks.IgnoreQueryFilters().CountAsync(c => c.DocumentId == uploaded.Id)).Should().Be(1);
        (await svc.ListAsync(tenant, new(true, true, [], user, true), default)).Should().BeEmpty();
        var published = new PolicyDocument { TenantId = tenant, PublicationStatus = "Withdrawn", PublishedAtUtc = DateTime.UtcNow.AddDays(-1) };
        db.PolicyDocuments.Add(published);
        await db.SaveChangesAsync();
        (await svc.DeleteAsync(tenant, published.Id, default)).Should().BeFalse();
    }

    [Fact]
    public async Task EmployeeEndpointRequiresEssReadAndDatabaseEmployeeLink()
    {
        await using var db = Db();
        var svc = Service(db);
        var controller = new PolicyDocumentController(svc, db);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim("tenant_id", Guid.NewGuid().ToString()), new Claim("permission", "ess.read"),
            new Claim("employee_id", "999"), new Claim(ClaimTypes.Role, "Employee") }, "test"));
        (await controller.EmployeeDocuments(default)).Should().BeOfType<ForbidResult>();
        (await controller.EmployeeAsk(new("annual leave"), default)).Should().BeOfType<ForbidResult>();
    }
}
