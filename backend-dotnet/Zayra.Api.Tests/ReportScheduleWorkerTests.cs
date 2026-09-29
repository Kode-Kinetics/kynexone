using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Reports;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public sealed class ReportScheduleWorkerTests
{
    [Fact]
    public async Task DueSchedule_ExecutesAndEmailsArtifact_OnlyOncePerPeriod()
    {
        await using var db = CreateDb();
        var (tenantId, userId) = await SeedAuthorizedScheduleAsync(db);
        var email = new RecordingEmail();
        using var services = BuildServices(db, email);
        var worker = new ReportScheduleWorker(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReportScheduleWorker>.Instance);

        await worker.ProcessOnceAsync(CancellationToken.None);
        await worker.ProcessOnceAsync(CancellationToken.None);

        var execution = await db.ReportExecutionLogs.SingleAsync();
        Assert.Equal("Success", execution.Status);
        Assert.Equal(1, execution.RowCount);
        Assert.Equal(userId, execution.RunBy);
        Assert.Single(email.Messages);
        Assert.Contains("Engineering", System.Text.Encoding.UTF8.GetString(email.Messages[0].Attachment.Data));
        var schedule = await db.ReportSchedules.SingleAsync();
        Assert.NotNull(schedule.LastRunAtUtc);
        Assert.True(schedule.NextRunAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task RevokedCreatorPermission_FailsClosedWithoutDelivery()
    {
        await using var db = CreateDb();
        await SeedAuthorizedScheduleAsync(db);
        db.RolePermissions.RemoveRange(db.RolePermissions);
        await db.SaveChangesAsync();
        var email = new RecordingEmail();
        using var services = BuildServices(db, email);
        var worker = new ReportScheduleWorker(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReportScheduleWorker>.Instance);

        await worker.ProcessOnceAsync(CancellationToken.None);

        Assert.Empty(email.Messages);
        var execution = await db.ReportExecutionLogs.SingleAsync();
        Assert.Equal("Failed", execution.Status);
        Assert.Contains("no longer has", execution.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OwnerWhoLostTheReportsDataPermission_StopsReceivingIt()
    {
        // The owner keeps reports.schedule but is demoted out of employees.read: they can no longer open
        // the headcount report by hand, so the schedule must stop mailing it to them.
        await using var db = CreateDb();
        await SeedAuthorizedScheduleAsync(db);
        var employeeRead = await db.Permissions.SingleAsync(x => x.Key == "employees.read");
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(x => x.PermissionId == employeeRead.Id).ToListAsync());
        await db.SaveChangesAsync();

        var email = new RecordingEmail();
        using var services = BuildServices(db, email);
        var worker = new ReportScheduleWorker(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReportScheduleWorker>.Instance);

        await worker.ProcessOnceAsync(CancellationToken.None);

        Assert.Empty(email.Messages);
        var execution = await db.ReportExecutionLogs.SingleAsync();
        Assert.Equal("Failed", execution.Status);
        Assert.Contains("employees.read", execution.ErrorMessage);
        // An owner problem, so the schedule is marked as needing a new owner rather than retried quietly.
        Assert.NotNull((await db.ReportSchedules.AsNoTracking().SingleAsync()).OwnerInvalidatedAtUtc);
    }

    [Fact]
    public async Task OwnerNarrowedToTheirTeam_StopsReceivingTheOrganisationWideReport()
    {
        // manager.read narrows an employee reader to their own team. The worker used to ignore that and
        // deliver the whole organisation's rows, i.e. more than the owner could see on screen.
        await using var db = CreateDb();
        var (_, userId) = await SeedAuthorizedScheduleAsync(db);
        await GrantAsync(db, userId, "manager.read");

        var email = new RecordingEmail();
        using var services = BuildServices(db, email);
        var worker = new ReportScheduleWorker(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReportScheduleWorker>.Instance);

        await worker.ProcessOnceAsync(CancellationToken.None);

        Assert.Empty(email.Messages);
        var execution = await db.ReportExecutionLogs.SingleAsync();
        Assert.Equal("Failed", execution.Status);
        Assert.Contains("organisation", execution.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScheduledPassportReport_MasksNumbers_ForAnOwnerWithoutEmployeesSensitive()
    {
        await using var db = CreateDb();
        var (tenantId, userId) = await SeedAuthorizedScheduleAsync(db);
        await GrantAsync(db, userId, "compliance.read");
        var employee = await db.Employees.SingleAsync();
        db.PassportRecords.Add(new PassportRecord
        {
            TenantId = tenantId, EmployeeId = employee.PublicId, EmployeeName = employee.FullName,
            PassportNumber = "P7654321", Nationality = "Indian", Status = "Active",
            ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(15)),
        });
        var schedule = await db.ReportSchedules.SingleAsync();
        schedule.ReportKey = "compliance.passport-expiry";
        schedule.ReportName = "Passport expiry";
        await db.SaveChangesAsync();

        var email = new RecordingEmail();
        using var services = BuildServices(db, email);
        var worker = new ReportScheduleWorker(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReportScheduleWorker>.Instance);

        await worker.ProcessOnceAsync(CancellationToken.None);

        var attachment = System.Text.Encoding.UTF8.GetString(Assert.Single(email.Messages).Attachment.Data);
        Assert.Contains("Engineer", attachment);
        Assert.DoesNotContain("P7654321", attachment);
        Assert.Contains("Restricted", attachment);
    }

    [Fact]
    public async Task ARecipientWhoIsNotAUserOfTheTenant_IsNotSentTheReport()
    {
        await using var db = CreateDb();
        await SeedAuthorizedScheduleAsync(db);
        var schedule = await db.ReportSchedules.SingleAsync();
        schedule.Recipients = "recipient@example.com, outsider@elsewhere.test";
        await db.SaveChangesAsync();

        var email = await RunWorkerAsync(db);

        Assert.Equal(["recipient@example.com"], email.Messages.Select(m => m.To));
        // Delivered, and the log says who was left out and why.
        var execution = await db.ReportExecutionLogs.SingleAsync();
        Assert.Equal("Success", execution.Status);
        Assert.Contains("outsider@elsewhere.test", execution.ErrorMessage);
    }

    [Fact]
    public async Task WhenNoRecipientMayReceiveTheReport_TheRunFails_SayingWhy()
    {
        await using var db = CreateDb();
        await SeedAuthorizedScheduleAsync(db);
        var schedule = await db.ReportSchedules.SingleAsync();
        schedule.Recipients = "outsider@elsewhere.test";
        await db.SaveChangesAsync();

        var email = await RunWorkerAsync(db);

        Assert.Empty(email.Messages);
        var execution = await db.ReportExecutionLogs.SingleAsync();
        Assert.Equal("Failed", execution.Status);
        Assert.Contains("outsider@elsewhere.test", execution.ErrorMessage);
    }

    [Fact]
    public async Task ARecipientScopedToOneCompany_DoesNotReceiveTheOwnersGroupWideReport()
    {
        await using var db = CreateDb();
        var (tenantId, _) = await SeedAuthorizedScheduleAsync(db);
        var company = new Company { TenantId = tenantId, LegalNameEn = "Only Co", IsActive = true };
        db.Companies.Add(company);
        var recipient = await db.Users.SingleAsync(u => u.Email == "recipient@example.com");
        recipient.IsGroupScope = false;
        db.UserEntityAccesses.Add(new UserEntityAccess { TenantId = tenantId, UserId = recipient.Id, CompanyId = company.Id, Role = "HR", IsActive = true });
        await db.SaveChangesAsync();

        var email = await RunWorkerAsync(db);

        Assert.Empty(email.Messages);
        Assert.Contains("recipient@example.com", (await db.ReportExecutionLogs.SingleAsync()).ErrorMessage);
    }

    [Fact]
    public async Task IdentityNumbersAreNeverEmailed_EvenWhenTheOwnerMaySeeThem()
    {
        // Email leaves the product: whoever the owner is, a mailed report carries no passport numbers.
        await using var db = CreateDb();
        var (tenantId, userId) = await SeedAuthorizedScheduleAsync(db);
        await GrantAsync(db, userId, "compliance.read");
        await GrantAsync(db, userId, "employees.sensitive");
        var employee = await db.Employees.SingleAsync();
        db.PassportRecords.Add(new PassportRecord
        {
            TenantId = tenantId, EmployeeId = employee.PublicId, EmployeeName = employee.FullName,
            PassportNumber = "P1112223", Status = "Active", ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(15)),
        });
        var schedule = await db.ReportSchedules.SingleAsync();
        schedule.ReportKey = "compliance.passport-expiry";
        await db.SaveChangesAsync();

        var email = await RunWorkerAsync(db);

        var attachment = System.Text.Encoding.UTF8.GetString(Assert.Single(email.Messages).Attachment.Data);
        Assert.DoesNotContain("P1112223", attachment);
        Assert.Contains("Restricted", attachment);
    }

    private static async Task<RecordingEmail> RunWorkerAsync(ZayraDbContext db)
    {
        var email = new RecordingEmail();
        using var services = BuildServices(db, email);
        var worker = new ReportScheduleWorker(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReportScheduleWorker>.Instance);
        await worker.ProcessOnceAsync(CancellationToken.None);
        return email;
    }

    private static async Task GrantAsync(ZayraDbContext db, Guid userId, string permissionKey)
    {
        var permission = new Permission { Id = Guid.NewGuid(), Key = permissionKey, Module = "Test" };
        db.Permissions.Add(permission);
        var roleId = (await db.UserRoles.FirstAsync(x => x.UserId == userId)).RoleId;
        db.RolePermissions.Add(new RolePermission { RoleId = roleId, PermissionId = permission.Id });
        await db.SaveChangesAsync();
    }

    private static ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ServiceProvider BuildServices(ZayraDbContext db, IEmailService email)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IEmailService>(email);
        services.AddSingleton<Zayra.Api.Application.Common.IDataScopeService>(new DataScopeService(db));
        // The worker now tells a human when a schedule fails (F3), so it resolves
        // INotificationService alongside the email service.
        services.AddSingleton<Zayra.Api.Infrastructure.Notifications.INotificationService>(TestNotifications.For(db));
        return services.BuildServiceProvider();
    }

    private static async Task<(Guid TenantId, Guid UserId)> SeedAuthorizedScheduleAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();
        var employeeReadPermissionId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Reports Tenant", Slug = $"reports-{Guid.NewGuid():N}" });
        db.Users.Add(new User
        {
            Id = userId, TenantId = tenantId, Email = "owner@example.com", NormalizedEmail = "OWNER@EXAMPLE.COM",
            FullName = "Report Owner", PasswordHash = "hash", IsActive = true, IsGroupScope = true
        });
        db.Roles.Add(new Role { Id = roleId, TenantId = tenantId, Name = "Custom Analyst", NormalizedName = "CUSTOM ANALYST" });
        // reports.schedule is the capability; employees.read is the data the headcount report shows,
        // and without manager.read it is organisation-wide, as for an HR user.
        db.Permissions.AddRange(
            new Permission { Id = permissionId, Key = "reports.schedule", Module = "Reports" },
            new Permission { Id = employeeReadPermissionId, Key = "employees.read", Module = "Employees" });
        db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
        db.RolePermissions.AddRange(
            new RolePermission { RoleId = roleId, PermissionId = permissionId },
            new RolePermission { RoleId = roleId, PermissionId = employeeReadPermissionId });
        // The recipient is a colleague in the same role: a scheduled report is only ever mailed to an
        // active user of the tenant who could open it themselves.
        var recipientId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = recipientId, TenantId = tenantId, Email = "recipient@example.com", NormalizedEmail = "RECIPIENT@EXAMPLE.COM",
            FullName = "Report Recipient", PasswordHash = "hash", IsActive = true, IsGroupScope = true
        });
        db.UserRoles.Add(new UserRole { UserId = recipientId, RoleId = roleId });
        db.Employees.Add(new Employee
        {
            Id = 1, TenantId = tenantId, EmployeeCode = "E-1", FullName = "Engineer", Department = "Engineering",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1)
        });
        db.ReportSchedules.Add(new ReportSchedule
        {
            TenantId = tenantId, CreatedBy = userId, ReportKey = "hr.headcount", ReportName = "Headcount",
            Category = "HR", FiltersJson = "{}", Frequency = "Daily", DeliveryMethod = "Email",
            Recipients = "recipient@example.com", ExportFormat = "JSON", IsActive = true,
            NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();
        return (tenantId, userId);
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<(string To, EmailAttachment Attachment)> Messages { get; } = [];
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        {
            Messages.Add((toAddress, Assert.Single(attachments!)));
            return Task.CompletedTask;
        }
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
