using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
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

public sealed class DraftApprovalAtomicityTests
{
    [Fact]
    public async Task Approval_PersistsOneCompleteFailClosedAggregate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Controller.ApproveDraft(fixture.DraftId, default);
        result.Result.Should().BeOfType<OkObjectResult>();

        fixture.Db.ChangeTracker.Clear();
        var employee = await fixture.Db.Employees.IgnoreQueryFilters().SingleAsync();
        employee.Status.Should().Be(EmployeeStatuses.Active);
        employee.CompanyId.Should().Be(fixture.CompanyId);

        var draft = await fixture.Db.EmployeeDrafts.IgnoreQueryFilters().SingleAsync();
        draft.Status.Should().Be("Activated");
        draft.ApprovedAtUtc.Should().NotBeNull();
        draft.ActivatedAtUtc.Should().Be(draft.ApprovedAtUtc);

        var document = await fixture.Db.EmployeeDocuments.IgnoreQueryFilters().SingleAsync();
        document.EmployeeId.Should().Be(employee.Id);
        document.CompanyId.Should().Be(fixture.CompanyId);
        (await fixture.Db.EmployeeHistories.IgnoreQueryFilters()
            .CountAsync(x => x.EmployeeId == employee.Id && x.EventType == "Activated")).Should().Be(1);

        var user = await fixture.Db.Users.IgnoreQueryFilters()
            .Include(x => x.UserRoles)
            .Include(x => x.EmployeeUserAccounts)
            .Include(x => x.EntityAccesses)
            .SingleAsync();
        employee.UserAccountId.Should().Be(user.Id);
        user.IsActive.Should().BeFalse();
        user.Status.Should().Be("PendingPasswordSetup");
        user.AccessMode.Should().Be(AccessModes.NoLogin);
        user.IsEmailConfirmed.Should().BeFalse();
        fixture.PasswordHasher.Verify("ChangeMe123!", user.PasswordHash).Should().BeFalse();

        var link = user.EmployeeUserAccounts.Should().ContainSingle().Subject;
        link.EmployeeId.Should().Be(employee.Id);
        link.AccessMode.Should().Be(AccessModes.NoLogin);
        link.Status.Should().Be("PendingPasswordSetup");
        link.RequiresPasswordSetup.Should().BeTrue();
        link.InvitationTokenHash.Should().BeEmpty();

        var grant = user.EntityAccesses.Should().ContainSingle().Subject;
        grant.CompanyId.Should().Be(fixture.CompanyId);
        grant.IsActive.Should().BeFalse("legal-entity access remains staged until an explicit invitation");
        user.UserRoles.Should().ContainSingle();

