using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CompanySetup;
using Zayra.Api.Application.Employees;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.CompanySetup;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.WorkWeek;
using Zayra.Api.Models;
using Zayra.Api.Tests.Onboarding;
using V = Zayra.Api.Application.CompanySetup.CompanySetupVocabulary;

namespace Zayra.Api.Tests.Security;

public sealed partial class EmployeeAccessTests
{
    [Fact]
    public async Task A_setup_created_reviewer_approves_pay_the_administrator_entered_and_the_administrator_cannot()
    {
        var w = await SeedAsync();
        await using (var db = _fx.CreateDb())
        {
            var permission = await db.Permissions.SingleAsync(p => p.Key == "payroll.approve");
            var role = new Role
            {
                Id = Guid.NewGuid(), TenantId = w.TenantId, Name = "Finance Approver", NormalizedName = "FINANCE APPROVER",
                Description = "Finance Approver", IsSystem = true, IsEditable = true, IsActive = true, AuthorityLevel = 8,
            };
            db.Roles.Add(role);
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
            await db.SaveChangesAsync();
        }

        // Setup's own post-commit path creates the reviewer: a direct login, no employee record.
        var reviewerEmail = $"finance.reviewer@{w.Domain}";
        await using (var db = _fx.CreateDb())
        {
            var audit = new AuditService(db);
            var setup = new CompanySetupService(db, new OrganizationSetupService(db, audit),
                new AccessManagementService(db, new Pbkdf2PasswordHasher(), audit, new JwtTokenService(Jwt)),
                new WorkWeekService(db), new RecordingEmail(configured: false));
            var caller = new CompanySetupCaller(w.TenantId, w.AdminId, true, false, Ctx(w.AdminId, w.TenantId));
            (await setup.ProvisionPayReviewerAsync(caller, w.CompanyId,
                new CompanySetupCompanyAdmin(V.AdminInvite, null, "Finance Reviewer", null, reviewerEmail), default)).Should().BeNull();
        }
        Guid reviewer;
        await using (var db = _fx.CreateDb())
        {
            var user = await db.Users.IgnoreQueryFilters().Include(u => u.EmployeeUserAccounts)
                .SingleAsync(u => u.TenantId == w.TenantId && u.NormalizedEmail == AuthService.Normalize(reviewerEmail));
            user.EmployeeUserAccounts.Should().BeEmpty();
            reviewer = user.Id;
            // Until they choose their own password they cannot act.
            (await PayApprovalRouting.UsableOperatorsAsync(db, w.TenantId, w.CompanyId, default)).Should().NotContain(o => o.Id == reviewer);
            user.MustChangePassword = false;
            await db.SaveChangesAsync();
        }
        await using (var db = _fx.CreateDb())
            (await PayApprovalRouting.UsableOperatorsAsync(db, w.TenantId, w.CompanyId, default))
                .Should().Contain(o => o.Id == reviewer && o.CanApprovePay && !o.CanEnterPay);

        // The administrator enters the pay; the second-person rule is unchanged.
        await using var hireDb = _fx.CreateDb();
        var request = Hire("Setup Reviewer Hire", $"hire.setup@{w.Domain}", w.CompanyId) with {
            Nationality = "Indian",
            ComplianceRecords = [new EmployeeComplianceRecordRequest("SA", "iqama_number", "Iqama", TestIds.ValidIqama(7),
                null, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), true, true)],
            SalaryBreakdown = new EmployeeSalaryBreakdownRequest(9000m, 0, 0, 0, 0, 0, 0, null, null, "SAR")
        };
        var id = (await Employees(hireDb).CreateAsync(w.TenantId, request, Ctx(w.AdminId, w.TenantId), default)).Id;

        var self = await PayController(hireDb, w, w.AdminId).Decide(id, new("unused", "approve", "Self approval must fail"), default);
        ((ObjectResult)self).StatusCode.Should().Be(403);

        var controller = PayController(hireDb, w, reviewer);
        var detail = (OkObjectResult)await controller.Detail(id, default);
        var fingerprint = JsonSerializer.SerializeToElement(detail.Value).GetProperty("fingerprint").GetString()!;
        (await controller.Decide(id, new(fingerprint, "approve", "Checked evidence in test fixture"), default)).Should().BeOfType<OkObjectResult>();
    }
}
