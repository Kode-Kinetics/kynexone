using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Employees;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// GET /api/pay-approvals took 41 s on production: it ran about forty queries PER EMPLOYEE, and every round trip pays ~1.8 s across regions.
/// A fixed number of set-based queries now serves any number of employees. These tests pin (1) that count, and (2) the response, byte for byte,
/// for a dataset that exercises every branch: a pending package, a rejected one, a barred creator, a company outside the caller's scope, an employee
/// already in post, an employee with no pay, an approved package, a package that drifted from its approval, a shared IBAN, a designation and a job title.
/// </summary>
public class PayApprovalsListQueryCountTests(Xunit.Abstractions.ITestOutputHelper output)
{
    internal static readonly Guid Tenant = Guid.Parse("11111111-0000-0000-0000-000000000001");
    internal static readonly Guid CompanyA = Guid.Parse("22222222-0000-0000-0000-00000000000a");
    private static readonly Guid CompanyB = Guid.Parse("22222222-0000-0000-0000-00000000000b");
    private static readonly Guid Caller = Guid.Parse("33333333-0000-0000-0000-000000000001");
    private static readonly Guid Huda = Guid.Parse("33333333-0000-0000-0000-000000000002");
    private static readonly Guid Omar = Guid.Parse("33333333-0000-0000-0000-000000000003");
    private static readonly DateTime T0 = new(2026, 10, 26, 9, 15, 0, DateTimeKind.Utc);

