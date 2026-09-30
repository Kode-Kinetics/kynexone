using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;
using Zayra.Api.Controllers;
namespace Zayra.Api.Tests;

// Real PostgreSQL service/controller journey; deliberately NOT browser or bank acceptance evidence.
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class SaudiBankExportApprovedDetailsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ApprovedBeneficiaryDataFeedsDefaultExporterWithoutAddressStubOrPaymentMutation()
    {
        await using var db = fixture.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var company = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        var batch = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 9);
        var service = new SaudiBankExportService(db);
        await service.SaveSettingsAsync(tenant, company, Guid.NewGuid(), SaudiBankExportTestData.Settings(), default);
        var request = SaudiBankExportTestData.Request("980001");
        Assert.False((await service.ValidateAsync(tenant, batch, request, default)).Value!.CanExport);
        var employees = await db.Employees.Where(e => e.TenantId == tenant).ToListAsync();
        foreach (var employee in employees)
        {
            var maker = Guid.NewGuid();
            var profile = await db.EmployeePayrollProfiles.SingleAsync(p => p.TenantId == tenant && p.EmployeeId == employee.Id);
            var details = JsonSerializer.Serialize(new { schema = ApprovedSaudiBeneficiaryDetails.Schema,
                bicCode = profile.BankRoutingCode, employeeAddress1 = "Riyadh", employeeAddress2 = "Olaya", employeeAddress3 = "Building 11" });
            var ctrl = HrmHierarchyTests.BuildImportControllerInternal(db, tenant);
            ctrl.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, maker.ToString()),
                new Claim(ClaimTypes.Role, "Admin"), new Claim("is_group_scope", "true"),
                new Claim("permission", "employees.read"), new Claim("permission", "employees.write"), new Claim("permission", "employees.sensitive") }, "Test"));
            var accepted = Assert.IsType<AcceptedResult>(await ctrl.UpdateEmployee(employee.Id,
                new EmployeeUpdateRequest(DateOnly.FromDateTime(DateTime.UtcNow), new() { ["wpsBankDetails"] = JsonSerializer.SerializeToElement(details) }), default));
            using var response = JsonDocument.Parse(JsonSerializer.Serialize(accepted.Value));
            var approval = response.RootElement.GetProperty("approvalRequestId").GetGuid();
            Assert.Contains((await service.ValidateAsync(tenant, batch, request, default)).Value!.Errors, e => e.Code == "bank_change_pending");
            var workflow = new ApprovalWorkflowService(db, new AuditService(db));
            await workflow.DecideAsync(tenant, approval, new ApprovalDecisionRequest("Approve", "Independent synthetic review"),
                new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenant, ["HR Manager"], []), default);
        }
        var validation = (await service.ValidateAsync(tenant, batch, request, default)).Value!;
        Assert.True(validation.CanExport, JsonSerializer.Serialize(validation.Errors));
        var generated = await service.GenerateAsync(tenant, Guid.NewGuid(), batch, request, default);
        Assert.Equal(SaudiBankExportOutcome.Ok, generated.Outcome);
        Assert.Equal(SaudiBankExportOutcome.Ok, (await service.DownloadAsync(tenant, Guid.NewGuid(), batch, default)).Outcome);
        Assert.Equal("Draft", (await db.PayrollPaymentBatches.AsNoTracking().SingleAsync(b => b.Id == batch)).WpsStatus);
        Assert.Empty(await db.FinanceGlEntries.Where(g => g.TenantId == tenant).ToListAsync());
    }
}
