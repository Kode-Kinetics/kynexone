using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Guards the required-document coverage KPI after it moved from in-memory aggregation to a
/// SQL-side correlated COUNT(DISTINCT ...).
///
/// These tests run on SQLite rather than the InMemory provider ON PURPOSE. InMemory does not
/// translate LINQ to SQL, so it happily client-evaluates anything and would keep passing even if
/// the query became untranslatable against PostgreSQL. SQLite is a real relational provider, so
/// an untranslatable expression throws here the same way it would in production.
/// </summary>
public class DashboardKpiDocumentCoverageTests
{
    private static readonly string[] Required = ["Iqama", "Work Permit", "National ID", "Passport"];

    private static DashboardController MakeCtrl(ZayraDbContext db, Guid tenantId, IDataScopeService? scope = null)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new Claim[]
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        }, "test"));
        var ctrl = new DashboardController(
            db,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            scope ?? new _KpiUnrestricted());
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal },
        };
        return ctrl;
    }

    private static Employee NewEmployee(Guid tenantId, string code, string status = "Active") => new()
    {
        TenantId = tenantId,
        EmployeeCode = code,
        FullName = $"Employee {code}",
        Status = status,
        JoiningDate = DateTime.UtcNow.AddYears(-1),
    };

    private static EmployeeDocument NewDoc(Guid tenantId, int employeeId, string type, bool deleted = false) => new()
    {
        TenantId = tenantId,
        EmployeeId = employeeId,
        DocumentType = type,
        FileName = $"{type}.pdf",
        IsDeleted = deleted,
    };

    private static DashboardKpisDto ExtractKpis(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<DashboardKpisDto>(ok.Value);
    }

    /// <summary>Opens a live SQLite connection + created schema. Caller disposes both.</summary>
    private static async Task<(SqliteConnection conn, ZayraDbContext db)> NewRelationalDbAsync()
    {
        var conn = new SqliteConnection($"Data Source=kpi-docs-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=10");
        await conn.OpenAsync();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        return (conn, db);
    }

    [Fact]
    public async Task MissingDocuments_TranslatesToSql_AndCountsOnlyEmployeesBelowFullCoverage()
    {
        var (conn, db) = await NewRelationalDbAsync();
        await using var _c = conn;
        await using var _d = db;

        var tenantId = Guid.NewGuid();
        var complete = NewEmployee(tenantId, "E-COMPLETE");
        var partial = NewEmployee(tenantId, "E-PARTIAL");
        var none = NewEmployee(tenantId, "E-NONE");
        db.Employees.AddRange(complete, partial, none);
        await db.SaveChangesAsync();

        // complete: holds all four required documents -> NOT missing
        foreach (var type in Required) db.EmployeeDocuments.Add(NewDoc(tenantId, complete.Id, type));
        // partial: holds three of four -> missing
        db.EmployeeDocuments.Add(NewDoc(tenantId, partial.Id, "Iqama"));
        db.EmployeeDocuments.Add(NewDoc(tenantId, partial.Id, "Work Permit"));
        db.EmployeeDocuments.Add(NewDoc(tenantId, partial.Id, "National ID"));
        // none: holds only an irrelevant document -> missing
        db.EmployeeDocuments.Add(NewDoc(tenantId, none.Id, "Driving Licence"));
        await db.SaveChangesAsync();

        var kpis = ExtractKpis(await MakeCtrl(db, tenantId).Kpis(CancellationToken.None));

        kpis.MissingDocuments.Should().Be(2, "partial and none lack at least one required document");
    }

    [Fact]
    public async Task MissingDocuments_MatchesDocumentTypeCaseInsensitively()
    {
        var (conn, db) = await NewRelationalDbAsync();
        await using var _c = conn;
        await using var _d = db;

        var tenantId = Guid.NewGuid();
        var employee = NewEmployee(tenantId, "E-CASE");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        // Same four required documents, stored in mixed / upper / lower case. The previous
        // in-memory implementation used an OrdinalIgnoreCase HashSet; that behaviour must survive
        // the move into SQL, where comparison is case-SENSITIVE by default.
        db.EmployeeDocuments.Add(NewDoc(tenantId, employee.Id, "iqama"));
        db.EmployeeDocuments.Add(NewDoc(tenantId, employee.Id, "WORK PERMIT"));
        db.EmployeeDocuments.Add(NewDoc(tenantId, employee.Id, "National id"));
        db.EmployeeDocuments.Add(NewDoc(tenantId, employee.Id, "pAsSpOrT"));
        await db.SaveChangesAsync();

        var kpis = ExtractKpis(await MakeCtrl(db, tenantId).Kpis(CancellationToken.None));

        kpis.MissingDocuments.Should().Be(0, "case differences must not count as a missing document");
    }

    [Fact]
    public async Task MissingDocuments_IgnoresSoftDeletedDocumentsAndDuplicates()
    {
        var (conn, db) = await NewRelationalDbAsync();
        await using var _c = conn;
        await using var _d = db;

        var tenantId = Guid.NewGuid();
        var employee = NewEmployee(tenantId, "E-DEL");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        db.EmployeeDocuments.Add(NewDoc(tenantId, employee.Id, "Iqama"));
        db.EmployeeDocuments.Add(NewDoc(tenantId, employee.Id, "Iqama"));       // duplicate must not inflate coverage
        db.EmployeeDocuments.Add(NewDoc(tenantId, employee.Id, "Work Permit"));
        db.EmployeeDocuments.Add(NewDoc(tenantId, employee.Id, "National ID"));
        db.EmployeeDocuments.Add(NewDoc(tenantId, employee.Id, "Passport", deleted: true)); // soft-deleted -> not coverage
        await db.SaveChangesAsync();

        var kpis = ExtractKpis(await MakeCtrl(db, tenantId).Kpis(CancellationToken.None));

        kpis.MissingDocuments.Should().Be(1,
            "a duplicated Iqama does not substitute for the soft-deleted Passport");
    }

    [Fact]
    public async Task MissingDocuments_IsTenantIsolatedAndIgnoresInactiveEmployees()
    {
        var (conn, db) = await NewRelationalDbAsync();
        await using var _c = conn;
        await using var _d = db;

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var aActive = NewEmployee(tenantA, "A-1");
        var aInactive = NewEmployee(tenantA, "A-2", status: "Inactive"); // not Active -> excluded
        var bActive = NewEmployee(tenantB, "B-1");                        // other tenant -> excluded
        db.Employees.AddRange(aActive, aInactive, bActive);
        await db.SaveChangesAsync();

        var kpis = ExtractKpis(await MakeCtrl(db, tenantA).Kpis(CancellationToken.None));

        kpis.MissingDocuments.Should().Be(1, "only tenant A's single active employee is in scope");
    }

    [Fact]
    public async Task MissingDocuments_RespectsRestrictedDataScope()
    {
        var (conn, db) = await NewRelationalDbAsync();
        await using var _c = conn;
        await using var _d = db;

        var tenantId = Guid.NewGuid();
        var inScope = NewEmployee(tenantId, "S-IN");
        var outOfScope = NewEmployee(tenantId, "S-OUT");
        db.Employees.AddRange(inScope, outOfScope);
        await db.SaveChangesAsync();

        var kpis = ExtractKpis(await MakeCtrl(db, tenantId, new _KpiRestricted(new[] { inScope.Id }))
            .Kpis(CancellationToken.None));

        kpis.MissingDocuments.Should().Be(1, "the out-of-scope employee must not be counted");
    }
}

file sealed class _KpiUnrestricted : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _KpiRestricted : IDataScopeService
{
    private readonly IReadOnlyCollection<int> _allowed;
    public _KpiRestricted(IReadOnlyCollection<int> allowed) => _allowed = allowed;
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Department, AllowedEmployeeIds = _allowed });
}