    private sealed class Counter : DbCommandInterceptor
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Reset() => Interlocked.Exchange(ref _count, 0);
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Interlocked.Increment(ref _count); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _count); return ValueTask.FromResult(result); }
    }

    private sealed class NoAudit : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static async Task<(ZayraDbContext Db, Counter Counter, SqliteConnection Connection)> OpenAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var counter = new Counter();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(connection).AddInterceptors(counter).Options);
        await db.Database.EnsureCreatedAsync();
        await SeedBaseAsync(db);
        return (db, counter, connection);
    }

    /// <summary>The tenant, its two companies and the three people the audit rows name.</summary>
    internal static async Task SeedBaseAsync(ZayraDbContext db)
    {
        db.Tenants.Add(new Tenant { Id = Tenant, Name = "Pay approvals", Slug = "pay-approvals" });
        db.Companies.AddRange(
            new Company { Id = CompanyA, TenantId = Tenant, LegalNameEn = "Acme Arabia LLC", TradeName = "Acme Arabia", CountryCode = "SAU", IsActive = true, DefaultCurrency = "SAR" },
            new Company { Id = CompanyB, TenantId = Tenant, LegalNameEn = "Beta Holdings", CountryCode = "SAU", IsActive = true, DefaultCurrency = "SAR" });
        db.Users.AddRange(User(Caller, "Fatima Finance"), User(Huda, "Huda HR Officer"), User(Omar, "Omar Reviewer"));
        await db.SaveChangesAsync();
    }

    private static User User(Guid id, string name) => new() { Id = id, TenantId = Tenant, FullName = name, Email = $"{id:N}@t.test", NormalizedEmail = $"{id:N}@T.TEST" };

    private static async Task<Employee> EmployeeAsync(ZayraDbContext db, Guid company, string code, string name, string status = "Draft", Guid? createdBy = null,
        Guid? designationId = null, string jobTitle = "")
    {
        var e = new Employee
        {
            TenantId = Tenant, CompanyId = company, EmployeeCode = code, FullName = name, EnglishName = name, Status = status, JoiningDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedBy = createdBy ?? Huda, DesignationId = designationId, JobTitle = jobTitle, WorkEmail = $"{code}@t.test", Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    private static async Task PendingPackageAsync(ZayraDbContext db, Employee e, decimal basic = 8000m)
    {
        db.SalaryChangeRequests.Add(new SalaryChangeRequest
        {
            TenantId = Tenant, CompanyId = e.CompanyId, Source = "NewHire", EmployeeId = e.Id, EmployeeCode = e.EmployeeCode, EmployeeName = e.FullName, SalaryStructureId = Guid.NewGuid(),
            BasicSalary = basic, HousingAllowance = 2000m, TransportAllowance = 500m, Currency = "SAR", Status = SalaryChangeRequest.Pending, RequestedAtUtc = T0,
        });
        await db.SaveChangesAsync();
    }

    private static async Task ProfileAsync(ZayraDbContext db, Employee e, string iban, string bank = "Riyad Bank")
    {
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = Tenant, EmployeeId = e.Id, Iban = iban, BankName = bank, CreatedBy = Huda });
        await db.SaveChangesAsync();
    }

    private static async Task StructureAsync(ZayraDbContext db, Employee e, decimal basic)
    {
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = Tenant, EmployeeId = e.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = basic, HousingAllowance = 1000m, Currency = "SAR", IsActive = true,
            EffectiveDate = new DateOnly(2026, 12, 1), CreatedAtUtc = T0,
        });
        await db.SaveChangesAsync();
    }

    private static async Task GapAsync(ZayraDbContext db, Employee e, DateTime? resolvedAt = null, string? raw = null)
    {
        db.EmployeeImportGaps.Add(new EmployeeImportGap
        {
            TenantId = Tenant, CompanyId = e.CompanyId, ImportBatchId = Guid.Empty, EmployeeId = e.Id, RowNumber = 0, GapType = CompensationApprovalGate.GapType, GapCategory = "pay",
            Detail = "Pay package and bank details are awaiting approval by Finance.", ResolvedAtUtc = resolvedAt, RawValue = raw,
        });
        await db.SaveChangesAsync();
    }

    private static async Task AuditAsync(ZayraDbContext db, Employee e, string action, Guid user, DateTime at, string? metadata = null)
    {
        db.AuditLogs.Add(new AuditLog { TenantId = Tenant, UserId = user, Action = action, EntityName = "Employee", EntityId = e.Id.ToString(), CreatedAtUtc = at, Metadata = metadata });
        await db.SaveChangesAsync();
    }

    /// <summary>One employee per branch of the list's rules.</summary>
    internal static async Task SeedScenarioAsync(ZayraDbContext db)
    {
        var designation = new Designation { Id = Guid.Parse("44444444-0000-0000-0000-000000000001"), TenantId = Tenant, CompanyId = CompanyA, TitleEn = "Senior Analyst", Code = "SA" };
        db.Designations.Add(designation);
        await db.SaveChangesAsync();

        var shared = "SA03 8000 0000 6080 1016 7519";
        var e1 = await EmployeeAsync(db, CompanyA, "E-001", "Layla Al-Harbi", designationId: designation.Id);          // pending first package
        await PendingPackageAsync(db, e1); await ProfileAsync(db, e1, shared); await AuditAsync(db, e1, CompensationApprovalGate.CreatedAction, Huda, T0);
        var e2 = await EmployeeAsync(db, CompanyA, "E-002", "Omar Al-Qahtani", jobTitle: "Analyst");                    // sent back, same account as E-001
        await GapAsync(db, e2); await StructureAsync(db, e2, 5000m); await ProfileAsync(db, e2, shared.Replace(" ", "").ToLowerInvariant(), "Al Rajhi Bank");
        await AuditAsync(db, e2, CompensationApprovalGate.WrittenAction, Huda, T0.AddMinutes(5));
        await AuditAsync(db, e2, PayApprovalsController.RejectedAction, Omar, T0.AddMinutes(30), "{\"reason\":\"IBAN does not match the contract\"}");
        var e3 = await EmployeeAsync(db, CompanyA, "E-003", "Created By Caller", createdBy: Caller);                    // the caller made it: barred
        await PendingPackageAsync(db, e3); await ProfileAsync(db, e3, "SA4420000001234567891234");
        var e4 = await EmployeeAsync(db, CompanyB, "E-004", "Other Company Hire", "Invited");                           // outside a company-scoped caller
        await PendingPackageAsync(db, e4, 6000m); await ProfileAsync(db, e4, "SA5530000000009876543210");
        var e5 = await EmployeeAsync(db, CompanyA, "E-005", "Already In Post", "Active");                               // in post: the salary-change approval owns it
        await GapAsync(db, e5); await StructureAsync(db, e5, 7000m);
        await EmployeeAsync(db, CompanyA, "E-006", "No Pay Yet");                                                        // nothing to approve
        var e7 = await EmployeeAsync(db, CompanyA, "E-007", "Approved Hire");                                            // approved on exactly these figures
        await StructureAsync(db, e7, 4000m); await ProfileAsync(db, e7, "SA6610000000001111222233");
        var approved = CompensationApprovalGate.PayFingerprint(await CompensationApprovalGate.LivePackageAsync(db, Tenant, e7.Id, CancellationToken.None));
        await GapAsync(db, e7, T0, CompensationApprovalGate.ApprovedMarker + approved);
        var e8 = await EmployeeAsync(db, CompanyA, "E-008", "Drifted Hire");                                             // approved, then the figures changed
        await StructureAsync(db, e8, 4500m); await ProfileAsync(db, e8, "SA7710000000002222333344");
        await GapAsync(db, e8, T0, CompensationApprovalGate.ApprovedMarker + "deadbeef");
    }

    private static async Task SeedPendingAsync(ZayraDbContext db, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var e = await EmployeeAsync(db, CompanyA, $"P-{i:D3}", $"Pending Hire {i}");
            await PendingPackageAsync(db, e, 5000m + i); await ProfileAsync(db, e, $"SA44200000012345678912{i:D2}");
            await AuditAsync(db, e, CompensationApprovalGate.CreatedAction, Huda, T0.AddMinutes(i));
        }
    }

    internal static PayApprovalsController Controller(ZayraDbContext db, Guid? scopedToCompany = null)
    {
        var claims = new List<Claim> { new("tenant_id", Tenant.ToString()), new(ClaimTypes.NameIdentifier, Caller.ToString()), new("permission", "payroll.approve") };
        if (scopedToCompany is Guid cid) claims.Add(new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = EntityScopeModes.Companies, c = new[] { cid } })));
        else claims.Add(new Claim("is_group_scope", "true"));
        return new PayApprovalsController(db, new NoAudit())
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } } };
    }

    /// <summary>
    /// The list as a request sees it: a FRESH context (nothing tracked, so every figure is read back from the database), and numbers compared by value,
    /// because the test database (SQLite) and production (Postgres) store decimals with different scales.
    /// </summary>
    private static async Task<string> ListJsonAsync(ZayraDbContext seeded, Counter counter, Guid? scopedToCompany = null)
    {
        await using var fresh = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(seeded.Database.GetDbConnection()).AddInterceptors(counter).Options);
        counter.Reset();
        var result = await Controller(fresh, scopedToCompany).List(CancellationToken.None);
        return Normalize(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value));
    }

    internal static string Normalize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(doc.RootElement, writer);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        static void Write(JsonElement e, Utf8JsonWriter w)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: w.WriteStartObject(); foreach (var p in e.EnumerateObject()) { w.WritePropertyName(p.Name); Write(p.Value, w); } w.WriteEndObject(); break;
                case JsonValueKind.Array: w.WriteStartArray(); foreach (var x in e.EnumerateArray()) Write(x, w); w.WriteEndArray(); break;
                case JsonValueKind.Number: w.WriteRawValue(e.GetDecimal().ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture)); break;
                default: e.WriteTo(w); break;
            }
        }
    }

    [Fact]
    public async Task TheList_UsesAFixedNumberOfQueries_WhetherOneEmployeeIsPendingOrTwenty()
    {
        var (db1, counter1, c1) = await OpenAsync();
        await using var _1 = c1; await using var _d1 = db1;
        await SeedPendingAsync(db1, 1);
        var one = await ListJsonAsync(db1, counter1);
        var queriesForOne = counter1.Count;

        var (db20, counter20, c20) = await OpenAsync();
        await using var _2 = c20; await using var _d20 = db20;
        await SeedPendingAsync(db20, 20);
        var twenty = await ListJsonAsync(db20, counter20);
        var queriesForTwenty = counter20.Count;

        output.WriteLine($"QUERIES for 1 employee: {queriesForOne}; for 20 employees: {queriesForTwenty}");
        Assert.Contains("\"count\":1", one);
        Assert.Contains("\"count\":20", twenty);
        Assert.Equal(queriesForOne, queriesForTwenty);
        Assert.InRange(queriesForTwenty, 1, 12);
    }

    [Fact]
    public async Task TheList_Response_IsUnchanged_ForEveryBranch_ForAGroupCaller()
    {
        var (db, counter, connection) = await OpenAsync();
        await using var _c = connection; await using var _d = db;
        await SeedScenarioAsync(db);

        Assert.Equal(PayApprovalsListSnapshots.Group, await ListJsonAsync(db, counter));
    }

    [Fact]
    public async Task TheList_Response_IsUnchanged_ForACallerScopedToOneCompany()
    {
        var (db, counter, connection) = await OpenAsync();
        await using var _c = connection; await using var _d = db;
        await SeedScenarioAsync(db);

        Assert.Equal(PayApprovalsListSnapshots.CompanyA, await ListJsonAsync(db, counter, CompanyA));
    }
}

