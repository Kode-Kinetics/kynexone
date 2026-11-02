using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Http;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// M6: the company write guard's refusals (company_scope_required, company_reassignment_blocked) are InvalidOperationExceptions carrying a
/// stable "code: explanation" message. They must reach the client as 409 with that code, not as a bare 400 "bad_request" or a 500.
/// </summary>
public class CompanyGuardRefusalMappingTests
{
    [Theory]
    [InlineData("company_scope_required: LeaveType in a multi-company tenant needs an explicit company.", "company_scope_required")]
    [InlineData("company_reassignment_blocked: LeaveType rows cannot change company outside an explicit transfer workflow.", "company_reassignment_blocked")]
    public void TheStableCodes_AreAnswered409WithTheirOwnCode(string message, string code)
    {
        var (status, mapped, text) = ExceptionResponseMapper.Map(new InvalidOperationException(message));
        (status, mapped, text).Should().Be((StatusCodes.Status409Conflict, code, message));
    }

    [Fact]
    public void OtherRefusals_KeepTheirStatus()
    {
        ExceptionResponseMapper.Map(new InvalidOperationException("Something else is wrong.")).Should().Be((400, "bad_request", "Something else is wrong."));
        ExceptionResponseMapper.Map(new UnauthorizedAccessException("company_scope_denied")).StatusCode.Should().Be(403);
        ExceptionResponseMapper.Map(new Exception("boom")).Should().Be((500, "internal_error", "An unexpected error occurred. Quote your traceId when contacting support."));
        // A message that merely MENTIONS a code is not the guard's refusal.
        ExceptionResponseMapper.Map(new InvalidOperationException("see company_scope_required docs")).StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task TheRealGuard_ForAReassignedRow_ProducesA409()
    {
        var w = await ScopeSodWorld.CreateAsync();
        var own = new LeaveType { TenantId = w.Tenant, CompanyId = w.CompanyA, Code = "OW", NameEn = "OW", IsActive = true };
        await using (var seed = w.Plain()) { seed.LeaveTypes.Add(own); await seed.SaveChangesAsync(); }
        await using var db = w.DbFor(w.CompanyAdmin(w.CompanyA));
        var row = await db.LeaveTypes.SingleAsync(x => x.Id == own.Id);
        row.CompanyId = w.CompanyB;

        var thrown = (await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>()).Which;

        var (status, code, _) = ExceptionResponseMapper.Map(thrown);
        (status, code).Should().Be((409, "company_reassignment_blocked"));
    }

    [Fact]
    public async Task TheRealGuard_ForAnAmbiguousNewRow_ProducesA409()
    {
        var w = await ScopeSodWorld.CreateAsync();
        // A caller who can see BOTH companies (but is not group scope) creating a config row with no company: which one is not guessable.
        await using var db = w.DbFor(w.TwoCompanyAdmin());
        db.LeaveTypes.Add(new LeaveType { TenantId = w.Tenant, CompanyId = null, Code = "AM", NameEn = "AM", IsActive = true });

        var thrown = (await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>()).Which;

        ExceptionResponseMapper.Map(thrown).Should().Match<(int, string, string)>(m => m.Item1 == 409 && m.Item2 == "company_scope_required");
    }
}
