using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// Transfer decisions that arrive together. Every transition used to read the status, check it, and write: HR could apply the
/// department change while a rejection (or a withdrawal) passed the same check, and the transfer was then overwritten as
/// Rejected or Withdrawn although the employee had already moved. Each contender runs on its own context and connection, released
/// from one barrier, over several freshly seeded transfers: a round can come out serial by accident, not every round.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeTransferDecisionConcurrencyPostgresTests(PostgresFixture fixture)
{
    private const int Rounds = 10;
    private static readonly string[] HrPermissions = ["manager.approve", "employees.approve", "employees.read"];

    private sealed record Seeded(Guid Tenant, Guid TransferId, int EmployeeId, Guid FromDepartment, Guid ToDepartment);

    private async Task<Guid> SeedTenantAsync()
    {
        await using var db = fixture.CreateDb();
        return await PostgresFixture.SeedMinimalTenant(db);
    }

    private async Task<Seeded> SeedTransferAsync(Guid tenant, string status)
    {
        await using var db = fixture.CreateDb();
        var company = new Company { TenantId = tenant, LegalNameEn = $"Co {Guid.NewGuid():N}"[..12], RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var from = new Department { TenantId = tenant, CompanyId = company.Id, Code = $"O{Guid.NewGuid():N}"[..8], NameEn = $"Operations {Guid.NewGuid():N}"[..14], IsActive = true };
        var to = new Department { TenantId = tenant, CompanyId = company.Id, Code = $"F{Guid.NewGuid():N}"[..8], NameEn = $"Finance {Guid.NewGuid():N}"[..14], IsActive = true };
        db.AddRange(from, to);
        await db.SaveChangesAsync();
        var employee = new Employee
        {
            TenantId = tenant, CompanyId = company.Id, EmployeeCode = $"T-{Guid.NewGuid():N}"[..12], FullName = "Transferee", Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-1), DepartmentId = from.Id, Department = from.NameEn,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var transfer = new EmployeeTransferRequest
        {
            TenantId = tenant, EmployeeId = employee.Id, Status = status, NewDepartment = to.NameEn, NewDepartmentId = to.Id,
            RequestedByUserId = Guid.NewGuid(), EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow), Reason = "Restructure",
        };
        db.EmployeeTransferRequests.Add(transfer);
        await db.SaveChangesAsync();
        return new Seeded(tenant, transfer.Id, employee.Id, from.Id, to.Id);
    }

    private static EmployeesController Controller(ZayraDbContext db, Guid tenant)
    {
        var controller = new EmployeesController(db, new Pbkdf2PasswordHasher(), new AuditService(db), new NullDocumentStorage(),
            TestNotifications.For(db), new PeopleAuditFixtures.StubHijriDateService(), new DataScopeService(db), new PeopleAuditFixtures.StubLetterService());
        var claims = new List<Claim>
        {
            new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "Admin"), new("is_group_scope", "true"),
        };
        claims.AddRange(HrPermissions.Select(p => new Claim("permission", p)));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };
        return controller;
    }

    private Task<IActionResult> ApproveHrAsync(Seeded s) => RunAsync(s.Tenant, c => c.ApproveHrTransfer(s.TransferId, null!, default));
    private Task<IActionResult> RejectAsync(Seeded s) => RunAsync(s.Tenant, c => c.RejectTransfer(s.TransferId, new TransferDecisionRequest("Not now"), default));
    private Task<IActionResult> WithdrawAsync(Seeded s) => RunAsync(s.Tenant, c => c.WithdrawTransfer(s.TransferId, new TransferDecisionRequest("Plans changed"), default));

    private async Task<IActionResult> RunAsync(Guid tenant, Func<EmployeesController, Task<IActionResult>> action)
    {
        await using var db = fixture.CreateDb();
        return await action(Controller(db, tenant));
    }

    private static async Task<IActionResult?[]> RaceAsync(Func<Task<IActionResult>> first, Func<Task<IActionResult>> second)
    {
        var ready = new SemaphoreSlim(0, 2);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IActionResult?> Run(Func<Task<IActionResult>> contender)
        {
            await Task.Yield();
            ready.Release();
            await go.Task;
            try { return await contender(); }
            catch { return null; }
        }
        var a = Task.Run(() => Run(first));
        var b = Task.Run(() => Run(second));
        await ready.WaitAsync();
        await ready.WaitAsync();
        go.SetResult();
        return await Task.WhenAll(a, b);
    }

    private async Task<(string Status, Guid? DepartmentId, List<string> Events)> ReadAsync(Seeded s)
    {
        await using var db = fixture.CreateDb();
        var status = await db.EmployeeTransferRequests.AsNoTracking().Where(x => x.Id == s.TransferId).Select(x => x.Status).SingleAsync();
        var department = await db.Employees.AsNoTracking().Where(x => x.Id == s.EmployeeId).Select(x => x.DepartmentId).SingleAsync();
        var events = await db.EmployeeHistories.AsNoTracking()
            .Where(h => h.EmployeeId == s.EmployeeId && (h.EventType == "TransferApproved" || h.EventType == "TransferRejected" || h.EventType == "TransferWithdrawn"))
            .Select(h => h.EventType).ToListAsync();
        return (status, department, events);
    }

    [Fact]
    public async Task ApproveHr_Alone_AppliesTheMove_AndClosesTheTransfer()
    {
        var s = await SeedTransferAsync(await SeedTenantAsync(), "PendingHrApproval");

        (await ApproveHrAsync(s)).Should().BeOfType<OkObjectResult>();

        var after = await ReadAsync(s);
        after.Status.Should().Be("ApprovedApplied");
        after.DepartmentId.Should().Be(s.ToDepartment);
        after.Events.Should().Equal("TransferApproved");
    }

    [Fact]
    public async Task ASimultaneousHrApprovalAndRejection_NeverBothTakeEffect()
    {
        var tenant = await SeedTenantAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var s = await SeedTransferAsync(tenant, "PendingHrApproval");

            var results = await RaceAsync(() => ApproveHrAsync(s), () => RejectAsync(s));

            results.Should().NotContainNulls($"round {round}: no contender may throw");
            results.OfType<OkObjectResult>().Should().HaveCount(1, $"round {round}: exactly one decision takes effect");
            results.Where(r => r is not OkObjectResult).Should().ContainSingle().Which.Should().Match(r => r is ConflictObjectResult || r is BadRequestObjectResult, $"round {round}: the loser is told it lost");

            var after = await ReadAsync(s);
            if (after.Status == "ApprovedApplied")
            {
                after.DepartmentId.Should().Be(s.ToDepartment, $"round {round}: applied means the employee moved");
                after.Events.Should().Equal(["TransferApproved"], $"round {round}: no rejection is recorded over an applied move");
            }
            else
            {
                after.Status.Should().Be("Rejected", $"round {round}");
                after.DepartmentId.Should().Be(s.FromDepartment, $"round {round}: rejected means the employee did not move");
                after.Events.Should().Equal(["TransferRejected"], $"round {round}");
            }
        }
    }

    [Fact]
    public async Task ASimultaneousHrApprovalAndWithdrawal_NeverBothTakeEffect()
    {
        var tenant = await SeedTenantAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var s = await SeedTransferAsync(tenant, "PendingHrApproval");

            var results = await RaceAsync(() => ApproveHrAsync(s), () => WithdrawAsync(s));

            results.Should().NotContainNulls($"round {round}");
            results.OfType<OkObjectResult>().Should().HaveCount(1, $"round {round}");

            var after = await ReadAsync(s);
            if (after.Status == "ApprovedApplied")
            {
                after.DepartmentId.Should().Be(s.ToDepartment, $"round {round}");
                after.Events.Should().Equal(["TransferApproved"], $"round {round}");
            }
            else
            {
                after.Status.Should().Be("Withdrawn", $"round {round}");
                after.DepartmentId.Should().Be(s.FromDepartment, $"round {round}");
                after.Events.Should().Equal(["TransferWithdrawn"], $"round {round}");
            }
        }
    }

    [Fact]
    public async Task ASimultaneousRejectionAndWithdrawal_RecordExactlyOneOutcome()
    {
        var tenant = await SeedTenantAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var s = await SeedTransferAsync(tenant, "PendingNewManager");

            var results = await RaceAsync(() => RejectAsync(s), () => WithdrawAsync(s));

            results.Should().NotContainNulls($"round {round}");
            results.OfType<OkObjectResult>().Should().HaveCount(1, $"round {round}");
            results.OfType<ConflictObjectResult>().Should().HaveCount(1, $"round {round}");

            var after = await ReadAsync(s);
            after.DepartmentId.Should().Be(s.FromDepartment, $"round {round}");
            after.Events.Should().HaveCount(1, $"round {round}: one outcome in the history, not two");
            (after.Status, after.Events[0]).Should().BeOneOf(("Rejected", "TransferRejected"), ("Withdrawn", "TransferWithdrawn"));
        }
    }

    [Fact]
    public async Task TwoSimultaneousCurrentManagerApprovals_AdvanceTheTransferOnce()
    {
        var tenant = await SeedTenantAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var s = await SeedTransferAsync(tenant, "PendingCurrentManager");

            var results = await RaceAsync(
                () => RunAsync(tenant, c => c.ApproveCurrentManager(s.TransferId, default)),
                () => RunAsync(tenant, c => c.ApproveCurrentManager(s.TransferId, default)));

            results.Should().NotContainNulls($"round {round}");
            results.OfType<OkObjectResult>().Should().HaveCount(1, $"round {round}: the step is passed once");
            (await ReadAsync(s)).Status.Should().Be("PendingNewManager", $"round {round}");
        }
    }
}
