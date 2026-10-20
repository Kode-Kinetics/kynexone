using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class EmployeeOrgFieldResolverTests
{
    [Fact]
    public async Task DepartmentChange_AppliesDepartmentCostCenterAndDefaultManager()
    {
        var tenantId = Guid.NewGuid();
        var costCenter = new CostCenter
        {
            TenantId = tenantId,
            Code = "QA-OPS-CC-18",
            Name = "QA Operations Cost Center",
        };
        var department = new Department
        {
            TenantId = tenantId,
            CostCenterId = costCenter.Id,
            Code = "QA-OPS",
            NameEn = "QA Operations",
            ManagerEmployeeId = 902,
        };
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.CostCenters.Add(costCenter);
        db.Departments.Add(department);
        await db.SaveChangesAsync();

        var employee = new Employee
        {
            TenantId = tenantId,
            Department = "QA-OPS",
            CostCenter = "OLD",
            ManagerEmployeeId = 1,
        };

        await EmployeeOrgFieldResolver.ResolveAppliedChangesAsync(
            db, tenantId, employee, ["department"], CancellationToken.None);

        employee.DepartmentId.Should().Be(department.Id);
        employee.Department.Should().Be("QA Operations");
        employee.CostCenterId.Should().Be(costCenter.Id);
        employee.CostCenter.Should().Be("QA-OPS-CC-18");
        employee.ManagerEmployeeId.Should().Be(902);
    }
}
