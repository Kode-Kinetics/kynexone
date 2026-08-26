using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Compliance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public sealed class EmployeeIdentityControllerTests
{
    [Fact]
    public async Task ContractCreate_StampsCanonicalEmployeeIdentityAndName()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = Employee(tenantId, "EMP-CONTRACT");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var controller = Controller(db, tenantId);

        var result = await controller.Create(new CreateContractRequest(
            employee.PublicId, "Untrusted client name", null, "Employment",
            new DateOnly(2026, 1, 1), null, 12_000m, "AED", null, null, "en"), default);

        result.Should().BeOfType<CreatedAtActionResult>();
        var contract = await db.EmployeeContracts.SingleAsync();
        contract.EmployeeId.Should().Be(employee.PublicId);
        contract.EmployeeName.Should().Be(employee.FullName);
        contract.CompanyId.Should().Be(employee.CompanyId);
    }

    [Fact]
    public async Task ContractCreate_RejectsEmployeeFromAnotherTenant()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var employee = Employee(tenantA, "EMP-FOREIGN");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var result = await Controller(db, tenantB).Create(new CreateContractRequest(
            employee.PublicId, employee.FullName, null, "Employment",
            new DateOnly(2026, 1, 1), null, 12_000m, "AED", null, null, "en"), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.EmployeeContracts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task IdentityIntegrityReport_CountsOnlyUnprovenRowsInCurrentTenant()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var employee = Employee(tenantId, "EMP-REPORT");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.AddRange(
            new EmployeeLoan
            {
                TenantId = tenantId, EmployeeId = employee.PublicId, EmployeeIntId = employee.Id,
                EmployeeName = employee.FullName, LoanNumber = "LN-VALID", LoanTypeId = Guid.NewGuid(),
            },
            new EmployeeContract
            {
                TenantId = tenantId, EmployeeId = Guid.NewGuid(), EmployeeName = "Unresolved",
                ContractNumber = "CON-UNRESOLVED",
            },
            new EmployeeContract
            {
                TenantId = otherTenantId, EmployeeId = Guid.NewGuid(), EmployeeName = "Other tenant",
                ContractNumber = "CON-OTHER-TENANT",
            });
        await db.SaveChangesAsync();

        var controller = ReportsController(db, tenantId);
        var ok = (await controller.EmployeeIdentityIntegrity(default)).Should().BeOfType<OkObjectResult>().Subject;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

        json.RootElement.GetProperty("status").GetString().Should().Be("RequiresReconciliation");
        json.RootElement.GetProperty("totalUnresolved").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("counts").GetProperty("contracts").GetInt32().Should().Be(1);
    }

    private static ContractsController Controller(ZayraDbContext db, Guid tenantId) => new(db)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                }, "test")),
            },
        },
    };

    private static ComplianceReportsController ReportsController(ZayraDbContext db, Guid tenantId) => new(db)
    {
        ControllerContext = Controller(db, tenantId).ControllerContext,
    };

    private static Employee Employee(Guid tenantId, string code) => new()
    {
        TenantId = tenantId,
        CompanyId = Guid.NewGuid(),
        EmployeeCode = code,
        FullName = "Canonical Employee",
        EnglishName = "Canonical Employee",
        Status = EmployeeStatuses.Active,
    };

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
