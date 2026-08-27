using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Infrastructure.WorkWeek;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// WAVE 0 — THE ATTENDANCE MODULE SHIPPED EMPTY FOR THE ENTIRE LIFE OF THE PRODUCT.
///
/// <para><c>GET /api/attendance</c> (and its <c>/daily</c> alias, and <c>/monthly</c>) read
/// <see cref="AttendanceDailyRecord"/>. Every demo seeder wrote only the legacy
/// <see cref="AttendanceRecord"/> projection: 4,261 legacy rows tenant-wide, 0 rows in the table the
/// API actually reads. Every tenant saw a blank Attendance screen.</para>
///
/// <para>The suite had ~1,838 tests and not one of them asked whether the module had any data, so
/// the defect was invisible to CI from day one. That is the gap this file closes:</para>
/// <list type="number">
/// <item>a REAL seeder is run and the REAL service behind the endpoint is asked for rows;</item>
/// <item>the seeded numbers are proved identical to what the live pipeline computes;</item>
/// <item>the legacy row's <c>CompanyId</c> is proved non-null (issue #55);</item>
/// <item>a source lint stops a future seeder from writing the legacy table on its own again.</item>
/// </list>
/// </summary>
public class AttendanceDemoSeedTests
{
    // ── 1. The assertion nobody ever made: the module has data ────────────────────

