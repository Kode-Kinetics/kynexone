using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;
namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeBankApprovalPostgresTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("Approve", "bankIban")]
    [InlineData("Approve", "bankName")]
    [InlineData("Reject", "bankIban")]
    public async Task SubmittedBankChange_SeparateApprover_PersistsOnlyApprovedProfileField(string decision, string field)
    {
        Guid tenant; Guid approvalId; Guid requester = Guid.NewGuid(); int employeeId;
        var value = field == "bankIban" ? "SA5380000000006080101001" : "Approved Bank";
        await using (var db = fixture.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(db);
            var e = new Employee { TenantId = tenant, EmployeeCode = "BANK-PG", FullName = "Synthetic Bank PG", Status = "Active", JoiningDate = DateTime.UtcNow.Date, BankName = "Stale scalar", BankIban = "" };
            db.Employees.Add(e); await db.SaveChangesAsync(); employeeId = e.Id;
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenant, EmployeeId = e.Id, BankName = "Original Bank", Iban = "SA0380000000608010167519", SalaryCurrency = "SAR", MolId = "KEEP-MOL", AccountNumber = "KEEP-ACCOUNT", WpsEligible = false, EosbEligible = false });
            await db.SaveChangesAsync();
            var ctrl = HrmHierarchyTests.BuildImportControllerInternal(db, tenant);
            ctrl.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, requester.ToString()), new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "employees.read"), new Claim("permission", "employees.write"), new Claim("permission", "employees.sensitive") }, "Test"));
            Assert.IsType<AcceptedResult>(await ctrl.UpdateEmployee(e.Id, new EmployeeUpdateRequest(DateOnly.FromDateTime(DateTime.UtcNow.Date), new() { [field] = JsonSerializer.SerializeToElement(value) }), CancellationToken.None));
            approvalId = (await db.ApprovalRequests.SingleAsync(x => x.TenantId == tenant)).Id;
        }
        await using (var blocked = fixture.CreateDb())
        {
            var service = new ApprovalWorkflowService(blocked, new AuditService(blocked));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideAsync(tenant, approvalId, new ApprovalDecisionRequest("Approve", "self"), new RequestContext("127.0.0.1", "test", requester, tenant, ["HR Manager"], []), CancellationToken.None));
        }
        await using (var approved = fixture.CreateDb())
        {
            var p = await approved.EmployeePayrollProfiles.SingleAsync(x => x.TenantId == tenant);
            Assert.Equal("Original Bank", p.BankName); Assert.Equal("SA0380000000608010167519", p.Iban);
            Assert.Equal("Pending", (await approved.ApprovalRequests.SingleAsync(x => x.Id == approvalId)).Status);
            var service = new ApprovalWorkflowService(approved, new AuditService(approved));
            await service.DecideAsync(tenant, approvalId, new ApprovalDecisionRequest(decision, "Independent synthetic review"), new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenant, ["HR Manager"], []), CancellationToken.None);
        }
        await using var verify = fixture.CreateDb();
        var profile = await verify.EmployeePayrollProfiles.SingleAsync(x => x.TenantId == tenant && x.EmployeeId == employeeId);
        Assert.Equal(decision == "Approve" && field == "bankName" ? value : "Original Bank", profile.BankName);
        Assert.Equal(decision == "Approve" && field == "bankIban" ? value : "SA0380000000608010167519", profile.Iban);
        Assert.Equal("SAR", profile.SalaryCurrency); Assert.Equal("KEEP-MOL", profile.MolId);
        Assert.Equal("KEEP-ACCOUNT", profile.AccountNumber); Assert.False(profile.WpsEligible); Assert.False(profile.EosbEligible);
        Assert.Equal(1, await verify.ApprovalDecisions.CountAsync(x => x.ApprovalRequestId == approvalId));
    }
}
