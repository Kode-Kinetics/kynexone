using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The person a proposed package is about never decides it: the login of the employee cannot confirm or reject their own proposal,
/// even when someone else asked for it. Another HR user still can.
/// </summary>
public sealed class PackageSubjectDecisionTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);

    private static async Task<(ZayraDbContext Db, PackageSeed Seed)> SeededAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"pkg-subject-{Guid.NewGuid():N}").Options);
        var seed = new PackageSeed();
        await seed.SaveAsync(db);
        return (db, seed);
    }

    private static EmployeePackageController Package(ZayraDbContext db, PackageSeed s, Guid userId)
    {
        var resolver = new EntitlementResolver(db);
        var clock = new FixedTenantClock(Today);
        var controller = new EmployeePackageController(db, resolver, new EntitlementWriter(db, clock, resolver), clock);
        var claims = new List<Claim>
        {
            new("tenant_id", s.TenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, "HR Manager"), new("is_group_scope", "true"), new("permission", "entitlements.manage"),
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
        return controller;
    }

    private static async Task<Guid> ProposeAsync(ZayraDbContext db, PackageSeed s, Guid requester)
    {
        var proposal = await new EntitlementWriter(db, new FixedTenantClock(Today), new EntitlementResolver(db)).ProposeAsync(s.TenantId, s.Term.Id, default);
        var job = new BackgroundJob { TenantId = s.TenantId, JobType = PackageFreezeJobHandler.JobType, IdempotencyKey = "k", CreatedByUserId = requester };
        db.BackgroundJobs.Add(job);
        db.BackgroundJobItems.Add(new BackgroundJobItem
        {
            TenantId = s.TenantId, JobId = job.Id, ItemKey = PackageFreezeJobHandler.ItemKey(s.Term.Id), ResultJson = JsonSerializer.Serialize(proposal),
        });
        await db.SaveChangesAsync();
        return job.Id;
    }

    private static JsonElement Body(IActionResult result, int status)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(result);
        obj.StatusCode.Should().Be(status);
        return JsonSerializer.SerializeToElement(obj.Value);
    }

    [Fact]
    public async Task TheEmployeesOwnLogin_CannotConfirmTheirOwnProposal_AndNothingIsWritten()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        var batch = await ProposeAsync(db, s, requester: Guid.NewGuid());

        var result = await Package(db, s, s.UserId).ConfirmProposal(batch, new ConfirmProposalRequest(s.Term.Id, null), default);

        var body = Body(result, 400);
        body.GetProperty("error").GetString().Should().Be(SubjectDecisionBar.ErrorCode);
        body.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        (await db.EmployeeEntitlements.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TheEmployeesOwnLogin_CannotRejectTheirOwnProposal_ButAnotherHrUserCan()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        var batch = await ProposeAsync(db, s, requester: Guid.NewGuid());

        var own = await Package(db, s, s.UserId).RejectProposal(batch, new RejectProposalRequest(s.Term.Id, "Does not match the contract."), default);
        Body(own, 400).GetProperty("error").GetString().Should().Be(SubjectDecisionBar.ErrorCode);

        var other = await Package(db, s, Guid.NewGuid()).RejectProposal(batch, new RejectProposalRequest(s.Term.Id, "Does not match the contract."), default);
        other.Should().BeOfType<OkObjectResult>();
    }
}
