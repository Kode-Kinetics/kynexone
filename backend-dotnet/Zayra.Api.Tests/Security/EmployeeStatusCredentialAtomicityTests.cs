using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

public sealed class EmployeeStatusCredentialAtomicityTests
{
    [Fact]
    public async Task SuspensionInvalidatesEveryCredentialAndReactivationDoesNotGrantLogin()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = new Tenant { Name = "Lifecycle tenant", Slug = "lifecycle", IsActive = true };
        var user = new User
        {
            TenantId = tenant.Id,
            Email = "employee@lifecycle.test",
            NormalizedEmail = "EMPLOYEE@LIFECYCLE.TEST",
            FullName = "Lifecycle Employee",
            PasswordHash = "unimportant",
            Status = "Active",
            AccessMode = AccessModes.EssOnly,
            IsActive = true,
            IsEmailConfirmed = true,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-1)
        };
        var employee = new Employee
        {
            TenantId = tenant.Id,
            UserAccountId = user.Id,
            EmployeeCode = "LIFE-001",
            FullName = user.FullName,
            Status = EmployeeStatuses.Active,
            JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        db.AddRange(tenant, user, employee);
        await db.SaveChangesAsync();

        var link = new EmployeeUserAccount
        {
            TenantId = tenant.Id,
            EmployeeId = employee.Id,
            UserId = user.Id,
            AccessMode = AccessModes.EssOnly,
            Status = "Active",
            InvitationTokenHash = "live-invitation",
            InvitationExpiresAtUtc = DateTime.UtcNow.AddDays(1)
        };
        var reset = new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = "live-reset",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var challenge = new MfaChallengeToken
        {
            TenantId = tenant.Id,
            UserId = user.Id,
            TokenHash = "live-mfa",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
        };
        var refresh = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = "live-refresh",
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
        };
        db.AddRange(link, reset, challenge, refresh);
        await db.SaveChangesAsync();
        var originalStamp = TenantSessionSecurity.StampValue(user);

        var service = new EmployeeManagementService(
            db, new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db));
        var context = new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenant.Id);
        var effective = DateOnly.FromDateTime(DateTime.UtcNow);

        await service.ChangeStatusAsync(tenant.Id, employee.Id,
            new EmployeeStatusChangeRequest(EmployeeStatuses.Suspended, effective, "Security hold"),
            context, CancellationToken.None);

        db.ChangeTracker.Clear();
        var blockedUser = await db.Users.SingleAsync(x => x.Id == user.Id);
        var blockedLink = await db.EmployeeUserAccounts.SingleAsync(x => x.Id == link.Id);
        Assert.False(blockedUser.IsActive);
        Assert.Equal("Deactivated", blockedUser.Status);
        Assert.Equal(AccessModes.NoLogin, blockedUser.AccessMode);
        Assert.NotEqual(originalStamp, TenantSessionSecurity.StampValue(blockedUser));
        Assert.Equal(AccessModes.NoLogin, blockedLink.AccessMode);
        Assert.Equal("NoLogin", blockedLink.Status);
        Assert.Equal($"Employee lifecycle status: {EmployeeStatuses.Suspended}", blockedLink.LoginDisabledReason);
        Assert.Empty(blockedLink.InvitationTokenHash);
        Assert.Null(blockedLink.InvitationExpiresAtUtc);
        Assert.NotNull((await db.PasswordResetTokens.SingleAsync(x => x.Id == reset.Id)).UsedAtUtc);
        Assert.NotNull((await db.MfaChallengeTokens.SingleAsync(x => x.Id == challenge.Id)).UsedAtUtc);
        Assert.NotNull((await db.RefreshTokens.SingleAsync(x => x.Id == refresh.Id)).RevokedAtUtc);

        await service.ActivateAsync(tenant.Id, employee.Id,
            new EmployeeStatusChangeRequest(EmployeeStatuses.Active, effective, "Employment restored"),
            context, CancellationToken.None);

        db.ChangeTracker.Clear();
        var stillBlockedUser = await db.Users.SingleAsync(x => x.Id == user.Id);
        var stillBlockedLink = await db.EmployeeUserAccounts.SingleAsync(x => x.Id == link.Id);
        Assert.False(stillBlockedUser.IsActive);
        Assert.Equal("Deactivated", stillBlockedUser.Status);
        Assert.Equal(AccessModes.NoLogin, stillBlockedUser.AccessMode);
        Assert.Equal(AccessModes.NoLogin, stillBlockedLink.AccessMode);
        Assert.Equal("NoLogin", stillBlockedLink.Status);
        Assert.Equal(EmployeeStatuses.Active,
            await db.Employees.Where(x => x.Id == employee.Id).Select(x => x.Status).SingleAsync());
    }
}
