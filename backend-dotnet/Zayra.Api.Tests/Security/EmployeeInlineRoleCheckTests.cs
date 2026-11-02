using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// PEO-01. Role-name shortcuts (<c>User.IsInRole("Admin")</c>) ignore a per-user Deny override, because the
/// role claim survives while the permission claim is withheld. Every in-method decision in the People
/// controllers must ask for the permission instead.
/// </summary>
public sealed class EmployeeInlineRoleCheckTests
{
    private static ZayraDbContext CreateDb() => PeopleAuditFixtures.CreateDb();
    private static Employee SeedEmployee(ZayraDbContext db, Guid t, string c, string n) => PeopleAuditFixtures.SeedEmployee(db, t, c, n);
    private static EmployeesController CreateController(ZayraDbContext db, Guid t, string role, string[]? p = null, int? e = null) => PeopleAuditFixtures.CreateController(db, t, role, p, e);

    [Fact]
    public async Task InlineRoleChecks_HonourPerUserDeny()
    {
        var tenantId = Guid.NewGuid();
        using var db = CreateDb();
        var subject = SeedEmployee(db, tenantId, "E1", "Subject");
        var currentManager = SeedEmployee(db, tenantId, "E2", "Manager");
        var transferId = Guid.NewGuid();
        db.EmployeeTransferRequests.Add(new EmployeeTransferRequest
        {
            Id = transferId, TenantId = tenantId, EmployeeId = subject.Id,
            CurrentManagerEmployeeId = currentManager.Id, Status = "PendingCurrentManager",
        });
        db.SaveChanges();

        // Admin role claim, but employees.approve is Denied for this user (no permission claim).
        var denied = CreateController(db, tenantId, "Admin", ["manager.approve", "employees.read"]);
        var deniedResult = await denied.ApproveCurrentManager(transferId, CancellationToken.None);
        deniedResult.Should().BeOfType<ForbidResult>("the Admin role name must not bypass the expected-manager check once employees.approve is denied");

        var allowed = CreateController(db, tenantId, "Admin", ["manager.approve", "employees.approve", "employees.read"]);
        var allowedResult = await allowed.ApproveCurrentManager(transferId, CancellationToken.None);
        allowedResult.Should().NotBeOfType<ForbidResult>();
    }

    [Fact]
    public void PeopleControllers_DoNotDecideInline_ByRoleName()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? controllers = null;
        while (dir is not null && controllers is null)
        {
            var candidate = Path.Combine(dir.FullName, "Zayra.Api", "Controllers");
            if (Directory.Exists(candidate)) controllers = candidate;
            dir = dir.Parent;
        }
        controllers.Should().NotBeNull("the ratchet must be able to find the controller sources");

        var offenders = new List<string>();
        foreach (var file in new[] { "EmployeesController.cs", "EmployeeSelfServiceController.cs" })
        {
            var lines = File.ReadAllLines(Path.Combine(controllers!, file));
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("//")) continue;
                if (line.Contains("IsInRole(\"")) offenders.Add($"{file}:{i + 1}: {line}");
            }
        }
        offenders.Should().BeEmpty("role-name shortcuts ignore per-user Deny overrides; use User.HasPermission(...)");
    }
}
