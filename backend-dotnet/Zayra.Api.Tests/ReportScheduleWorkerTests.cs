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

    private static ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ServiceProvider BuildServices(ZayraDbContext db, IEmailService email)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IEmailService>(email);
        services.AddSingleton<Zayra.Api.Application.Common.IDataScopeService>(new DataScopeService(db));
        return services.BuildServiceProvider();
    }

    private static async Task<(Guid TenantId, Guid UserId)> SeedAuthorizedScheduleAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Reports Tenant", Slug = $"reports-{Guid.NewGuid():N}" });
        db.Users.Add(new User
        {
            Id = userId, TenantId = tenantId, Email = "owner@example.com", NormalizedEmail = "OWNER@EXAMPLE.COM",
            FullName = "Report Owner", PasswordHash = "hash", IsActive = true, IsGroupScope = true
        });
        db.Roles.Add(new Role { Id = roleId, TenantId = tenantId, Name = "Custom Analyst", NormalizedName = "CUSTOM ANALYST" });
        db.Permissions.Add(new Permission { Id = permissionId, Key = "reports.schedule", Module = "Reports" });
        db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
        db.RolePermissions.Add(new RolePermission { RoleId = roleId, PermissionId = permissionId });
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
