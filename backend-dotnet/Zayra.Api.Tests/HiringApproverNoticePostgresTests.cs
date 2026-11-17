using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>When an offer is accepted, the people who can approve the hire are told once, in English and Arabic; not the maker, not anyone without the key. Real Postgres.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class HiringApproverNoticePostgresTests(PostgresFixture fixture) : HiringPostgresTestBase(fixture)
{
    // ── Approvers are told ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AcceptingAnOffer_TellsTheHireApprovers_InBothLanguages_ButNotTheMakerOrAnOutsider()
    {
        Guid approver = Guid.NewGuid(), maker = Guid.NewGuid(), outsider = Guid.NewGuid();
        var s = await SeedAsync(extra: (db, tenant) =>
        {
            var permission = new Permission { Key = "employees.approve", Module = "Employees", Description = "Approve hires" };
            db.Permissions.Add(permission);
            Role NewRole(string name, bool withPermission)
            {
                var role = new Role { TenantId = tenant, Name = name, NormalizedName = name.ToUpperInvariant(), IsActive = true };
                db.Roles.Add(role);
                if (withPermission) db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
                return role;
            }
            void NewUser(Guid id, string name, Role role)
            {
                db.Users.Add(new User { Id = id, TenantId = tenant, Email = $"{name}-{id:N}@example.test", NormalizedEmail = $"{name}-{id:N}@example.test".ToUpperInvariant(),
                    FullName = name, PasswordHash = "x", Status = "Active", IsActive = true, IsGroupScope = true });
                db.UserRoles.Add(new UserRole { UserId = id, RoleId = role.Id });
            }
            var hr = NewRole("HR Manager", true);
            var viewer = NewRole("Viewer", false);
            NewUser(approver, "approver", hr);
            NewUser(maker, "maker", hr);
            NewUser(outsider, "outsider", viewer);
        });
        var recorded = new RecordingNotifications();

        await using (var db = Fixture.CreateDb())
        {
            var controller = new ApplicationsController(db, new RecruitmentService(db), recorded);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", s.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, maker.ToString()),
                new Claim(ClaimTypes.Name, "Maker"), new Claim(ClaimTypes.Role, "HR Manager"),
                new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
            }, "Test")) } };
            (await controller.AcceptOffer(s.OfferId, CancellationToken.None, await OfferWorkflowTestSupport.ResponseAsync(db, s.OfferId))).Should().BeOfType<OkObjectResult>();
        }

        var drafts = recorded.Calls.Where(c => c.EntityName == "EmployeeDraft").ToList();
        drafts.Should().ContainSingle("only the other holder of employees.approve is told").Which.UserId.Should().Be(approver);
        var notice = drafts.Single();
        notice.Message.Should().Contain("Noura Al Mansoori");
        (notice.Title + notice.Message).Should().MatchRegex("[؀-ۿ]", "the notice carries Arabic text");
        (notice.Title + notice.Message).Should().MatchRegex("[A-Za-z]", "and English text");
        notice.EntityId.Should().NotBeNullOrEmpty();
    }

}