internal static class PayApprovalsListSnapshots
{
    // Captured from the per-employee implementation before it was replaced, on the dataset seeded above.
    public const string Group = """
    {"items":[{"employeeId":1,"employeeCode":"E-001","name":"Layla Al-Harbi","companyId":"22222222-0000-0000-0000-00000000000a","companyName":"Acme Arabia","designation":"Senior Analyst","currency":"SAR","gross":10500,"ibanLast4":"7519","enteredBy":{"Name":"Huda HR Officer","AtUtc":"2026-10-26T09:15:00"},"lastChangedAtUtc":"2026-10-26T09:15:00","status":"pending"},{"employeeId":2,"employeeCode":"E-002","name":"Omar Al-Qahtani","companyId":"22222222-0000-0000-0000-00000000000a","companyName":"Acme Arabia","designation":"Analyst","currency":"SAR","gross":6000,"ibanLast4":"7519","enteredBy":{"Name":"Huda HR Officer","AtUtc":"2026-10-26T09:20:00"},"lastChangedAtUtc":"2026-10-26T09:20:00","status":"rejected"},{"employeeId":4,"employeeCode":"E-004","name":"Other Company Hire","companyId":"22222222-0000-0000-0000-00000000000b","companyName":"Beta Holdings","designation":null,"currency":"SAR","gross":8500,"ibanLast4":"3210","enteredBy":null,"lastChangedAtUtc":null,"status":"pending"},{"employeeId":8,"employeeCode":"E-008","name":"Drifted Hire","companyId":"22222222-0000-0000-0000-00000000000a","companyName":"Acme Arabia","designation":null,"currency":"SAR","gross":5500,"ibanLast4":"3344","enteredBy":null,"lastChangedAtUtc":null,"status":"pending"}],"count":4,"hasMore":false,"requiresOtherReviewerCount":1}
    """;
    public const string CompanyA = """
    {"items":[{"employeeId":1,"employeeCode":"E-001","name":"Layla Al-Harbi","companyId":"22222222-0000-0000-0000-00000000000a","companyName":"Acme Arabia","designation":"Senior Analyst","currency":"SAR","gross":10500,"ibanLast4":"7519","enteredBy":{"Name":"Huda HR Officer","AtUtc":"2026-10-26T09:15:00"},"lastChangedAtUtc":"2026-10-26T09:15:00","status":"pending"},{"employeeId":2,"employeeCode":"E-002","name":"Omar Al-Qahtani","companyId":"22222222-0000-0000-0000-00000000000a","companyName":"Acme Arabia","designation":"Analyst","currency":"SAR","gross":6000,"ibanLast4":"7519","enteredBy":{"Name":"Huda HR Officer","AtUtc":"2026-10-26T09:20:00"},"lastChangedAtUtc":"2026-10-26T09:20:00","status":"rejected"},{"employeeId":8,"employeeCode":"E-008","name":"Drifted Hire","companyId":"22222222-0000-0000-0000-00000000000a","companyName":"Acme Arabia","designation":null,"currency":"SAR","gross":5500,"ibanLast4":"3344","enteredBy":null,"lastChangedAtUtc":null,"status":"pending"}],"count":3,"hasMore":false,"requiresOtherReviewerCount":1}
    """;
}
