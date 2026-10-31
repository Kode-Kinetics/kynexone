using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;
using Zayra.Api.Tests.Onboarding;

namespace Zayra.Api.Tests.Security;

public sealed partial class EmployeeAccessTests
{
    [Fact]
    public async Task Forty_six_hires_require_independent_approval_then_activate_redeem_and_sign_in()
    {
        var w = await SeedAsync(maxUsers: 100);
        // A new company can establish direct administrative logins before any employee is active.
        var reviewer = await AddUserAsync(w, $"reviewer@{w.Domain}", ["Payroll Approver"], true, staff: true);
        var otherReviewer = await AddUserAsync(w, $"other.reviewer@{w.Domain}", ["Payroll Approver"], true, staff: true);
        (await SignInAsync(w.Slug, $"reviewer@{w.Domain}", StaffPassword)).Tokens.Should().NotBeNull();
        await using (var db = _fx.CreateDb())
        {
            var operators = await PayApprovalRouting.UsableOperatorsAsync(db, w.TenantId, w.CompanyId, default);
            operators.Should().Contain(o => o.Id == reviewer && o.CanApprovePay);
        }
        var ids = new List<int>();
        for (var i = 0; i < 46; i++)
        {
            await using var db = _fx.CreateDb();
            var request = Hire($"Recovery Person {i}", $"recovery.{i}@{w.Domain}", w.CompanyId) with {
                Nationality = "Indian",
                ComplianceRecords = [new EmployeeComplianceRecordRequest("SA", "iqama_number", "Iqama", TestIds.ValidIqama(i),
                    null, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), true, true)],
                SalaryBreakdown = new EmployeeSalaryBreakdownRequest(8000m + i, 0, 0, 0, 0, 0, 0, null, null, "SAR")
            };
            ids.Add((await Employees(db).CreateAsync(w.TenantId, request, Ctx(w.AdminId, w.TenantId), default)).Id);
        }
        // Mirror the reported cohort: the second reviewer is the last editor of one record.
        await UpdateEmployeeAsync(ids[^1], e => e.UpdatedBy = reviewer);
        var beforeApproval = await IssueAsync(w, w.HrOfficerId, ids.ToArray());
        beforeApproval.Issued.Should().BeEmpty();
        beforeApproval.Skipped.Should().HaveCount(46).And.OnlyContain(s => s.ReasonCode == EmployeeAccessService.Skip.AwaitingApproval);
        var activate = new EmployeeStatusChangeRequest("Active", DateOnly.FromDateTime(DateTime.UtcNow), "Independent pay review completed");
        foreach (var id in ids)
        {
            await using var db = _fx.CreateDb();
            var readiness = (await new EmployeeActivationGuard(db).EvaluateEmployeeAsync(w.TenantId, id, default))!.Value.Readiness;
            readiness.Blocking.Should().ContainSingle(b => b.Key == CompensationApprovalGate.GapType);
            await Assert.ThrowsAsync<EmployeeActivationBlockedException>(() =>
                Employees(db).ActivateAsync(w.TenantId, id, activate, Ctx(w.AdminId, w.TenantId), default));
            var self = await PayController(db, w, w.AdminId).Decide(id, new("unused", "approve", "Self approval must fail"), default);
            ((ObjectResult)self).StatusCode.Should().Be(403);
            var decider = id == ids[^1] ? otherReviewer : reviewer;
            if (id == ids[^1])
            {
                var barred = await PayController(db, w, reviewer).Detail(id, default);
                ((ObjectResult)barred).StatusCode.Should().Be(403);
            }
            var controller = PayController(db, w, decider);
            var detail = (OkObjectResult)await controller.Detail(id, default);
            var fingerprint = JsonSerializer.SerializeToElement(detail.Value).GetProperty("fingerprint").GetString()!;
            (await controller.Decide(id, new(fingerprint, "approve", "Checked signed offer and bank evidence in test fixture"), default))
                .Should().BeOfType<OkObjectResult>();
            (await new EmployeeActivationGuard(db).EvaluateEmployeeAsync(w.TenantId, id, default))!.Value.Readiness.IsBlocked.Should().BeFalse();
            (await db.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == id)).ActivationBlockersCount.Should().Be(0);
            (await Employees(db).ActivateAsync(w.TenantId, id, activate, Ctx(w.AdminId, w.TenantId), default))!.Status.Should().Be("Active");
        }
        var issued = await IssueAsync(w, w.HrOfficerId, ids.ToArray());
        issued.Skipped.Should().BeEmpty();
        issued.Issued.Should().HaveCount(46);
        foreach (var item in issued.Issued)
        {
            var email = $"recovery.{ids.IndexOf(item.EmployeeId)}@{w.Domain}";
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SignInAsync(w.Slug, email, NewPassword));
            await RedeemAsync(w, email, item.Code!);
            var login = await SignInAsync(w.Slug, email, NewPassword);
            var token = new JwtSecurityTokenHandler().ReadJwtToken(login.Tokens!.AccessToken);
            token.Claims.Single(c => c.Type == "employee_id").Value.Should().Be(item.EmployeeId.ToString());
            (await StateAsync(w, item.EmployeeId)).Should().Be(EmployeeAccessStates.Active);
            (await RedeemRefusedAsync(w, email, item.Code!)).Code.Should().Be(Zayra.Api.Infrastructure.Auth.WelcomeCodeRedeemer.Codes.Used);
        }
        await using var verify = _fx.CreateDb();
        (await verify.EmployeeImportGaps.CountAsync(g => g.TenantId == w.TenantId && g.GapType == CompensationApprovalGate.GapType && g.ResolvedAtUtc == null))
            .Should().Be(0);
    }

    private static PayApprovalsController PayController(ZayraDbContext db, World w, Guid user)
    {
        var claims = new List<Claim> { new("tenant_id", w.TenantId.ToString()), new(ClaimTypes.NameIdentifier, user.ToString()),
            new("permission", "payroll.approve") };
        claims.AddRange(EntityScopeClaims.Build(EntityScopeDescriptor.Group, Array.Empty<EntityAccessGrant>()));
        return new(db, new AuditService(db)) { ControllerContext = new() { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        } } };
    }
}
