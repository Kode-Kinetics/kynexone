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

namespace Zayra.Api.Tests;

/// <summary>
/// PEO-04 — HR decisions on an employee's personal-detail change request (approve / reject).
/// The subject never decides their own change (this repo's separation-of-duties refusal is a 400 with
/// code subject_cannot_decide, not a 403), a rejection is audited, an approval leaves history and is
/// validated before it touches the record. InMemory EF; no Docker.
/// </summary>
public class EssProfileChangeApprovalTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Employee SeedEmployee(ZayraDbContext db, Guid tenantId, string code)
    {
        var e = new Employee
        {
            TenantId = tenantId, EmployeeCode = code, FullName = $"Employee {code}", Department = "Ops",
            Designation = "Officer", Status = "Active", JoiningDate = DateTime.UtcNow.Date.AddYears(-1),
            Phone = "0500000000", PersonalEmail = "old@example.com",
        };
        db.Employees.Add(e);
        db.SaveChanges();
        return e;
    }

    private static EmployeeProfileChangeRequest SeedChange(ZayraDbContext db, Guid tenantId, int employeeId, Guid requestedBy, Dictionary<string, object?> changes)
    {
        var change = new EmployeeProfileChangeRequest
        {
            TenantId = tenantId, EmployeeId = employeeId, RequestedChangesJson = JsonSerializer.Serialize(changes),
            Reason = "update", CreatedBy = requestedBy,
        };
        db.EmployeeProfileChangeRequests.Add(change);
        db.SaveChanges();
        return change;
    }

    private sealed class NoStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public string ResolvePath(string storageUrl) => storageUrl;
    }

    private static EmployeeSelfServiceController Hr(ZayraDbContext db, Guid tenantId, Guid userId, int? callerEmployeeId = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, "HR Manager"),
            new("permission", "employees.write"),
        };
        if (callerEmployeeId is int id) claims.Add(new Claim("employee_id", id.ToString()));
        var controller = new EmployeeSelfServiceController(db, null!, new PdfRenderGate(1), null!, null!, null!, new NoStorage());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
        return controller;
    }

    [Fact]
    public async Task Approve_ByTheSubject_IsRefused_AndChangesNothing()
    {
        var tenantId = Guid.NewGuid();
        await using var db = CreateDb();
        var subject = SeedEmployee(db, tenantId, "E1");
        var change = SeedChange(db, tenantId, subject.Id, Guid.NewGuid(), new() { ["phone"] = "0555555555" });

        var result = await Hr(db, tenantId, Guid.NewGuid(), callerEmployeeId: subject.Id)
            .ApproveProfileChange(change.Id, new ProfileChangeDecisionDto(null), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>("the separation-of-duties refusal for the subject");
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == subject.Id)).Phone.Should().Be("0500000000");
        (await db.EmployeeProfileChangeRequests.AsNoTracking().SingleAsync(c => c.Id == change.Id)).Status.Should().Be("PendingHR");
    }

    [Fact]
    public async Task Reject_ByTheSubject_IsRefused()
    {
        var tenantId = Guid.NewGuid();
        await using var db = CreateDb();
        var subject = SeedEmployee(db, tenantId, "E1");
        var change = SeedChange(db, tenantId, subject.Id, Guid.NewGuid(), new() { ["phone"] = "0555555555" });

        var result = await Hr(db, tenantId, Guid.NewGuid(), callerEmployeeId: subject.Id)
            .RejectProfileChange(change.Id, new ProfileChangeDecisionDto("no"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.EmployeeProfileChangeRequests.AsNoTracking().SingleAsync(c => c.Id == change.Id)).Status.Should().Be("PendingHR");
    }

    [Fact]
    public async Task Reject_ByAnotherHrUser_WritesAnAuditRow()
    {
        var tenantId = Guid.NewGuid();
        await using var db = CreateDb();
        var subject = SeedEmployee(db, tenantId, "E1");
        var change = SeedChange(db, tenantId, subject.Id, Guid.NewGuid(), new() { ["phone"] = "0555555555" });

        var result = await Hr(db, tenantId, Guid.NewGuid()).RejectProfileChange(change.Id, new ProfileChangeDecisionDto("no"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        (await db.EmployeeSelfServiceAuditLogs.AsNoTracking().ToListAsync())
            .Should().Contain(a => a.Action == "ess.profile_change.rejected" && a.EntityId == change.Id.ToString());
    }

    [Fact]
    public async Task Approve_ByAnotherHrUser_AppliesTheValue_AndWritesAnEmployeeHistoryRow()
    {
        var tenantId = Guid.NewGuid();
        await using var db = CreateDb();
        var subject = SeedEmployee(db, tenantId, "E1");
        var decider = Guid.NewGuid();
        var change = SeedChange(db, tenantId, subject.Id, Guid.NewGuid(), new() { ["phone"] = "0555555555" });

        var result = await Hr(db, tenantId, decider).ApproveProfileChange(change.Id, new ProfileChangeDecisionDto(null), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == subject.Id)).Phone.Should().Be("0555555555");
        var history = await db.EmployeeHistories.AsNoTracking().Where(h => h.EmployeeId == subject.Id).ToListAsync();
        history.Should().ContainSingle(h => h.FieldName == "Phone" && h.ApprovedByUserId == decider && h.CreatedByUserId == decider);
    }

    [Theory]
    [InlineData("personalEmail", "not-an-email")]
    [InlineData("phone", "0123456789012345678901234567890123456789012345678901234567890123456789")]
    [InlineData("preferredName", "NameNameNameNameNameNameNameNameNameNameNameNameNameNameNameNameNameNameNameNameNameNameNameNameNameNameNameNameNameNameName")]
    public async Task Approve_WithInvalidValue_Is400_AndChangesNothing(string field, string value)
    {
        var tenantId = Guid.NewGuid();
        await using var db = CreateDb();
        var subject = SeedEmployee(db, tenantId, "E1");
        var change = SeedChange(db, tenantId, subject.Id, Guid.NewGuid(), new() { [field] = value });

        var result = await Hr(db, tenantId, Guid.NewGuid()).ApproveProfileChange(change.Id, new ProfileChangeDecisionDto(null), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        var stored = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == subject.Id);
        stored.Phone.Should().Be("0500000000");
        stored.PersonalEmail.Should().Be("old@example.com");
        (await db.EmployeeProfileChangeRequests.AsNoTracking().SingleAsync(c => c.Id == change.Id)).Status.Should().Be("PendingHR");
        (await db.EmployeeHistories.AsNoTracking().CountAsync(h => h.EmployeeId == subject.Id)).Should().Be(0);
    }
}
