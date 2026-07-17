using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

public class GovernanceHardeningTests
{
    [Fact]
    public async Task AuditService_WritesTamperEvidentHashChain()
    {
        await using var db = CreateDb();
        var svc = new AuditService(db);
        var tenantId = Guid.NewGuid();
        var ctx = new RequestContext("127.0.0.1", "governance-test", Guid.NewGuid(), tenantId);

        await svc.WriteAsync("employee.change_requested", "Employee", "1", ctx, "{\"field\":\"salary\"}", CancellationToken.None);
        await svc.WriteAsync("employee.change_approved", "Employee", "1", ctx, "{\"field\":\"salary\"}", CancellationToken.None);

        var logs = await db.AuditLogs.Where(x => x.TenantId == tenantId).OrderBy(x => x.CreatedAtUtc).ToListAsync();
        logs.Should().HaveCount(2);
        logs[0].PreviousHash.Should().BeEmpty();
        logs[0].EntryHash.Should().NotBeNullOrWhiteSpace();
        logs[1].PreviousHash.Should().Be(logs[0].EntryHash);
        logs.Should().OnlyContain(x => AuditService.VerifyIntegrity(x));

        logs[1].Metadata = "{\"field\":\"bankIban\"}";
        AuditService.VerifyIntegrity(logs[1]).Should().BeFalse("changing metadata after write must invalidate the audit hash");
    }

    [Fact]
    public async Task AuditService_VerifiesFullHashChainContinuity()
    {
        await using var db = CreateDb();
        var svc = new AuditService(db);
        var tenantId = Guid.NewGuid();
        var ctx = new RequestContext("127.0.0.1", "governance-test", Guid.NewGuid(), tenantId);

        await svc.WriteAsync("employee.created", "Employee", "1", ctx, null, CancellationToken.None);
        await svc.WriteAsync("employee.updated", "Employee", "1", ctx, "{\"field\":\"department\"}", CancellationToken.None);

        var logs = await db.AuditLogs.Where(x => x.TenantId == tenantId).OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToListAsync();
        AuditService.VerifyChain(logs).IsValid.Should().BeTrue();

        logs[1].PreviousHash = "forged";
        var report = AuditService.VerifyChain(logs);

        report.IsValid.Should().BeFalse();
        report.Failures.Should().ContainSingle(x => x.Reason == "entry_hash_mismatch");
    }

    [Fact]
    public async Task AuditLogs_AreAppendOnly()
    {
        await using var db = CreateDb();
        var svc = new AuditService(db);
        var tenantId = Guid.NewGuid();
        var ctx = new RequestContext("127.0.0.1", "governance-test", Guid.NewGuid(), tenantId);

        await svc.WriteAsync("employee.created", "Employee", "1", ctx, null, CancellationToken.None);
        var log = await db.AuditLogs.SingleAsync(x => x.TenantId == tenantId);

        log.Metadata = "{\"forged\":true}";
        var modify = () => db.SaveChangesAsync();

        await modify.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*audit_log_append_only_violation*");

        db.Entry(log).State = EntityState.Unchanged;
        db.AuditLogs.Remove(log);
        var delete = () => db.SaveChangesAsync();

        await delete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*audit_log_append_only_violation*");
    }

    [Fact]
    public async Task ApproveChange_BlocksRequesterSelfApproval()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var requesterId = Guid.NewGuid();
        var employee = SeedEmployee(db, tenantId);
        var change = SeedSalaryChange(db, tenantId, employee.Id, requesterId, DateOnly.FromDateTime(DateTime.UtcNow.Date));
        var controller = CreateController(db, tenantId, "Admin", requesterId);

        var result = await controller.ApproveChange(change.Id, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        var unchanged = await db.Employees.FindAsync(employee.Id);
        unchanged!.Salary.Should().Be(10_000m);
        (await db.EmployeeChangeRequests.FindAsync(change.Id))!.Status.Should().Be("PendingApproval");
    }