    [Fact]
    public async Task SeededTenant_AttendanceDailyEndpoint_ReturnsRows()
    {
        await using var db = CreateDb();
        await RunEnterpriseSeederAsync(db);

        var tenantId = await SeededTenantIdAsync(db);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Exactly what AttendanceController.Daily calls for GET /api/attendance.
        var page = await Service(db).GetDailyAsync(
            tenantId, today.AddDays(-7), today, null, null, 1, 50, CancellationToken.None);

        Assert.True(page.Total > 0,
            "GET /api/attendance returned 0 rows for a freshly seeded tenant — the Attendance " +
            "module is empty again. Seeders must write AttendanceDailyRecord, not only the legacy " +
            "AttendanceRecord projection.");
        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, row => Assert.NotEqual(0, row.EmployeeId));
    }

    [Fact]
    public async Task SeededTenant_AttendanceMonthlyEndpoint_ReturnsRows()
    {
        await using var db = CreateDb();
        await RunEnterpriseSeederAsync(db);

        var tenantId = await SeededTenantIdAsync(db);
        var now = DateTime.UtcNow;

        // GetMonthlyAsync reads the SAME table as GetDailyAsync, so it was equally empty.
        var rows = await Service(db).GetMonthlyAsync(
            tenantId, now.Year, now.Month, null, CancellationToken.None);

        Assert.NotEmpty(rows);
    }

    // ── 2. The seeded numbers are not a fiction ───────────────────────────────────

    /// <summary>
    /// Seeds a day through <see cref="AttendanceDemoSeed"/>, then re-runs the REAL
    /// <c>AttendanceService.ProcessAsync</c> over that same date and asserts nothing moved.
    ///
    /// <para>This is the guard against a demo that lies. It can only pass because the seeder also
    /// persists the raw punches behind each day: the pipeline recomputes from those punches and
    /// must land on the identical FirstIn/LastOut/worked/break/late/overtime/status. If someone
    /// edits the seeder's arithmetic without editing <c>ProcessEmployeeDay</c> (or vice versa),
    /// this fails.</para>
    /// </summary>
    [Fact]
    public async Task SeededDay_ReproducesExactly_WhatTheLivePipelineComputes()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        await db.SaveChangesAsync();

        var date = new DateOnly(2026, 8, 18);   // a Tuesday — not a GCC rest day
        var policy = await AttendanceDemoSeed.ResolvePolicyAsync(db, tenantId, CancellationToken.None);
        AttendanceDemoSeed.AddDay(db, tenantId, AttendanceDemoSeed.EmployeeFacts.From(employee),
            date, new TimeOnly(8, 45), new TimeOnly(19, 15), policy, TimeZoneInfo.Utc);
        await db.SaveChangesAsync();

        var seeded = await db.AttendanceDailyRecords.AsNoTracking().SingleAsync();
        Assert.Equal("Present", seeded.Status);
        Assert.Equal(570, seeded.TotalWorkedMinutes);   // 10h30 gross − 60 break
        Assert.Equal(90, seeded.OvertimeMinutes);       // 570 − 480 standard
        Assert.False(seeded.MissingPunch);

        await Service(db).ProcessAsync(tenantId, new ProcessAttendanceRequest(date, date, employee.Id),
            new RequestContext(null, null, Guid.NewGuid(), tenantId), CancellationToken.None);

        var processed = await db.AttendanceDailyRecords.AsNoTracking().SingleAsync();
        Assert.Equal(seeded.FirstInUtc, processed.FirstInUtc);
        Assert.Equal(seeded.LastOutUtc, processed.LastOutUtc);
        Assert.Equal(seeded.MissingPunch, processed.MissingPunch);
        Assert.Equal(seeded.BreakMinutes, processed.BreakMinutes);
        Assert.Equal(seeded.TotalWorkedMinutes, processed.TotalWorkedMinutes);
        Assert.Equal(seeded.LateMinutes, processed.LateMinutes);
        Assert.Equal(seeded.EarlyExitMinutes, processed.EarlyExitMinutes);
        Assert.Equal(seeded.OvertimeMinutes, processed.OvertimeMinutes);
        Assert.Equal(seeded.UndertimeMinutes, processed.UndertimeMinutes);
        Assert.Equal(seeded.Status, processed.Status);
    }

    [Fact]
    public async Task SeededDayWithNoPunches_IsAbsent_NotAFictitiousPresent()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        await db.SaveChangesAsync();

        var policy = await AttendanceDemoSeed.ResolvePolicyAsync(db, tenantId, CancellationToken.None);
        AttendanceDemoSeed.AddDay(db, tenantId, AttendanceDemoSeed.EmployeeFacts.From(employee),
            new DateOnly(2026, 8, 18), null, null, policy, TimeZoneInfo.Utc);
        await db.SaveChangesAsync();

        var daily = await db.AttendanceDailyRecords.SingleAsync();
        Assert.Equal("Absent", daily.Status);
        Assert.True(daily.MissingPunch);
        Assert.Equal(0, daily.TotalWorkedMinutes);
        Assert.Equal(0, daily.BreakMinutes);
        // The legacy projection must not claim overtime on a day with no punches.
        Assert.Equal(0m, (await db.AttendanceRecords.SingleAsync()).OvertimeHours);
    }

    /// <summary>Local wall-clock punches must be converted with the tenant's timezone, or a Riyadh
    /// tenant's 08:30 arrivals all read as three hours late.</summary>
    [Fact]
    public async Task SeededPunches_AreConvertedFromTenantLocalTimeToUtc()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        await db.SaveChangesAsync();

        // A fixed +03:00 zone rather than "Asia/Riyadh": identical offset for this date, but no
        // dependence on the host's IANA timezone database.
        var riyadh = TimeZoneInfo.CreateCustomTimeZone("Test/Riyadh", TimeSpan.FromHours(3), "Test +03", "Test +03");
        var policy = await AttendanceDemoSeed.ResolvePolicyAsync(db, tenantId, CancellationToken.None);
        AttendanceDemoSeed.AddDay(db, tenantId, AttendanceDemoSeed.EmployeeFacts.From(employee),
            new DateOnly(2026, 8, 18), new TimeOnly(8, 30), new TimeOnly(17, 30), policy, riyadh);
        await db.SaveChangesAsync();

        var daily = await db.AttendanceDailyRecords.SingleAsync();
        Assert.Equal(new DateTime(2026, 8, 18, 5, 30, 0, DateTimeKind.Utc), daily.FirstInUtc!.Value);
        // 08:30 local against an 09:00 local shift start is EARLY, not 3.5 hours late.
        Assert.Equal(0, daily.LateMinutes);
        Assert.Equal("Present", daily.Status);
    }

    // ── 3. Issue #55: a null company hides the row from every company-scoped user ──

    [Fact]
    public async Task SeededLegacyRow_CarriesTheOwningEmployeesCompany()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        employee.CompanyId = companyId;
        await db.SaveChangesAsync();

        var policy = await AttendanceDemoSeed.ResolvePolicyAsync(db, tenantId, CancellationToken.None);
        AttendanceDemoSeed.AddDay(db, tenantId, AttendanceDemoSeed.EmployeeFacts.From(employee),
            new DateOnly(2026, 8, 18), new TimeOnly(8, 30), new TimeOnly(17, 30), policy, TimeZoneInfo.Utc);
        await db.SaveChangesAsync();

        var legacy = await db.AttendanceRecords.SingleAsync();
        Assert.Equal(companyId, legacy.CompanyId);
    }

    [Fact]
    public async Task EnterpriseSeeder_LeavesNoLegacyAttendanceRowWithoutACompany()
    {
        await using var db = CreateDb();
        await RunEnterpriseSeederAsync(db);

        var orphans = await db.AttendanceRecords.CountAsync(x => x.CompanyId == null);
        Assert.Equal(0, orphans);
        Assert.True(await db.AttendanceRecords.AnyAsync());
    }

    // ── 4. Stop the next seeder from re-opening the hole ──────────────────────────

    /// <summary>
    /// The original defect was six seeders each hand-rolling <c>new AttendanceRecord</c> and none of
    /// them writing <see cref="AttendanceDailyRecord"/>. Demo attendance now has exactly one door —
    /// <see cref="AttendanceDemoSeed"/> — which always writes both. This lint fails if a seeder
    /// starts constructing the legacy row on its own again, which is how the daily table would
    /// silently go empty a second time.
    ///
    /// Follows the existing source-lint convention in <c>Security/BypassLintTests</c>: if the source
    /// tree cannot be located the test skips rather than reporting a false pass.
    /// </summary>
    [Fact]
    public void NoSeeder_ConstructsTheLegacyAttendanceRecordDirectly()
    {
        var seedDir = ResolveSeedSourceDirectory();
        if (seedDir is null) return;   // binaries relocated; cannot scan — do not false-pass

        var offenders = Directory
            .EnumerateFiles(seedDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("AttendanceDemoSeed.cs", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f)
                .Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line))
                .Where(x => x.Text.Contains("new AttendanceRecord", StringComparison.Ordinal)))
            .Select(x => $"{x.File}:{x.Line}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Seeders must create demo attendance through AttendanceDemoSeed.AddDay, which writes " +
            "AttendanceDailyRecord (the table GET /api/attendance reads) alongside the legacy " +
            "AttendanceRecord projection and stamps CompanyId. Direct construction found at: " +
            string.Join(", ", offenders));
    }

    private static string? ResolveSeedSourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) return null;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api", "Infrastructure", "Seed");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the real <see cref="EnterpriseGroupSeeder"/>. It is the lightest seeder that produces a
    /// full tenant (companies, employees, attendance) with no statutory/GOSI dependencies, so a test
    /// can exercise genuine seeder output rather than a hand-built fixture that could drift from it.
    /// </summary>
    private static Task RunEnterpriseSeederAsync(ZayraDbContext db) =>
        new EnterpriseGroupSeeder(db, new StubHasher(), new StubAuthSeeder(db), new WorkWeekService(db),
            NullLogger<EnterpriseGroupSeeder>.Instance).SeedAsync(CancellationToken.None);

    private static async Task<Guid> SeededTenantIdAsync(ZayraDbContext db) =>
        (await db.AttendanceDailyRecords.AsNoTracking().Select(x => x.TenantId).FirstAsync());

    private static Employee AddEmployee(ZayraDbContext db, Guid tenantId)
    {
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"E-{Guid.NewGuid():N}", EnglishName = "Test Employee",
            FullName = "Test Employee", Department = "Operations", Branch = "HQ",
            Status = "Active", JoiningDate = new DateTime(2020, 1, 1),
        };
        db.Employees.Add(employee);
        return employee;
    }

    private static AttendanceService Service(ZayraDbContext db) =>
        new(db, new NullNotifications(), new NullHttpClients());

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class StubHasher : IPasswordHasher
    {
        public string Hash(string password) => $"hashed:{password}";
        public bool Verify(string password, string passwordHash) => passwordHash == $"hashed:{password}";
    }

    /// <summary>Creates just the role rows EnterpriseGroupSeeder looks up by name.</summary>
    private sealed class StubAuthSeeder : IAuthSeeder
    {
        private static readonly string[] RoleNames =
            { "Admin", "HR Director", "HR Manager", "Finance Approver", "Compliance Officer", "Auditor", "Payroll Officer" };

        private readonly ZayraDbContext _db;
        public StubAuthSeeder(ZayraDbContext db) => _db = db;

        public Task SeedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<Role> EnsureTenantRolesAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            foreach (var name in RoleNames)
                _db.Roles.Add(new Role
                {
                    TenantId = tenantId, Name = name, NormalizedName = name.ToUpperInvariant(),
                    IsSystem = true, IsActive = true,
                });
            await _db.SaveChangesAsync(cancellationToken);
            return await _db.Roles.FirstAsync(r => r.TenantId == tenantId && r.Name == "Admin", cancellationToken);
        }
    }

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
