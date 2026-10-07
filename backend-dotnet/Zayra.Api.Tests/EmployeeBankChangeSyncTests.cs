using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// P0 money bug: `bankIban`/`bankName` are SENSITIVE, so every bank change becomes an
/// <see cref="EmployeeChangeRequest"/>. There used to be TWO appliers —
/// <c>EmployeesController.ApproveChange</c> (the direct endpoint, now retired with 410) and
/// <c>ApprovalWorkflowService.SyncEmployeeChangeDecisionAsync</c> (the normal Approvals screen).
/// The WPS/SIF export reads <c>EmployeePayrollProfile.Iban</c>, NOT <c>Employee.BankIban</c>.
/// Only the first applier called <c>EmployeeBankProfileSync</c>, so an IBAN approved from the
/// Approvals screen left the payroll profile on the OLD account — the employee was paid into the
/// previous bank account, or dropped from the WPS file entirely.
///
/// These tests assert on PERSISTED values (never a status code). The two-applier parity guard went
/// with the direct endpoint: the Approval Center is now the only applier.
/// </summary>
public class EmployeeBankChangeSyncTests
{
    private const string OldIban = "SA0380000000608010167519";
    private const string NewIban = "SA4420000001234567891234";
    private const string OldBank = "Old Bank";
    private const string NewBank = "New Bank";

    private static ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record Fixture(Guid TenantId, Employee Employee, EmployeePayrollProfile Profile, EmployeeChangeRequest Change, Guid RequesterUserId);

