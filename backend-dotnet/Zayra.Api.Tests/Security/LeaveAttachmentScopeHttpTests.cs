using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Leave attachment keys through the real pipeline. A key is the handle to an employee's medical document: the response to filing a leave
/// never hands it to someone who files it on the employee's behalf unless they are HR or Admin, and a CSV import only accepts a key that
/// is the key of a document the employee uploaded.
/// </summary>
public sealed class LeaveAttachmentScopeHttpTests(LeaveBatch1Fixture f) : IClassFixture<LeaveBatch1Fixture>
{
    private string NewKey(string file) => $"storage/documents/{f.TenantId:N}/{Guid.NewGuid():N}_{file}";

    private async Task<Guid> SeedDocumentAsync(int employeeId, string key)
    {
        var id = Guid.NewGuid();
        await f.WithDb(async db =>
        {
            db.EmployeeDocuments.Add(new EmployeeDocument
            {
                Id = id, TenantId = f.TenantId, CompanyId = f.CompanyId, EmployeeId = employeeId, DocumentType = "Other", FileName = "note.pdf",
                ContentType = "application/pdf", StorageUrl = key, ApprovalStatus = "Approved",
            });
            await db.SaveChangesAsync();
            return 0;
        });
        return id;
    }

    private async Task<HttpResponseMessage> SubmitWithAttachmentAsync(string who, Guid documentId)
    {
        var day = f.NextWorkday().AddDays(30);
        return await f.Send(HttpMethod.Post, "/api/leave/requests", f.Token(who), new
        {
            employeeId = f.EmployeeId, leaveTypeId = f.LeaveTypeId, startDate = day.ToString("yyyy-MM-dd"), endDate = day.ToString("yyyy-MM-dd"),
            dayType = "Full", reason = "Sick", isEmergency = false, attachmentDocumentId = documentId,
        });
    }

    [Fact]
    public async Task Filing_on_behalf_without_an_hr_role_does_not_return_the_attachment_key()
    {
        var key = NewKey("sicknote.pdf");
        var documentId = await SeedDocumentAsync(f.EmployeeId, key);
        using var response = await SubmitWithAttachmentAsync("clerk", documentId);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        text.Should().NotContain(key, "a clerk who files leave for someone must not be handed the key to their medical document");
    }

    [Fact]
    public async Task Filing_as_hr_still_returns_the_attachment_key()
    {
        var key = NewKey("sicknote2.pdf");
        var documentId = await SeedDocumentAsync(f.EmployeeId, key);
        using var response = await SubmitWithAttachmentAsync("hr", documentId);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        text.Should().Contain(key);
    }

    private async Task<(int Created, int Skipped)> ImportAsync(string attachmentKey)
    {
        var code = await f.WithDb(db => db.Employees.IgnoreQueryFilters().Where(e => e.Id == f.EmployeeId).Select(e => e.EmployeeCode).SingleAsync());
        var day = f.NextWorkday().AddDays(60);
        var csv = "EmployeeCode,LeaveTypeCode,StartDate,EndDate,DayType,AttachmentPath,Reason\n"
            + $"{code},AL,{day:yyyy-MM-dd},{day:yyyy-MM-dd},Full,{attachmentKey},Imported\n";
        using var response = await f.Send(HttpMethod.Post, "/api/leave/requests/import", f.Token("hr"), new { csvContent = csv });
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var json = System.Text.Json.JsonDocument.Parse(text).RootElement;
        return (json.GetProperty("created").GetInt32(), json.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public async Task Import_refuses_an_attachment_key_the_employee_does_not_own()
    {
        var foreignKey = NewKey("peer.pdf");
        await SeedDocumentAsync(f.PeerId, foreignKey);
        var (created, skipped) = await ImportAsync(foreignKey);
        created.Should().Be(0);
        skipped.Should().Be(1);
        (await ImportAsync(NewKey("invented.pdf"))).Created.Should().Be(0);
    }

    [Fact]
    public async Task Import_accepts_the_key_of_a_document_the_employee_uploaded()
    {
        var key = NewKey("mine.pdf");
        await SeedDocumentAsync(f.EmployeeId, key);
        var (created, _) = await ImportAsync(key);
        created.Should().Be(1);
    }
}
