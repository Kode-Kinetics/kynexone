using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// Item 5: the database backstop for "one waiver, one punch". Should the per-employee advisory lock ever be bypassed,
/// another consumer that uses the waiver between the punch's read and its write makes the punch FAIL (refused
/// <c>selfie_required</c>) instead of consuming the same waiver a second time. Compiles against e835077f and fails there.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class SelfieWaiverBackstopPostgresTests
{
    private readonly PostgresFixture _fx;
    public SelfieWaiverBackstopPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Item5_AWaiverUsedByALockBypassingWriterMeanwhile_RefusesThePunch_InsteadOfConsumingItTwice()
    {
        Guid tenantId, waiverId;
        int employeeId;
        await using (var db = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            var employee = new Employee
            {
                TenantId = tenantId, EmployeeCode = $"BACKSTOP-{Guid.NewGuid():N}"[..20], FullName = "Backstop Person",
                Status = EmployeeStatuses.Active, JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            db.Employees.Add(employee);
            await db.SaveChangesAsync();
            employeeId = employee.Id;
            db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenantId, FeatureKey = SelfieWorld.SelfieKey, IsEnabled = true, ConfigJson = SelfieWorld.SignedOffConfig(requireSelfieForConsented: true) });
            db.BiometricConsents.Add(new BiometricConsent { TenantId = tenantId, EmployeeId = employeeId, PolicyVersion = "1", Channel = BiometricConsentChannels.Mobile });
            var waiver = new AttendanceEvidence
            {
                TenantId = tenantId, EmployeeId = employeeId, StorageKey = $"storage/documents/{tenantId:N}/attendance-evidence/{Guid.NewGuid():N}.jpg",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(9),
                PurgeState = AttendanceEvidencePurgeStates.Purged, PurgedAtUtc = DateTime.UtcNow.AddMinutes(-1), FailedReason = SelfieUploadFailureReasons.Busy,
            };
            db.AttendanceEvidence.Add(waiver);
            await db.SaveChangesAsync();
            waiverId = waiver.Id;
        }

        var rival = new RivalConsumer(_fx, tenantId, employeeId, waiverId);
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
            .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance, rival)
            .Options;
        await using (var db = new ZayraDbContext(options))
        {
            var controller = new AttendanceController(new AttendanceService(db, new NullNotifications(), new NullHttpClients()),
                new DataScopeService(db), new HrmHierarchyService(db, new NullAudit()), db, new AttendanceVerificationService(db, SelfieWorld.ResidentKsa))
            { ControllerContext = Context(tenantId, employeeId) };

            var punch = await controller.MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);

            Assert.True(rival.Ran, "the rival consumer never ran: the punch did not try to use the waiver");
            Assert.Equal("selfie_required", SelfieWorld.CodeOf(punch.Result));
        }

        await using var verify = _fx.CreateDb();
        var used = await verify.AttendanceEvidence.IgnoreQueryFilters().SingleAsync(e => e.Id == waiverId);
        Assert.Equal(rival.RawEventId, used.WaiverRawEventId); // the first consumer's use stands
        Assert.Equal(1, await verify.AttendanceRawEvents.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId)); // only the rival's
    }

    private static ControllerContext Context(Guid tenantId, int employeeId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", tenantId.ToString()),
                new Claim("sub", Guid.NewGuid().ToString()),
                new Claim("employee_id", employeeId.ToString()),
                new Claim("access_mode", AccessModes.Mobile),
            ], "Test")),
        };
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        return new ControllerContext { HttpContext = context };
    }

    /// <summary>
    /// Just before the punch writes its use of the waiver, another writer (on its own connection, WITHOUT the advisory
    /// lock — the bypass) uses the same waiver and commits.
    /// </summary>
    private sealed class RivalConsumer(PostgresFixture fx, Guid tenantId, int employeeId, Guid waiverId) : SaveChangesInterceptor
    {
        public bool Ran { get; private set; }
        public Guid RawEventId { get; } = Guid.NewGuid();

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!Ran && eventData.Context!.ChangeTracker.Entries<AttendanceEvidence>()
                    .Any(e => e.State == EntityState.Modified && e.Entity.Id == waiverId && e.Entity.WaiverConsumedAtUtc != null))
            {
                Ran = true;
                await using var other = fx.CreateDb();
                other.AttendanceRawEvents.Add(new AttendanceRawEvent
                {
                    Id = RawEventId, TenantId = tenantId, EmployeeId = employeeId, PunchTimestampUtc = DateTime.UtcNow.AddSeconds(-30),
                    PunchDirection = "In", Source = "rival", VerificationMethod = AttendanceVerificationMethods.None, PhotoReference = $"waiver:{waiverId}",
                });
                var waiver = await other.AttendanceEvidence.IgnoreQueryFilters().SingleAsync(e => e.Id == waiverId, ct);
                waiver.WaiverConsumedAtUtc = DateTime.UtcNow;
                waiver.WaiverRawEventId = RawEventId;
                await other.SaveChangesAsync(ct);
            }
            return result;
        }
    }
}

/// <summary>Item 5's model fact: waiver consumption is a concurrency token, like evidence use.</summary>
public sealed class SelfieWaiverConcurrencyTokenTests
{
    [Fact]
    public void Item5_WaiverConsumption_IsAConcurrencyToken_LikeEvidenceUse()
    {
        using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var entity = db.Model.FindEntityType(typeof(AttendanceEvidence))!;
        Assert.True(entity.FindProperty(nameof(AttendanceEvidence.WaiverConsumedAtUtc))!.IsConcurrencyToken);
        Assert.True(entity.FindProperty(nameof(AttendanceEvidence.UsedAtUtc))!.IsConcurrencyToken);
    }
}
