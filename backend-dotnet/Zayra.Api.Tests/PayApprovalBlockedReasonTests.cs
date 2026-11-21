using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Pay approvals: "why is this blocked for me". The queue hides the packages the caller may not approve and counts them; it now also
/// names each one with the reason, taken from the SAME rule the approve endpoint refuses with (CompensationApprovalGate), never a second copy.
/// </summary>
public class PayApprovalBlockedReasonTests
{
    private static readonly Guid Tenant = PayApprovalsListQueryCountTests.Tenant;
    private static readonly Guid Company = PayApprovalsListQueryCountTests.CompanyA;
    private static readonly Guid Caller = Guid.Parse("33333333-0000-0000-0000-000000000001");
    private static readonly Guid Huda = Guid.Parse("33333333-0000-0000-0000-000000000002");
    private static readonly Guid Omar = Guid.Parse("33333333-0000-0000-0000-000000000003");

    private static async Task<(ZayraDbContext Db, SqliteConnection Connection)> OpenAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await PayApprovalsListQueryCountTests.SeedBaseAsync(db);
        return (db, connection);
    }

    private static async Task<Employee> HireWithPendingPackageAsync(ZayraDbContext db, string code, Guid createdBy, Guid? userAccount = null)
    {
        var e = new Employee
        {
            TenantId = Tenant, CompanyId = Company, EmployeeCode = code, FullName = code, EnglishName = code, Status = "Draft",
            JoiningDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), CreatedBy = createdBy, UserAccountId = userAccount,
            WorkEmail = $"{code}@t.test", Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        db.SalaryChangeRequests.Add(new SalaryChangeRequest
        {
            TenantId = Tenant, CompanyId = Company, Source = "NewHire", EmployeeId = e.Id, EmployeeCode = code, EmployeeName = code, SalaryStructureId = Guid.NewGuid(),
            BasicSalary = 8000m, Currency = "SAR", Status = SalaryChangeRequest.Pending, RequestedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return e;
    }

    private static async Task<JsonElement> ListAsync(ZayraDbContext seeded, SqliteConnection connection)
    {
        await using var fresh = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(connection).Options);
        var result = await PayApprovalsListQueryCountTests.Controller(fresh).List(CancellationToken.None);
        return JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
    }

    [Fact]
    public async Task EachBlockedPackage_NamesItsReason_AndAnApprovablePackageHasNone()
    {
        var (db, connection) = await OpenAsync();
        await using var _c = connection; await using var _d = db;
        var mine = await HireWithPendingPackageAsync(db, "B-MINE", createdBy: Caller);                       // the caller entered it
        var me = await HireWithPendingPackageAsync(db, "B-ME", createdBy: Huda, userAccount: Caller);        // the caller is the employee
        var handed = await HireWithPendingPackageAsync(db, "B-CODE", createdBy: Huda, userAccount: Omar);    // the caller gave the employee their sign-in code
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = Tenant, UserId = Omar, Action = WelcomeCodeRedeemer.RedeemedAction, EntityName = "User", EntityId = Omar.ToString(),
            CreatedAtUtc = DateTime.UtcNow.AddDays(-1), Metadata = JsonSerializer.Serialize(new { disclosed = true, issuedBy = Caller }),
        });
        var free = await HireWithPendingPackageAsync(db, "B-FREE", createdBy: Huda);                         // nobody bars the caller
        await db.SaveChangesAsync();

        var body = await ListAsync(db, connection);

        var listed = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["B-FREE"], listed.Select(i => i.GetProperty("employeeCode").GetString()!).ToList());
        Assert.Equal(JsonValueKind.Null, listed.Single().GetProperty("blockedForCaller").ValueKind);

        var blocked = body.GetProperty("blockedItems").EnumerateArray()
            .ToDictionary(i => i.GetProperty("employeeCode").GetString()!, i => i.GetProperty("blockedForCaller").GetString());
        Assert.Equal(3, blocked.Count);
        Assert.Equal("EnteredByYou", blocked["B-MINE"]);
        Assert.Equal("YouAreTheEmployee", blocked["B-ME"]);
        Assert.Equal("YouIssuedSignInCode", blocked["B-CODE"]);
        Assert.Equal(3, body.GetProperty("requiresOtherReviewerCount").GetInt32());

        // The approve endpoint's own rule (the gate) gives the same answer for every one of them.
        foreach (var (employee, expected) in new[] { (mine, "EnteredByYou"), (me, "YouAreTheEmployee"), (handed, "YouIssuedSignInCode") })
        {
            Assert.Equal(expected, await CompensationApprovalGate.BlockedReasonAsync(db, Tenant, employee, Caller, CancellationToken.None));
            Assert.True(await CompensationApprovalGate.IsBarredAsync(db, Tenant, employee, Caller, CancellationToken.None));
        }
        Assert.Null(await CompensationApprovalGate.BlockedReasonAsync(db, Tenant, free, Caller, CancellationToken.None));
        Assert.False(await CompensationApprovalGate.IsBarredAsync(db, Tenant, free, Caller, CancellationToken.None));
    }
}
