using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public sealed class MigrationImportBatchTests
{
    [Fact]
    public async Task DryRunBatch_StoresPayloadAndReconciliationWithoutWritingSetupData()
    {
        await using var harness = await SqliteHarness.CreateAsync();
        var tenantId = await SeedTenantAsync(harness.Db);
        await SeedIdentityAndOperationalDataAsync(harness.Db, tenantId);
        var controller = CreateController(harness.Db, GroupAdmin(tenantId));

        var result = await controller.CreateDryRunBatch(SampleRequest(), CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<MigrationImportBatchDto>().Subject;
        dto.Status.Should().Be("DryRunPassed");
        dto.ReceivedRows.Should().Be(3);
        dto.Reconciliation.SectionCounts["companies"].Should().Be(1);
        dto.Reconciliation.IdentityCounts["users"].Should().Be(1);
        dto.Reconciliation.IdentityCounts["roles"].Should().Be(1);
        dto.Reconciliation.IdentityCounts["userRoleAssignments"].Should().Be(1);
        dto.Reconciliation.OperationalCounts["employees"].Should().Be(1);
        dto.Reconciliation.OperationalCounts["payrollRuns"].Should().Be(1);

        (await harness.Db.Companies.CountAsync()).Should().Be(0, "dry-run batches must not mutate setup tables");
        var stored = await harness.Db.MigrationImportBatches.SingleAsync(x => x.Id == dto.Id);
        stored.PayloadJson.Should().Contain("Batch Legal");
        stored.PackageChecksum.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CommitBatch_ResumesDryRunAndIsIdempotent()
    {
        await using var harness = await SqliteHarness.CreateAsync();
        var tenantId = await SeedTenantAsync(harness.Db);
        var controller = CreateController(harness.Db, GroupAdmin(tenantId));

        var dryRun = await controller.CreateDryRunBatch(SampleRequest(), CancellationToken.None);
        var dryRunDto = ((OkObjectResult)dryRun.Result!).Value.Should().BeOfType<MigrationImportBatchDto>().Subject;

        var committed = await controller.CommitBatch(dryRunDto.Id, CancellationToken.None);
        var ok = committed.Result.Should().BeOfType<OkObjectResult>().Subject;
        var committedDto = ok.Value.Should().BeOfType<MigrationImportBatchDto>().Subject;
        committedDto.Status.Should().Be("Committed");
        committedDto.CurrentSection.Should().Be("committed");

        (await harness.Db.Companies.CountAsync(x => x.TenantId == tenantId && x.LegalNameEn == "Batch Legal")).Should().Be(1);
        (await harness.Db.Branches.CountAsync(x => x.TenantId == tenantId && x.Code == "HQ")).Should().Be(1);

        var second = await controller.CommitBatch(dryRunDto.Id, CancellationToken.None);
        second.Result.Should().BeOfType<OkObjectResult>();
        (await harness.Db.Companies.CountAsync(x => x.TenantId == tenantId && x.LegalNameEn == "Batch Legal")).Should().Be(1);
        (await harness.Db.MigrationImportBatches.SingleAsync(x => x.Id == dryRunDto.Id)).Status.Should().Be("Committed");
    }

    private static OrganizationStructureImportRequest SampleRequest() => new(
        CompaniesCsv: "LegalNameEn,TradeName,CountryCode,Jurisdiction,RegistrationNumber,TaxNumber,DefaultCurrency,IsActive\nBatch Legal,Batch,SA,SA-default,CR-B,TAX-B,SAR,true",
        BranchesCsv: "CompanyLegalName,Code,NameEn,CountryCode,City,IsHeadOffice,IsActive\nBatch Legal,HQ,Head Office,SA,Riyadh,true,true",
        CostCentersCsv: null,
        DepartmentsCsv: "CompanyLegalName,BranchCode,Code,NameEn,ParentDepartmentCode,CostCenterCode,ApprovedHeadcount,MonthlyBudgetAmount,IsActive\nBatch Legal,HQ,HR,Human Resources,,,10,120000,true",
        GradesCsv: null,
        GradePayComponentsCsv: null,
        DesignationsCsv: null);

    private static OrganizationStructureImportController CreateController(ZayraDbContext db, ClaimsPrincipal principal)
    {
        var controller = new OrganizationStructureImportController(db, new AuditService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    private static ClaimsPrincipal GroupAdmin(Guid tenantId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        }, "Test"));

    private static async Task<Guid> SeedTenantAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Batch Tenant", Slug = $"batch-{tenantId:N}" });
        await db.SaveChangesAsync();
        return tenantId;
    }

    private static async Task SeedIdentityAndOperationalDataAsync(ZayraDbContext db, Guid tenantId)
    {
        var user = new User
        {
            TenantId = tenantId,
            Email = "admin@example.com",
            NormalizedEmail = "ADMIN@EXAMPLE.COM",
            FullName = "Admin User",
            PasswordHash = "hash"
        };
        var role = new Role { TenantId = tenantId, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin" };
        db.Users.Add(user);
        db.Roles.Add(role);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        db.Employees.Add(new Employee { TenantId = tenantId, EmployeeCode = "E-001", FullName = "Employee One" });
        db.PayrollRuns.Add(new PayrollRun { TenantId = tenantId, Year = 2026, Month = 7, Status = "Draft" });
        db.LeaveRequests.Add(new LeaveRequest { TenantId = tenantId, EmployeeId = 1, EmployeeName = "Employee One", StartDate = new DateOnly(2026, 7, 1), EndDate = new DateOnly(2026, 7, 1), TotalDays = 1 });
        db.AttendanceRawEvents.Add(new AttendanceRawEvent { TenantId = tenantId, EmployeeCode = "E-001", PunchTimestampUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private sealed class SqliteHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ZayraDbContext Db { get; }

        private SqliteHarness(SqliteConnection connection, ZayraDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public static async Task<SqliteHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ZayraDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new ZayraDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new SqliteHarness(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
