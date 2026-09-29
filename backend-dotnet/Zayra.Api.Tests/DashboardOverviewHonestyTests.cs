using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// /api/dashboard/full — the approval queue's subject employee, the compliance-alert filters
/// (active employees only, expiring document keys only, no sentinel dates, year in the title,
/// uncapped totals) and the "present today" tile reading AttendanceDailyRecords on the
/// tenant-local date.
///
/// <para>The in-memory harness has no <see cref="IHttpContextAccessor"/>, so it runs in system scope
/// where the company filter never engages; the company-scope half of these rules is asserted
/// against Postgres in <see cref="DashboardOverviewCompanyScopeTests"/>, and relational translation
/// against SQLite in <see cref="Overview_And_Summary_Translate_On_A_Relational_Provider"/>.</para>
/// </summary>
public class DashboardOverviewHonestyTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DashboardController Ctrl(ZayraDbContext db, Guid tenantId)
    {
        if (!db.Tenants.Any(t => t.Id == tenantId))
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Honesty", Slug = $"honesty-{tenantId:N}" });
            db.SaveChanges();
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        }, "test"));
        return new DashboardController(db,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            new HonestyUnrestrictedScope())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
        };
    }

    private static async Task<DashboardFullDto> Full(ZayraDbContext db, Guid tenantId)
    {
        var result = await Ctrl(db, tenantId).Full();
        return Assert.IsType<DashboardFullDto>(Assert.IsType<OkObjectResult>(result).Value);
    }

    private static Employee Emp(ZayraDbContext db, Guid tenantId, string code, string name, string status = "Active")
    {
        var e = new Employee { TenantId = tenantId, EmployeeCode = code, FullName = name, Status = status };
        db.Employees.Add(e);
        db.SaveChanges();
        return e;
    }

    private static void Compliance(ZayraDbContext db, Guid tenantId, int employeeId, string key, string label, DateOnly? expiry)
        => db.EmployeeComplianceRecords.Add(new EmployeeComplianceRecord
        {
            TenantId = tenantId, EmployeeId = employeeId, CountryCode = "SA",
            FieldKey = key, FieldLabel = label, FieldValue = "X", ExpiryDate = expiry,
        });

    private static DateOnly UtcToday => DateOnly.FromDateTime(DateTime.UtcNow);

    // ── 1. Approval queue ─────────────────────────────────────────────────────

    [Fact]
    public async Task ApprovalItem_CarriesTheSubjectEmployee_AndARowWithoutOneIsKept()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        var aisha = Emp(db, tid, "E-100", "Aisha Khan");
        db.ApprovalRequests.AddRange(
            new ApprovalRequest { TenantId = tid, EntityName = "LeaveRequest", Title = "Annual leave", Status = "Pending",
                RequestedForEmployeeId = aisha.Id, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1) },
            new ApprovalRequest { TenantId = tid, EntityName = "PayrollRun", Title = "Payroll Sep", Status = "Pending",
                RequestedForEmployeeId = null, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2) },
            // Points at an employee id that does not exist (or is invisible) — must not drop the row.
            new ApprovalRequest { TenantId = tid, EntityName = "Loan", Title = "Loan", Status = "Pending",
                RequestedForEmployeeId = 987_654, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-3) });
        db.SaveChanges();

        var queue = (await Full(db, tid)).Overview.ApprovalQueue;

        queue.Should().HaveCount(3, "the employee join is a LEFT join and never drops an approval");
        queue.Select(q => q.Title).Should().ContainInOrder("Annual leave", "Payroll Sep", "Loan");
        queue[0].EmployeeId.Should().Be(aisha.Id);
        queue[0].EmployeeCode.Should().Be("E-100");
        queue[0].EmployeeName.Should().Be("Aisha Khan");
        queue[1].EmployeeName.Should().BeNull();
        queue[2].EmployeeId.Should().BeNull();
    }

    // ── 2. Compliance alerts ──────────────────────────────────────────────────

    [Fact]
    public async Task Alert_ForAnInactiveEmployee_IsExcluded()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        var active = Emp(db, tid, "A1", "Active Person");
        var leaver = Emp(db, tid, "L1", "Leaver Person", status: "Terminated");
        Compliance(db, tid, active.Id, "iqama_number", "Iqama (residence permit)", UtcToday.AddDays(10));
        Compliance(db, tid, leaver.Id, "iqama_number", "Iqama (residence permit)", UtcToday.AddDays(-5));
        db.SaveChanges();

        var overview = (await Full(db, tid)).Overview;

        overview.Alerts.Should().ContainSingle().Which.EmployeeId.Should().Be(active.Id);
        overview.ComplianceAlertsTotal.Should().Be(1);
        overview.ComplianceCriticalTotal.Should().Be(0, "the only expired record belongs to a leaver");
    }

    [Fact]
    public async Task Alert_ForANonExpiringKey_LikeAGosiReference_IsExcluded()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        var e = Emp(db, tid, "G1", "Gosi Person");
        Compliance(db, tid, e.Id, "gosi_reference", "GOSI Reference", UtcToday.AddDays(-30));
        Compliance(db, tid, e.Id, "muqeem_reference", "Muqeem Reference", UtcToday.AddDays(5));
        Compliance(db, tid, e.Id, "Passport_Number", "Passport Number", UtcToday.AddDays(20)); // key match is case-insensitive
        db.SaveChanges();

        var overview = (await Full(db, tid)).Overview;

        overview.Alerts.Should().ContainSingle().Which.Kind.Should().Be("Passport_Number");
        overview.ComplianceAlertsTotal.Should().Be(1);
    }

    [Fact]
    public async Task Alert_WithASentinelPre1900Date_IsExcluded()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        var e = Emp(db, tid, "S1", "Sentinel Person");
        Compliance(db, tid, e.Id, "passport_number", "Passport Number", new DateOnly(1, 1, 1));
        Compliance(db, tid, e.Id, "visa_number", "Visa", new DateOnly(1899, 12, 31));
        Compliance(db, tid, e.Id, "qid", "QID", null);
        db.SaveChanges();

        var overview = (await Full(db, tid)).Overview;

        overview.Alerts.Should().BeEmpty();
        overview.ComplianceAlertsTotal.Should().Be(0);
        overview.ComplianceCriticalTotal.Should().Be(0);
    }

    [Fact]
    public async Task AlertTitle_IncludesTheYear_AndTheStructuredFieldsArePopulated()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        var e = Emp(db, tid, "Y1", "Omar Ali");
        var expired = UtcToday.AddDays(-400);
        var soon = UtcToday.AddDays(12);
        Compliance(db, tid, e.Id, "iqama_number", "Iqama (residence permit)", expired);
        Compliance(db, tid, e.Id, "passport_number", "Passport Number", soon);
        db.SaveChanges();

        var alerts = (await Full(db, tid)).Overview.Alerts;

        alerts.Should().HaveCount(2);
        var lapsed = alerts[0];
        lapsed.Title.Should().Be($"Iqama (residence permit) expired {expired.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}");
        lapsed.Title.Should().Contain(expired.Year.ToString());
        lapsed.Severity.Should().Be("Critical");
        lapsed.EmployeeId.Should().Be(e.Id);
        lapsed.EmployeeName.Should().Be("Omar Ali");
        lapsed.ExpiryDate.Should().Be(expired);
        lapsed.DaysRemaining.Should().Be(-400);
        lapsed.Kind.Should().Be("iqama_number");

        alerts[1].Title.Should().StartWith("Passport Number expires ").And.EndWith(soon.Year.ToString());
        alerts[1].Severity.Should().Be("Warning");
        alerts[1].DaysRemaining.Should().Be(12);
    }

    [Fact]
    public async Task AlertTotals_AreUncapped_WhileTheListStaysCappedAndOrderedByExpiry()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        // 15 alertable records: 4 expired, 11 upcoming (up to 84 days out, inside the 90-day horizon).
        // Plus one beyond the horizon (not alertable).
        for (var i = 0; i < 15; i++)
        {
            var e = Emp(db, tid, $"T{i}", $"Person {i}");
            Compliance(db, tid, e.Id, "passport_number", "Passport Number", UtcToday.AddDays(i < 4 ? -(i + 1) : i * 6));
        }
        var far = Emp(db, tid, "FAR", "Far Future");
        Compliance(db, tid, far.Id, "passport_number", "Passport Number", UtcToday.AddDays(200));
        db.SaveChanges();

        var overview = (await Full(db, tid)).Overview;

        overview.Alerts.Should().HaveCount(12, "the list is capped at 12");
        overview.Alerts.Select(a => a.ExpiryDate).Should().BeInAscendingOrder();
        overview.ComplianceAlertsTotal.Should().Be(15, "the total counts the whole filtered set, not the capped list");
        overview.ComplianceCriticalTotal.Should().Be(4);
    }

    // ── 3. Present today ──────────────────────────────────────────────────────

    [Fact]
    public async Task PresentToday_CountsLateAndHalfDay_FromDailyRecords()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        var emps = Enumerable.Range(1, 5).Select(i => Emp(db, tid, $"P{i}", $"P {i}")).ToList();
        var statuses = new[] { AttendanceStatuses.Present, AttendanceStatuses.Late, AttendanceStatuses.HalfDay,
                               AttendanceStatuses.Absent, AttendanceStatuses.OnLeave };
        for (var i = 0; i < 5; i++)
            db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
            {
                TenantId = tid, EmployeeId = emps[i].Id, WorkDate = UtcToday, Status = statuses[i],
            });
        db.SaveChanges();

        var summary = (await Full(db, tid)).Summary;

        summary.PresentToday.Should().Be(3, "Late and Half day arrivals turned up");
        summary.Absent.Should().Be(1);
        summary.OnLeave.Should().Be(1);
        summary.AttendanceRecordsToday.Should().Be(5);
    }

    [Fact]
    public async Task AttendanceRecordsToday_IsZero_WhenNothingWasCapturedToday()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        var e = Emp(db, tid, "Z1", "Zero");
        // Yesterday's capture and a legacy-mirror row for today do not count as "captured today".
        db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
        {
            TenantId = tid, EmployeeId = e.Id, WorkDate = UtcToday.AddDays(-2), Status = AttendanceStatuses.Present,
        });
        db.AttendanceRecords.Add(new AttendanceRecord { TenantId = tid, EmployeeId = e.Id, WorkDate = UtcToday, Status = "Present" });
        db.SaveChanges();

        var summary = (await Full(db, tid)).Summary;

        summary.AttendanceRecordsToday.Should().Be(0);
        summary.PresentToday.Should().Be(0);
    }

    [Fact]
    public async Task PresentToday_UsesTheTenantLocalDate_NotTheUtcDate()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        const string zone = "Pacific/Kiritimati"; // UTC+14: local date differs from UTC for 14h a day
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tid, DefaultTimezone = zone });
        var emps = Enumerable.Range(1, 3).Select(i => Emp(db, tid, $"K{i}", $"K {i}")).ToList();
        var local = TenantTimeZone.LocalDate(TenantTimeZone.FromId(zone), DateTime.UtcNow);
        // One present row on the tenant-local date; decoys on every OTHER date "today" could be.
        db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
            { TenantId = tid, EmployeeId = emps[0].Id, WorkDate = local, Status = AttendanceStatuses.Present });
        var decoys = new[] { UtcToday.AddDays(-1), UtcToday, UtcToday.AddDays(1) }.Where(d => d != local).ToList();
        for (var i = 0; i < decoys.Count; i++)
            db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
                { TenantId = tid, EmployeeId = emps[i + 1].Id, WorkDate = decoys[i], Status = AttendanceStatuses.Present });
        db.SaveChanges();

        var summary = (await Full(db, tid)).Summary;

        summary.PresentToday.Should().Be(1);
        summary.AttendanceRecordsToday.Should().Be(1);
    }

    // ── Wire shape ─────────────────────────────────────────────────────────────

    [Fact]
    public void NewFields_SerializeAsCamelCase_WithAnIsoDate()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var alert = JsonSerializer.Serialize(new DashboardAlertDto("t", "Critical", 7, "N", new DateOnly(2026, 1, 1), -3, "iqama_number"), web);
        alert.Should().Contain("\"employeeId\":7").And.Contain("\"employeeName\":\"N\"")
            .And.Contain("\"expiryDate\":\"2026-01-01\"").And.Contain("\"daysRemaining\":-3").And.Contain("\"kind\":\"iqama_number\"");

        var item = JsonSerializer.Serialize(new ApprovalQueueItemDto(Guid.Empty, "t", "m", DateTime.UnixEpoch, 1, "E1", "Name"), web);
        item.Should().Contain("\"employeeId\":1").And.Contain("\"employeeCode\":\"E1\"").And.Contain("\"employeeName\":\"Name\"");

        var summary = JsonSerializer.Serialize(new DashboardSummaryDto(0, 0, 0, 0, 0, 0m, 0, 4), web);
        summary.Should().Contain("\"attendanceRecordsToday\":4");
    }

    // ── Relational translation ─────────────────────────────────────────────────

    /// <summary>
    /// The in-memory provider evaluates LINQ in process and would accept a query Postgres cannot
    /// translate (left join + DefaultIfEmpty, GROUP BY constant with a filtered count, ToLower over
    /// an array Contains). SQLite is relational, so a translation failure surfaces here.
    /// </summary>
    [Fact]
    public async Task Overview_And_Summary_Translate_On_A_Relational_Provider()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var tid = Guid.NewGuid();
        var e = Emp(db, tid, "R1", "Relational");
        Compliance(db, tid, e.Id, "iqama_number", "Iqama", UtcToday.AddDays(-2));
        Compliance(db, tid, e.Id, "gosi_reference", "GOSI", UtcToday.AddDays(-2));
        db.ApprovalRequests.Add(new ApprovalRequest { TenantId = tid, EntityName = "Leave", Title = "L", Status = "Pending", RequestedForEmployeeId = e.Id });
        db.AttendanceDailyRecords.Add(new AttendanceDailyRecord { TenantId = tid, EmployeeId = e.Id, WorkDate = UtcToday, Status = AttendanceStatuses.Late });
        db.SaveChanges();

        // Summary and Overview are called directly: /full also runs the trend and payroll builders,
        // whose decimal Sum SQLite cannot aggregate (a provider limit unrelated to this change).
        var ctrl = Ctrl(db, tid);
        var payload = new
        {
            Summary = Assert.IsType<DashboardSummaryDto>(Assert.IsType<OkObjectResult>(await ctrl.Summary(CancellationToken.None)).Value),
            Overview = Assert.IsType<DashboardOverviewDto>(Assert.IsType<OkObjectResult>(await ctrl.Overview(CancellationToken.None)).Value),
        };

        payload.Overview.ApprovalQueue.Should().ContainSingle().Which.EmployeeName.Should().Be("Relational");
        payload.Overview.Alerts.Should().ContainSingle().Which.Kind.Should().Be("iqama_number");
        payload.Overview.ComplianceAlertsTotal.Should().Be(1);
        payload.Overview.ComplianceCriticalTotal.Should().Be(1);
        payload.Summary.PresentToday.Should().Be(1);
        payload.Summary.AttendanceRecordsToday.Should().Be(1);
    }
}

