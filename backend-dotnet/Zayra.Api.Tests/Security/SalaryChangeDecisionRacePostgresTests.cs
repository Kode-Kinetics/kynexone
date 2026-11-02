using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// M1 (real Postgres): an approve and a reject of the SAME pending salary change, decided at the same moment by two Finance users on two
/// connections. The row is read FOR UPDATE inside the decision transaction, so exactly one wins and the other finds it decided (409 not_pending):
/// the request can never read Rejected while the salary is live, nor Approved with no salary row. Needs a database; not run in the sandbox.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class SalaryChangeDecisionRacePostgresTests
{
    private readonly PostgresFixture _fx;
    public SalaryChangeDecisionRacePostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task ApproveAndReject_AtTheSameTime_NeverBothSucceed_AndTheRecordMatchesTheSalary()
    {
        for (var round = 0; round < 15; round++)
        {
            Guid tenantId; int employeeId; Guid changeId;
            await using (var seed = _fx.CreateDb())
            {
                tenantId = await PostgresFixture.SeedMinimalTenant(seed);
                var structure = new SalaryStructure { TenantId = tenantId, Code = "STD", Name = "Standard", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1) };
                var employee = new Employee { TenantId = tenantId, EmployeeCode = $"RACE-{round}", FullName = "Race Employee", Status = "Active", JoiningDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
                seed.AddRange(structure, employee);
                await seed.SaveChangesAsync();
                employeeId = employee.Id;
                var change = new SalaryChangeRequest
                {
                    TenantId = tenantId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode, EmployeeName = employee.FullName,
                    SalaryStructureId = structure.Id, BasicSalary = 9_000m, EffectiveDate = new DateOnly(2026, 1, 1), Currency = "SAR",
                    RequestedByUserId = Guid.NewGuid(), RequestedByName = "HR",
                };
                seed.SalaryChangeRequests.Add(change);
                await seed.SaveChangesAsync();
                changeId = change.Id;
            }

            await using var approverDb = _fx.CreateDb();
            await using var rejecterDb = _fx.CreateDb();
            var approve = SalaryApprovalKit.FinanceController(approverDb, tenantId).ApproveSalaryChange(changeId, null, CancellationToken.None);
            var reject = SalaryApprovalKit.FinanceController(rejecterDb, tenantId).RejectSalaryChange(changeId, null, CancellationToken.None);
            var results = await Task.WhenAll(approve, reject);

            results.Count(r => r is OkObjectResult).Should().Be(1, "exactly one decision wins; the other must be refused as already decided");
            results.Single(r => r is not OkObjectResult).Should().BeOfType<ConflictObjectResult>();

            await using var check = _fx.CreateDb();
            var stored = await check.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == changeId);
            var live = await check.EmployeeSalaryStructures.AsNoTracking().CountAsync(x => x.EmployeeId == employeeId && x.IsActive);
            if (stored.Status == SalaryChangeRequest.Approved) live.Should().Be(1, "an approved change has its salary row");
            else
            {
                stored.Status.Should().Be(SalaryChangeRequest.Rejected);
                live.Should().Be(0, "a rejected change must not leave a live salary");
            }
        }
    }
}