        var marker = await fixture.Db.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(x => x.Action == "employee.activated");
        marker.EntityId.Should().Be(employee.Id.ToString());
        marker.Metadata.Should().Contain(fixture.DraftId.ToString());
    }

    [Fact]
    public async Task DownstreamLinkFailure_RollsBackEveryApprovalEffect()
    {
        await using var fixture = await Fixture.CreateAsync(withConflictingOnboardingTask: true);

        Func<Task> approve = async () =>
            await fixture.Controller.ApproveDraft(fixture.DraftId, default);
        await approve.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different employee identity*");

        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Employees.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await fixture.Db.Users.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await fixture.Db.EmployeeUserAccounts.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await fixture.Db.UserEntityAccesses.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await fixture.Db.EmployeeHistories.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await fixture.Db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(x => x.Action == "employee.activated")).Should().Be(0);
        (await fixture.Db.EmployeeDrafts.IgnoreQueryFilters().SingleAsync()).Status
            .Should().Be("PendingHrApproval");
        var document = await fixture.Db.EmployeeDocuments.IgnoreQueryFilters().SingleAsync();
        document.EmployeeId.Should().BeNull();
        document.CompanyId.Should().BeNull();
    }

    [Fact]
    public async Task ExistingSameEmailIdentity_FailsClosedWithoutPartialEmployee()
    {
        await using var fixture = await Fixture.CreateAsync(withIdentityCollision: true);

        var result = await fixture.Controller.ApproveDraft(fixture.DraftId, default);
        result.Result.Should().BeOfType<ConflictObjectResult>();

        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Employees.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await fixture.Db.Users.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await fixture.Db.EmployeeUserAccounts.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await fixture.Db.UserEntityAccesses.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await fixture.Db.EmployeeHistories.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await fixture.Db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(x => x.Action == "employee.activated")).Should().Be(0);
        (await fixture.Db.EmployeeDrafts.IgnoreQueryFilters().SingleAsync()).Status
            .Should().Be("PendingHrApproval");
        (await fixture.Db.EmployeeDocuments.IgnoreQueryFilters().SingleAsync()).EmployeeId.Should().BeNull();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ZayraDbContext Db { get; }
        public EmployeesController Controller { get; }
        public Pbkdf2PasswordHasher PasswordHasher { get; }
        public Guid DraftId { get; }
        public Guid CompanyId { get; }

        private Fixture(
            SqliteConnection connection,
            ZayraDbContext db,
            EmployeesController controller,
            Pbkdf2PasswordHasher passwordHasher,
            Guid draftId,
            Guid companyId)
        {
            _connection = connection;
            Db = db;
            Controller = controller;
            PasswordHasher = passwordHasher;
            DraftId = draftId;
            CompanyId = companyId;
        }

        public static async Task<Fixture> CreateAsync(
            bool withConflictingOnboardingTask = false,
            bool withIdentityCollision = false)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ZayraDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new ZayraDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var tenantId = Guid.NewGuid();
            var actorId = Guid.NewGuid();
            var company = new Company
            {
                TenantId = tenantId,
                LegalNameEn = "Atomic Co",
                RegistrationNumber = $"RC-{Guid.NewGuid():N}",
                CountryCode = "AE",
                Jurisdiction = "AE",
                DefaultCurrency = "AED"
            };
            var branch = new Branch
            {
                TenantId = tenantId,
                CompanyId = company.Id,
                Code = "DXB",
                NameEn = "Dubai",
                CountryCode = "AE",
                IsActive = true
            };
            var draft = new EmployeeDraft
            {
                TenantId = tenantId,
                CreatedByUserId = actorId,
                Status = "PendingHrApproval",
                CurrentStep = "HrApproval",
                EnglishName = "Atomic Employee",
                WorkEmail = "atomic.employee@example.test",
                Branch = branch.NameEn,
                JoiningDate = DateTime.UtcNow.Date
            };
            db.AddRange(
                new Tenant { Id = tenantId, Name = "Atomic Tenant", Slug = $"atomic-{tenantId:N}" },
                company,
                branch,
                new Role
                {
                    TenantId = tenantId,
                    Name = "Employee",
                    NormalizedName = "EMPLOYEE",
                    IsActive = true
                },
                draft,
                new EmployeeDocument
                {
                    TenantId = tenantId,
                    DraftId = draft.Id,
                    DocumentType = "Passport",
                    FileName = "passport.pdf",
                    ContentType = "application/pdf",
                    StorageUrl = "tests/passport.pdf"
                });

            if (withConflictingOnboardingTask)
            {
                var application = new JobApplication
                {
                    TenantId = tenantId,
                    CompanyId = company.Id,
                    OnboardingDraftId = draft.Id
                };
                db.AddRange(application, new OnboardingTask
                {
                    TenantId = tenantId,
                    ApplicationId = application.Id,
                    EmployeeId = Guid.NewGuid(),
                    TaskTitle = "Issue laptop"
                });
            }
            if (withIdentityCollision)
            {
                db.Users.Add(new User
                {
                    TenantId = tenantId,
                    Email = draft.WorkEmail,
                    NormalizedEmail = AuthService.Normalize(draft.WorkEmail),
                    FullName = "Existing Identity",
                    PasswordHash = "not-used",
                    IsActive = false,
                    Status = "Deactivated",
                    AccessMode = AccessModes.NoLogin
                });
            }
            await db.SaveChangesAsync();

            var hasher = new Pbkdf2PasswordHasher();
            var audit = new AuditService(db);
            var controller = new EmployeesController(
                db,
                hasher,
                audit,
                new NoopDocumentStorage(),
                new NoopNotificationService(),
                new HijriDateService(),
                new DataScopeService(db),
                new NoopLetterService(),
                new ApprovalWorkflowService(db, audit));
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, actorId.ToString()),
                        new Claim(ClaimTypes.Role, "Admin"),
                        new Claim("permission", "employees.approve"),
                        new Claim("is_group_scope", "true")
                    }, "Test"))
                }
            };
            return new Fixture(connection, db, controller, hasher, draft.Id, company.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class NoopDocumentStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) =>
            Task.FromResult(new StoredDocument(file.FileName, file.ContentType, "tests/file", "/tmp/test"));
        public string ResolvePath(string storageUrl) => "/tmp/test";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());
    }

    private sealed class NoopNotificationService : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message,
            string entityName, string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
            Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoopLetterService : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<byte>());
    }
}
