using System.Data.Common;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Against REAL PostgreSQL: the overlap rule is evaluated in the same transaction as the insert, under one per-employee lock
/// (OvertimeEntryGuard), so two doors racing for the same hours record them once.
///
/// <para>The choreography is deterministic: contender A pauses right after its overlap check has read overtime_requests and
/// starts contender B. Without the lock B checks, inserts and commits inside that pause and A then inserts as well: two
/// requests for the same hours. With the lock B waits for A; A's pause times out, A commits, and B then sees A's row.</para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OvertimeEntryConcurrencyPostgresTests
{
    private readonly PostgresFixture _fx;
    public OvertimeEntryConcurrencyPostgresTests(PostgresFixture fx) => _fx = fx;

    private static readonly DateOnly Day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);
    private static DateTime At(int hour) => Day.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc);

    private sealed record World(Guid TenantId, int EmployeeId);

    [Fact]
    public async Task ManualRacingDetect_ForTheSameHours_RecordsThemOnce()
    {
        var w = await SeedAsync(withAttendanceDay: true);
        Task<ActionResult<DetectOvertimeResult>>? other = null;
        var gate = new AfterFirstOverlapCheck(() => other = Task.Run(async () =>
        {
            await using var db = _fx.CreateDb();
            return await Controller(db, w).DetectFromAttendance(new DetectOvertimeRequest(Day, Day, null), CancellationToken.None);
        }));

        var manual = await ManualAsync(w, gate);
        gate.Fired.Should().BeTrue("the choreography must actually interleave the second door");
        await other!;

        await using var check = _fx.CreateDb();
        (await check.OvertimeRequests.AsNoTracking().CountAsync(x => x.TenantId == w.TenantId))
            .Should().Be(1, "the same hours keyed by hand and detected from the punches are one entry, however the two calls interleave");
        manual.Result.Should().BeOfType<CreatedResult>("the first door holds the lock and commits");
    }

    [Fact]
    public async Task TwoManualRequests_RacingForTheSameWindow_OneIsRefused()
    {
        var w = await SeedAsync(withAttendanceDay: false);
        Task<ActionResult<OvertimeRequest>>? other = null;
        var gate = new AfterFirstOverlapCheck(() => other = Task.Run(async () =>
        {
            await using var db = _fx.CreateDb();
            return await Controller(db, w).CreateRequest(Create(w), CancellationToken.None);
        }));

        var first = await ManualAsync(w, gate);
        gate.Fired.Should().BeTrue();
        var second = await other!;

        await using var check = _fx.CreateDb();
        (await check.OvertimeRequests.AsNoTracking().CountAsync(x => x.TenantId == w.TenantId)).Should().Be(1);
        new[] { first.Result, second.Result }.Count(r => r is CreatedResult).Should().Be(1);
        new[] { first.Result, second.Result }.Count(r => r is ConflictObjectResult).Should().Be(1);
    }

    private static OvertimeRequestCreate Create(World w) => new(w.EmployeeId, null, null, Day, At(17), At(19), "Manual", "Stayed late");

    private async Task<ActionResult<OvertimeRequest>> ManualAsync(World w, AfterFirstOverlapCheck gate)
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance,
                Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance, gate);
        await using var db = new ZayraDbContext(options.Options);
        return await Controller(db, w).CreateRequest(Create(w), CancellationToken.None);
    }

    private static OvertimeController Controller(ZayraDbContext db, World w)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "HR Manager"), new("permission", "overtime.write"), new("permission", "overtime.read"),
        };
        return new OvertimeController(db, new OrgScope(), new HrmHierarchyService(db, new AuditService(db)))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } },
        };
    }

    private async Task<World> SeedAsync(bool withAttendanceDay)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = $"OT race {Guid.NewGuid():N}"[..20], CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"OTR-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
        };
        db.Companies.Add(company);
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "OTR-1", FullName = "Race Employee", Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-2), Salary = 12_000m,
        };
        db.Employees.Add(employee);
        db.OvertimePolicies.Add(new OvertimePolicy
        {
            TenantId = tenantId, Code = "STD", Name = "Standard", IsActive = true, RoundingRule = "None",
            MinimumMinutes = 30, MaximumMinutesPerDay = 600, MonthlyCapMinutes = 6000,
        });
        await db.SaveChangesAsync();
        if (withAttendanceDay)
        {
            db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
            {
                TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName, WorkDate = Day,
                FirstInUtc = At(8), LastOutUtc = At(19), TotalWorkedMinutes = 600, OvertimeMinutes = 120, Status = "Present",
            });
            await db.SaveChangesAsync();
        }
        return new World(tenantId, employee.Id);
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }

    /// <summary>
    /// After the first EXISTS over overtime_requests (the overlap check), starts the other contender and gives it up to
    /// three seconds to finish. Under the lock it cannot finish (it waits for this transaction), so the pause just times out.
    /// </summary>
    private sealed class AfterFirstOverlapCheck(Func<Task> startOther) : DbCommandInterceptor
    {
        private int _fired;
        public bool Fired => _fired == 1;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM overtime_requests", StringComparison.Ordinal)
                && command.CommandText.Contains("EXISTS", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                var other = Task.Run(startOther);
                await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
            }
            return result;
        }
    }
}