    [Fact]
    public async Task ApproveChange_BlocksFutureEffectiveApply()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var employee = SeedEmployee(db, tenantId);
        var change = SeedSalaryChange(db, tenantId, employee.Id, Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(7)));
        var controller = CreateController(db, tenantId, "Admin", Guid.NewGuid());

        var result = await controller.ApproveChange(change.Id, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        var unchanged = await db.Employees.FindAsync(employee.Id);
        unchanged!.Salary.Should().Be(10_000m);
        (await db.EmployeeChangeRequests.FindAsync(change.Id))!.Status.Should().Be("PendingApproval");
    }

    [Fact]
    public async Task DeleteEmployee_StampsPrivacyRetentionMetadata()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var employee = SeedEmployee(db, tenantId);
        var controller = CreateController(db, tenantId, "Admin", Guid.NewGuid());

        var result = await controller.Delete(employee.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var deleted = await db.Employees.IgnoreQueryFilters().FirstAsync(x => x.Id == employee.Id);
        deleted.IsDeleted.Should().BeTrue();
        deleted.PrivacyStatus.Should().Be("RetainedForStatutoryAudit");
        deleted.RetentionUntilUtc.Should().NotBeNull();
        deleted.RetentionUntilUtc!.Value.Should().BeAfter(DateTime.UtcNow.AddYears(6).AddMonths(11));
        var audit = await db.AuditLogs.SingleAsync(x => x.EntityName == "Employee" && x.EntityId == employee.Id.ToString());
        audit.Metadata.Should().Contain("RetainedForStatutoryAudit");
        AuditService.VerifyIntegrity(audit).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteEmployee_CancelsPendingEmployeeChangeApprovals()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var employee = SeedEmployee(db, tenantId);
        var change = SeedSalaryChange(db, tenantId, employee.Id, Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.Date));
        var approval = new ApprovalRequest
        {
            TenantId = tenantId,
            WorkflowId = Guid.NewGuid(),
            EntityName = nameof(EmployeeChangeRequest),
            EntityId = change.Id.ToString(),
            Title = "Salary change",
            Status = "Pending",
            RequestedForEmployeeId = employee.Id
        };
        db.ApprovalRequests.Add(approval);
        change.ApprovalRequestId = approval.Id;
        await db.SaveChangesAsync();
        var controller = CreateController(db, tenantId, "Admin", Guid.NewGuid());

        var result = await controller.Delete(employee.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        (await db.EmployeeChangeRequests.FindAsync(change.Id))!.Status.Should().Be("Cancelled");
        var cancelledApproval = await db.ApprovalRequests.FindAsync(approval.Id);
        cancelledApproval!.Status.Should().Be("Cancelled");
        cancelledApproval.CompletedAtUtc.Should().NotBeNull();
        var audit = await db.AuditLogs.SingleAsync(x => x.EntityName == "Employee" && x.EntityId == employee.Id.ToString());
        audit.Metadata.Should().Contain("CancelledApprovalRequests");
    }

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<Guid> SeedTenantAsync(ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Governance Corp", Slug = "governance-corp" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private static Employee SeedEmployee(ZayraDbContext db, Guid tenantId)
    {
        var employee = new Employee
        {
            TenantId = tenantId,
            EmployeeCode = "EMP-GOV-001",
            FullName = "Governance Employee",
            EnglishName = "Governance Employee",
            Gender = "Female",
            Department = "People",
            Designation = "Analyst",
            Status = "Active",
            JoiningDate = DateTime.UtcNow.Date,
            Salary = 10_000m
        };
        db.Employees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static EmployeeChangeRequest SeedSalaryChange(ZayraDbContext db, Guid tenantId, int employeeId, Guid requesterId, DateOnly effectiveDate)
    {
        var change = new EmployeeChangeRequest
        {
            TenantId = tenantId,
            EmployeeId = employeeId,
            RequestedByUserId = requesterId,
            EffectiveDate = effectiveDate,
            SensitiveFields = "salary",
            ProposedChangesJson = JsonSerializer.Serialize(new Dictionary<string, JsonElement>
            {
                ["salary"] = JsonSerializer.SerializeToElement(15_000m)
            })
        };
        db.EmployeeChangeRequests.Add(change);
        db.SaveChanges();
        return change;
    }

    private static EmployeesController CreateController(ZayraDbContext db, Guid tenantId, string role, Guid userId)
    {
        var controller = new EmployeesController(
            db,
            new Pbkdf2PasswordHasher(),
            new AuditService(db),
            new FakeDocumentStorage(),
            new FakeNotificationService(),
            new FakeHijriDateService(),
            new DataScopeService(db),
            new FakeLetterService());
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, role),
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    private sealed class FakeDocumentStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) =>
            Task.FromResult(new StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
        public string ResolvePath(string storageUrl) => "/tmp/test";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());
    }

    private sealed class FakeNotificationService : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeHijriDateService : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), date.ToString("yyyy-MM-dd"), date.Year, date.Month, date.Day);
    }

    private sealed class FakeLetterService : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    }
}