    /// <summary>An employee banked at the OLD account in BOTH homes, plus a pending, approvable
    /// bank change to the new account — the exact shape the Approvals screen decides on.</summary>
    private static async Task<Fixture> SeedPendingBankChangeAsync(ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Slug = $"t-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        var employee = new Employee
        {
            TenantId = tenant.Id,
            EmployeeCode = $"E-{Guid.NewGuid():N}"[..12],
            FullName = "Banked Employee",
            CountryCode = "SA",
            Status = "Active",
            JoiningDate = DateTime.UtcNow,
            BankName = OldBank,
            BankIban = OldIban
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var profile = new EmployeePayrollProfile
        {
            TenantId = tenant.Id,
            EmployeeId = employee.Id,
            BankName = OldBank,
            Iban = OldIban,
            SalaryCurrency = "SAR"
        };
        db.EmployeePayrollProfiles.Add(profile);

        var requesterUserId = Guid.NewGuid();
        var change = new EmployeeChangeRequest
        {
            TenantId = tenant.Id,
            EmployeeId = employee.Id,
            RequestedByUserId = requesterUserId, // never the approver — maker-checker
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            SensitiveFields = "bankIban,bankName",
            ProposedChangesJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["bankIban"] = NewIban,
                ["bankName"] = NewBank
            })
        };
        db.EmployeeChangeRequests.Add(change);
        await db.SaveChangesAsync();
        return new Fixture(tenant.Id, employee, profile, change, requesterUserId);
    }

    /// <summary>Route 2: the pending ApprovalRequest the Approvals screen shows, on a one-step
    /// HR-Manager workflow so a single Approve decision is final.</summary>
    private static async Task<ApprovalRequest> SeedApprovalRequestAsync(ZayraDbContext db, Fixture fx)
    {
        var workflow = new ApprovalWorkflow
        {
            TenantId = fx.TenantId,
            Code = "EMPLOYEE-CHANGE",
            Name = "Employee Master Change Approval",
            EntityName = nameof(EmployeeChangeRequest),
            IsActive = true
        };
        db.ApprovalWorkflows.Add(workflow);
        db.ApprovalWorkflowSteps.Add(new ApprovalWorkflowStep
        {
            TenantId = fx.TenantId,
            WorkflowId = workflow.Id,
            StepOrder = 1,
            StepName = "HR Final Approval",
            ApproverRole = "HR Manager",
            ApproverType = "Role",
            IsFinalStep = true
        });
        var approval = new ApprovalRequest
        {
            TenantId = fx.TenantId,
            WorkflowId = workflow.Id,
            EntityName = nameof(EmployeeChangeRequest),
            EntityId = fx.Change.Id.ToString(),
            Title = "Employee change approval",
            Status = "Pending",
            CurrentStepOrder = 1,
            CurrentApproverRole = "HR Manager",
            CurrentApproverType = "Role",
            CurrentQueue = "Role:HR Manager",
            RequestedByUserId = fx.RequesterUserId,
            RequestedForEmployeeId = fx.Employee.Id
        };
        db.ApprovalRequests.Add(approval);
        await db.SaveChangesAsync();
        return approval;
    }

    private static ApprovalWorkflowService CreateWorkflowService(ZayraDbContext db)
        => new(db, new AuditService(db));

    private static RequestContext ApproverCtx(Guid tenantId, Guid approverUserId)
        => new("127.0.0.1", "xunit", approverUserId, tenantId, new[] { "HR Manager" }, new[] { "approvals.override" });

    private static EmployeesController CreateController(ZayraDbContext db, Guid tenantId, Guid approverUserId)
    {
        var audit = new AuditService(db);
        var controller = new EmployeesController(
            db,
            new Pbkdf2PasswordHasher(),
            audit,
            new FakeDocs(),
            new FakeNotifications(),
            new FakeHijri(),
            new Zayra.Api.Infrastructure.Common.DataScopeService(db),
            new FakeLetters(),
            new ApprovalWorkflowService(db, audit),
            NullLogger<EmployeesController>.Instance,
            new EstablishmentGuardService(db));
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, approverUserId.ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("permission", "employees.read"),
            new Claim("permission", "employees.write"),
            new Claim("permission", "employees.approve"),
            new Claim("permission", "employees.sensitive"),
            new Claim("is_group_scope", "true")
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    /// <summary>The persisted end state a bank change must reach, whichever applier ran.</summary>
    private sealed record BankEndState(string EmployeeIban, string EmployeeBankName, string ProfileIban, string ProfileBankName, bool ProfileStamped, string ChangeStatus, bool Applied, int HistoryRows);

    private static async Task<BankEndState> ReadEndStateAsync(ZayraDbContext db, Fixture fx)
    {
        var employee = await db.Employees.AsNoTracking().SingleAsync(x => x.Id == fx.Employee.Id);
        var profile = await db.EmployeePayrollProfiles.AsNoTracking().SingleAsync(x => x.EmployeeId == fx.Employee.Id);
        var change = await db.EmployeeChangeRequests.AsNoTracking().SingleAsync(x => x.Id == fx.Change.Id);
        var history = await db.EmployeeHistories.AsNoTracking()
            .CountAsync(x => x.EmployeeId == fx.Employee.Id && x.EventType == "SensitiveChangeApproved");
        return new BankEndState(employee.BankIban, employee.BankName, profile.Iban, profile.BankName,
            profile.UpdatedAtUtc is not null, change.Status, change.AppliedAtUtc is not null, history);
    }

    // ── (a) Route 2 — the normal Approvals screen — must reach payroll ──

    [Fact]
    public async Task BankChangeApprovedViaApprovalWorkflow_UpdatesEmployeeScalarAndPayrollProfileIban()
    {
        await using var db = CreateDb();
        var fx = await SeedPendingBankChangeAsync(db);
        var approval = await SeedApprovalRequestAsync(db, fx);
        var approverUserId = Guid.NewGuid();

        var decided = await CreateWorkflowService(db).DecideAsync(
            fx.TenantId, approval.Id, new ApprovalDecisionRequest("Approve", "Verified against the bank letter."),
            ApproverCtx(fx.TenantId, approverUserId), CancellationToken.None);

        decided!.Status.Should().Be("Approved");
        var state = await ReadEndStateAsync(db, fx);
        state.EmployeeIban.Should().Be(NewIban);
        state.EmployeeBankName.Should().Be(NewBank);
        // THE MONEY ASSERTION: the WPS/SIF export reads the payroll profile, not the Employee scalar.
        state.ProfileIban.Should().Be(NewIban, "the payroll profile is what the WPS/SIF export actually pays into");
        state.ProfileBankName.Should().Be(NewBank);
        state.ProfileStamped.Should().BeTrue("the synced profile row must carry an update stamp");
        state.ChangeStatus.Should().Be("ApprovedApplied");
        state.HistoryRows.Should().Be(1, "the approval is recorded in employee history");
    }

    // ── Stubs ────────────────────────────────────────────────────────────────

    private sealed class FakeDocs : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new StoredDocument(file.FileName, file.ContentType ?? "application/octet-stream", "storage/test", "/tmp/test"));
        public string ResolvePath(string storageUrl) => "/tmp/test";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());
    }

    private sealed class FakeNotifications : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeHijri : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class FakeLetters : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
