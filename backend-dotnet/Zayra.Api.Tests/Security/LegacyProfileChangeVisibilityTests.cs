using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// G-003. The older mobile profile-change request (Section = null) was filtered out of HR's inbox and the employee
/// had no status for it, so it could be pending and never seen. These tests pin that it is now in the inbox with the
/// changed fields, is decided through the existing decision path (separation of duties unchanged), and that the
/// employee can read its status.
/// </summary>
public sealed class LegacyProfileChangeVisibilityTests
{
    private sealed class NoStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public string ResolvePath(string storageUrl) => storageUrl;
    }

    private sealed class World
    {
        public Guid Tenant { get; } = Guid.NewGuid();
        public ZayraDbContext Db { get; } = PeopleAuditFixtures.CreateDb();
        public Employee Subject { get; }
        public Employee Other { get; }
        public Guid SubjectLogin { get; } = Guid.NewGuid();
        public Guid HrLogin { get; } = Guid.NewGuid();

        public World()
        {
            Subject = PeopleAuditFixtures.SeedEmployee(Db, Tenant, "E1", "Sam Subject", userAccountId: SubjectLogin);
            Subject.Phone = "0500000000";
            Other = PeopleAuditFixtures.SeedEmployee(Db, Tenant, "E2", "Olga Other");
            Other.Phone = "0511111111";
            Db.SaveChanges();
        }

        public EmployeeProfileChangeRequest Legacy(Employee e, string json = "{\"phone\":\"0555555555\"}", string status = "PendingHR", string? section = null)
        {
            var r = new EmployeeProfileChangeRequest
            {
                TenantId = Tenant, EmployeeId = e.Id, RequestedChangesJson = json, Reason = "Requested from mobile app",
                Status = status, Section = section, CreatedBy = e.UserAccountId,
            };
            Db.EmployeeProfileChangeRequests.Add(r);
            Db.SaveChanges();
            return r;
        }

        private ClaimsPrincipal User(Guid userId, int? employeeId, params string[] permissions)
        {
            var claims = new List<Claim> { new("tenant_id", Tenant.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()) };
            if (employeeId is int e) claims.Add(new Claim("employee_id", e.ToString()));
            claims.AddRange(permissions.Select(p => new Claim("permission", p)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

        public HrProfileCompletionController Inbox()
        {
            var c = new HrProfileCompletionController(Db, new NoStorage(), null!);
            c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User(HrLogin, null, "employees.write") } };
            return c;
        }

        public EmployeeSelfServiceController Ess(Guid userId, int? employeeId, params string[] permissions)
        {
            var c = new EmployeeSelfServiceController(Db, null!, new PdfRenderGate(1), null!, null!, null!, new NoStorage());
            c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User(userId, employeeId, permissions) } };
            return c;
        }
    }

    // What the API sends (camelCase), not the CLR property names.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ProfileChange_LegacyRequest_AppearsInHrInbox_WithTheChangedFields()
    {
        var w = new World();
        var request = w.Legacy(w.Subject, "{\"phone\":\"0555555555\",\"personalEmail\":\"new@example.com\"}");

        var ok = (OkObjectResult)await w.Inbox().Inbox(null, null, null, 1, default);

        var items = JsonSerializer.SerializeToElement(ok.Value).EnumerateArray().ToList();
        items.Should().ContainSingle();
        var item = items[0];
        item.GetProperty("requestId").GetGuid().Should().Be(request.Id);
        item.GetProperty("section").ValueKind.Should().Be(JsonValueKind.Null, "the legacy request has no section");
        item.GetProperty("legacy").GetBoolean().Should().BeTrue();
        item.GetProperty("goesToFinance").GetBoolean().Should().BeFalse();
        var phone = item.GetProperty("diff").EnumerateArray().Single(d => d.GetProperty("field").GetString() == "phone");
        phone.GetProperty("before").GetString().Should().Be("0500000000");
        phone.GetProperty("after").GetString().Should().Be("0555555555");
        phone.GetProperty("changed").GetBoolean().Should().BeTrue();
        phone.GetProperty("labelKey").GetString().Should().Be("ess.profileChange.field.phone", "the screen translates it from the dictionary");
        item.GetProperty("diff").EnumerateArray().Select(d => d.GetProperty("field").GetString())
            .Should().BeEquivalentTo("phone", "personalEmail");
    }

    [Fact]
    public async Task ProfileChange_SectionFilter_StillExcludesLegacyRequests()
    {
        var w = new World();
        w.Legacy(w.Subject);

        var ok = (OkObjectResult)await w.Inbox().Inbox(null, "person", null, 1, default);

        JsonSerializer.SerializeToElement(ok.Value).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ProfileChange_LegacyRequest_IsDecidedThroughTheExistingPath_AndTheSubjectStillCannot()
    {
        var w = new World();
        var request = w.Legacy(w.Subject);

        // The subject (their own login) is refused exactly as before.
        var self = await w.Ess(w.SubjectLogin, w.Subject.Id, "employees.write").ApproveProfileChange(request.Id, new ProfileChangeDecisionDto("ok"), default);
        self.Should().BeOfType<BadRequestObjectResult>();
        w.Db.EmployeeProfileChangeRequests.Single().Status.Should().Be("PendingHR");

        // Another HR user approves: the value lands on the record.
        var hr = await w.Ess(w.HrLogin, null, "employees.write").ApproveProfileChange(request.Id, new ProfileChangeDecisionDto("ok"), default);
        hr.Should().BeOfType<OkObjectResult>();
        w.Db.EmployeeProfileChangeRequests.Single().Status.Should().Be("Approved");
        w.Db.Employees.Single(e => e.Id == w.Subject.Id).Phone.Should().Be("0555555555");
    }

    [Fact]
    public async Task ProfileChange_Employee_SeesOnlyTheirOwnLegacyRequests_WithStatus()
    {
        var w = new World();
        var mine = w.Legacy(w.Subject, status: "PendingHR");
        w.Legacy(w.Other);                                          // someone else's
        w.Legacy(w.Subject, "{\"iban\":\"x\"}", section: "bank");   // a section request has its own screen

        var result = await w.Ess(w.SubjectLogin, w.Subject.Id, "ess.read").MyProfileChangeRequests(default);

        var rows = JsonSerializer.SerializeToElement(((OkObjectResult)result).Value, Web).EnumerateArray().ToList();
        rows.Should().ContainSingle();
        rows[0].GetProperty("id").GetGuid().Should().Be(mine.Id);
        rows[0].GetProperty("status").GetString().Should().Be("PendingHR");
        rows[0].GetProperty("fields").EnumerateArray().Select(f => f.GetString()).Should().BeEquivalentTo("phone");
        rows[0].GetProperty("changes").GetProperty("phone").GetString().Should().Be("0555555555");
    }

    [Fact]
    public async Task ProfileChange_Rejected_ShowsTheEmployeeTheNote()
    {
        var w = new World();
        var request = w.Legacy(w.Subject);

        var reject = await w.Ess(w.HrLogin, null, "employees.write").RejectProfileChange(request.Id, new ProfileChangeDecisionDto("Number is not reachable"), default);
        reject.Should().BeOfType<OkObjectResult>();

        var rows = JsonSerializer.SerializeToElement(((OkObjectResult)await w.Ess(w.SubjectLogin, w.Subject.Id, "ess.read").MyProfileChangeRequests(default)).Value, Web).EnumerateArray().ToList();
        rows.Single().GetProperty("status").GetString().Should().Be("Rejected");
        rows.Single().GetProperty("reviewNote").GetString().Should().Be("Number is not reachable");
    }
}