/// <summary>
/// The company half of the rules: AttendanceDailyRecord and EmployeeComplianceRecord have no
/// CompanyId of their own, so their scope comes ONLY from the Employee join. A company-A caller must
/// not see company B's expiring Iqama, B's present employee, or B's employee name on an approval.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class DashboardOverviewCompanyScopeTests
{
    private readonly PostgresFixture _fx;
    public DashboardOverviewCompanyScopeTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task CompanyA_DoesNotSee_CompanyB_Alerts_Attendance_Or_EmployeeNames()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid tenantId, companyA, companyB;
        int empA, empB;
        await using (var seed = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);
            var a = Company(tenantId, "Alpha");
            var b = Company(tenantId, "Beta");
            seed.Companies.AddRange(a, b);
            await seed.SaveChangesAsync();
            companyA = a.Id; companyB = b.Id;

            var ea = Employee(tenantId, a.Id, "Alpha Person");
            var eb = Employee(tenantId, b.Id, "Beta Person");
            seed.Employees.AddRange(ea, eb);
            await seed.SaveChangesAsync();
            empA = ea.Id; empB = eb.Id;

            foreach (var id in new[] { empA, empB })
            {
                seed.EmployeeComplianceRecords.Add(new EmployeeComplianceRecord
                {
                    TenantId = tenantId, EmployeeId = id, CountryCode = "SA", FieldKey = "iqama_number",
                    FieldLabel = "Iqama (residence permit)", FieldValue = "X", ExpiryDate = today.AddDays(-1),
                });
                seed.AttendanceDailyRecords.Add(new AttendanceDailyRecord
                {
                    TenantId = tenantId, EmployeeId = id, WorkDate = today, Status = AttendanceStatuses.Late,
                });
            }
            // An approval owned by company A whose subject is a company-B employee: the row is A's to
            // see, the name is not.
            seed.ApprovalRequests.Add(new ApprovalRequest
            {
                TenantId = tenantId, CompanyId = companyA, EntityName = "Transfer", Title = "Transfer",
                Status = "Pending", RequestedForEmployeeId = empB, CreatedAtUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var accessor = new ScopeAccessor();
        await using var db = _fx.CreateDbWithAccessor(accessor);
        var http = ScopedContext(tenantId, companyA);
        accessor.HttpContext = http;
        var ctrl = new DashboardController(db,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new ScopeUnrestricted())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var payload = (DashboardFullDto)((OkObjectResult)await ctrl.Full(6, CancellationToken.None)).Value!;

        payload.Summary.TotalEmployees.Should().Be(1, "fixture sanity: company A has exactly one employee");
        payload.Summary.PresentToday.Should().Be(1, "only company A's Late employee is visible");
        payload.Summary.AttendanceRecordsToday.Should().Be(1);
        payload.Overview.Alerts.Should().ContainSingle().Which.EmployeeId.Should().Be(empA);
        payload.Overview.ComplianceAlertsTotal.Should().Be(1);
        payload.Overview.ComplianceCriticalTotal.Should().Be(1);
        var item = payload.Overview.ApprovalQueue.Should().ContainSingle().Subject;
        item.EmployeeId.Should().BeNull("the subject belongs to company B, which this caller cannot see");
        item.EmployeeName.Should().BeNull();
    }

    private static Company Company(Guid tenantId, string name) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = name, CountryCode = "SAU",
        Jurisdiction = "KSA-mainland", RegistrationNumber = $"REG-{Guid.NewGuid():N}", DefaultCurrency = "SAR",
        IsActive = true, CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static Employee Employee(Guid tenantId, Guid companyId, string name) => new()
    {
        TenantId = tenantId, CompanyId = companyId, EmployeeCode = $"OV-{Guid.NewGuid():N}".Substring(0, 14),
        FullName = name, Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static HttpContext ScopedContext(Guid tenantId, Guid companyId)
    {
        var accessJson = JsonSerializer.Serialize(new { c = companyId, r = "Viewer" });
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", tenantId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim("entity_access", accessJson),
            }, "Test")),
        };
    }

    private sealed class ScopeAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class ScopeUnrestricted : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}

file sealed class HonestyUnrestrictedScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}
