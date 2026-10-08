using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Three live defects in the self-service controller:
///   1. an HR user could approve or reject a change to their OWN profile (the subject deciding), and the
///      submitter of a change could decide it;
///   2. a rejected profile change left no audit row, while an approval did;
///   3. GET api/ess/policies listed every colleague's document whose type contained "Policy" (file name,
///      expiry), and the acknowledgement accepted a colleague's document too.
/// Each refusal test fails on the old code; each positive control proves the fix did not over-block.
/// InMemory EF. No Postgres.
/// </summary>
public class EssProfileChangeSubjectAndPoliciesTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class NoStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => throw new FileNotFoundException(storageUrl);
        public string ResolvePath(string storageUrl) => storageUrl;
    }

    private sealed class StubLetterService : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }

    private sealed class StubNotificationService : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>A controller for a caller with the given login id, optional employee link and ESS rights.</summary>
    private static EmployeeSelfServiceController Controller(ZayraDbContext db, Guid tenantId, Guid userId, int? employeeId = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("permission", "ess.read"),
            new("permission", "ess.write"),
            new("permission", "employees.write"),
        };
        if (employeeId is int e) claims.Add(new Claim("employee_id", e.ToString()));
        var storage = new NoStorage();
        var controller = new EmployeeSelfServiceController(
            db, new StubLetterService(), new PdfRenderGate(1),
            new Zayra.Api.Infrastructure.Leave.LeaveService(db, new ApprovalRouter(db)),
            new Zayra.Api.Infrastructure.Attendance.AttendanceService(db, new StubNotificationService(), new StubHttpClientFactory()),
            new HrLetterIssuer(db, new StubLetterService(), storage),
            storage);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
        return controller;
    }

    private static Employee SeedEmployee(ZayraDbContext db, Guid tenantId, string code, Guid? userAccountId = null)
    {
        var e = new Employee
        {
            TenantId = tenantId, EmployeeCode = code, FullName = $"Employee {code}", Department = "HR",
            Designation = "Officer", JobTitle = "Officer", Status = "Active", JoiningDate = DateTime.UtcNow.Date.AddYears(-1),
            UserAccountId = userAccountId, Phone = "0500000000",
        };
        db.Employees.Add(e);
        db.SaveChanges();
        return e;
    }

    private static EmployeeProfileChangeRequest SeedChange(ZayraDbContext db, Guid tenantId, int employeeId, Guid? createdBy)
    {
        var change = new EmployeeProfileChangeRequest
        {
            TenantId = tenantId, EmployeeId = employeeId, CreatedBy = createdBy,
            RequestedChangesJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["phone"] = "0599999999" }),
            Reason = "New number",
        };
        db.EmployeeProfileChangeRequests.Add(change);
        db.SaveChanges();
        return change;
    }

    private static string ErrorCodeOf(IActionResult result)
    {
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(bad.Value));
        return doc.RootElement.GetProperty("error").GetString()!;
    }

    // ── 1. The subject never decides their own profile change ───────────────────────────────────

    [Fact]
    public async Task An_hr_officer_cannot_approve_a_change_to_their_own_profile()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var hrUser = Guid.NewGuid();
        var hrOfficer = SeedEmployee(db, tenantId, "HR1", hrUser);
        var change = SeedChange(db, tenantId, hrOfficer.Id, hrUser);

        var result = await Controller(db, tenantId, hrUser, hrOfficer.Id)
            .ApproveProfileChange(change.Id, new ProfileChangeDecisionDto(null), CancellationToken.None);

        ErrorCodeOf(result).Should().Be(SubjectDecisionBar.ErrorCode);
        db.ChangeTracker.Clear();
        (await db.EmployeeProfileChangeRequests.SingleAsync()).Status.Should().Be("PendingHR");
        (await db.Employees.SingleAsync()).Phone.Should().Be("0500000000", "a refused approval must not apply the change");
        (await db.EmployeeSelfServiceAuditLogs.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task An_hr_officer_cannot_reject_a_change_to_their_own_profile()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var hrUser = Guid.NewGuid();
        var hrOfficer = SeedEmployee(db, tenantId, "HR1", hrUser);
        var change = SeedChange(db, tenantId, hrOfficer.Id, hrUser);

        var result = await Controller(db, tenantId, hrUser)
            .RejectProfileChange(change.Id, new ProfileChangeDecisionDto(null), CancellationToken.None);

        ErrorCodeOf(result).Should().Be(SubjectDecisionBar.ErrorCode);
        db.ChangeTracker.Clear();
        (await db.EmployeeProfileChangeRequests.SingleAsync()).Status.Should().Be("PendingHR");
    }

    [Fact]
    public async Task A_caller_whose_employee_link_is_the_subject_is_refused_even_without_the_user_account_link()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var hrUser = Guid.NewGuid();
        var subject = SeedEmployee(db, tenantId, "HR1"); // no UserAccountId on the row
        var change = SeedChange(db, tenantId, subject.Id, Guid.NewGuid());

        var result = await Controller(db, tenantId, hrUser, subject.Id)
            .ApproveProfileChange(change.Id, new ProfileChangeDecisionDto(null), CancellationToken.None);

        ErrorCodeOf(result).Should().Be(SubjectDecisionBar.ErrorCode);
    }

    [Fact]
    public async Task Whoever_submitted_a_change_for_someone_else_cannot_decide_it()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var subject = SeedEmployee(db, tenantId, "E1", Guid.NewGuid());
        var submitter = Guid.NewGuid();
        var change = SeedChange(db, tenantId, subject.Id, submitter);

        var approve = await Controller(db, tenantId, submitter)
            .ApproveProfileChange(change.Id, new ProfileChangeDecisionDto(null), CancellationToken.None);
        var reject = await Controller(db, tenantId, submitter)
            .RejectProfileChange(change.Id, new ProfileChangeDecisionDto(null), CancellationToken.None);

        ErrorCodeOf(approve).Should().Be(SubjectDecisionBar.ErrorCode);
        ErrorCodeOf(reject).Should().Be(SubjectDecisionBar.ErrorCode);
        db.ChangeTracker.Clear();
        (await db.EmployeeProfileChangeRequests.SingleAsync()).Status.Should().Be("PendingHR");
    }

    [Fact]
    public async Task Another_hr_user_still_approves_and_the_approval_is_audited()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employeeUser = Guid.NewGuid();
        var subject = SeedEmployee(db, tenantId, "E1", employeeUser);
        var hrUser = Guid.NewGuid();
        var hrOfficer = SeedEmployee(db, tenantId, "HR1", hrUser);
        var change = SeedChange(db, tenantId, subject.Id, employeeUser);

        var result = await Controller(db, tenantId, hrUser, hrOfficer.Id)
            .ApproveProfileChange(change.Id, new ProfileChangeDecisionDto("ok"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var saved = await db.EmployeeProfileChangeRequests.SingleAsync();
        saved.Status.Should().Be("Approved");
        saved.DecidedBy.Should().Be(hrUser);
        (await db.Employees.SingleAsync(x => x.Id == subject.Id)).Phone.Should().Be("0599999999");
        var audit = await db.EmployeeSelfServiceAuditLogs.SingleAsync();
        audit.Action.Should().Be("ess.profile_change.approved");
        audit.UserId.Should().Be(hrUser);
        audit.EmployeeId.Should().Be(subject.Id);
    }

    // ── 2. A rejection is audited like an approval ──────────────────────────────────────────────

    [Fact]
    public async Task Another_hr_user_still_rejects_and_the_rejection_writes_an_audit_row()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employeeUser = Guid.NewGuid();
        var subject = SeedEmployee(db, tenantId, "E1", employeeUser);
        var hrUser = Guid.NewGuid();
        var change = SeedChange(db, tenantId, subject.Id, employeeUser);

        var result = await Controller(db, tenantId, hrUser)
            .RejectProfileChange(change.Id, new ProfileChangeDecisionDto("not verified"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var saved = await db.EmployeeProfileChangeRequests.SingleAsync();
        saved.Status.Should().Be("Rejected");
        saved.DecidedBy.Should().Be(hrUser);
        (await db.Employees.SingleAsync()).Phone.Should().Be("0500000000");
        var audit = await db.EmployeeSelfServiceAuditLogs.SingleAsync();
        audit.Action.Should().Be("ess.profile_change.rejected");
        audit.EntityName.Should().Be(nameof(EmployeeProfileChangeRequest));
        audit.EntityId.Should().Be(change.Id.ToString());
        audit.UserId.Should().Be(hrUser);
        audit.EmployeeId.Should().Be(subject.Id);
    }

    // ── 3. Policies: the caller's own records only ──────────────────────────────────────────────

    private static EmployeeDocument SeedDocument(ZayraDbContext db, Guid tenantId, int employeeId, string type, string fileName)
    {
        var d = new EmployeeDocument
        {
            TenantId = tenantId, EmployeeId = employeeId, DocumentType = type, FileName = fileName,
            ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(3)),
        };
        db.EmployeeDocuments.Add(d);
        db.SaveChanges();
        return d;
    }

    [Fact]
    public async Task Policies_lists_the_callers_own_policy_document_and_never_a_colleagues()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var meUser = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1", meUser);
        var colleague = SeedEmployee(db, tenantId, "E2", Guid.NewGuid());
        var mine = SeedDocument(db, tenantId, me.Id, "Insurance Policy", "my-insurance.pdf");
        SeedDocument(db, tenantId, colleague.Id, "Insurance Policy", "colleague-insurance.pdf");
        SeedDocument(db, tenantId, colleague.Id, "Company Policy", "fake-company-policy.pdf");

        var result = await Controller(db, tenantId, meUser, me.Id).Policies(CancellationToken.None);

        var list = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<IEnumerable<ESSDocumentDto>>().Subject.ToList();
        list.Select(x => x.Id).Should().Equal(mine.Id);
        list.Should().NotContain(x => x.FileName.StartsWith("colleague") || x.FileName.StartsWith("fake"));
    }

    [Fact]
    public async Task Acknowledge_refuses_a_colleagues_policy_document_and_accepts_the_callers_own()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var meUser = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1", meUser);
        var colleague = SeedEmployee(db, tenantId, "E2", Guid.NewGuid());
        var mine = SeedDocument(db, tenantId, me.Id, "HR Policy", "handbook.pdf");
        var theirs = SeedDocument(db, tenantId, colleague.Id, "Company Policy", "fake-company-policy.pdf");

        var refused = await Controller(db, tenantId, meUser, me.Id).AcknowledgePolicy(theirs.Id, CancellationToken.None);
        refused.Result.Should().BeOfType<NotFoundResult>();

        var accepted = await Controller(db, tenantId, meUser, me.Id).AcknowledgePolicy(mine.Id, CancellationToken.None);
        accepted.Result.Should().BeOfType<CreatedResult>();
        (await db.EmployeePolicyAcknowledgements.SingleAsync()).PolicyId.Should().Be(mine.Id);
    }
}
