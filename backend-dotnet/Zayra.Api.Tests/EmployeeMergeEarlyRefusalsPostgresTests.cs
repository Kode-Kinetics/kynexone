using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>A merge that is going to be refused (different companies, no usable survivor) is refused BEFORE it takes the tenant-wide finance create locks.</summary>
public sealed partial class EmployeeMergeOwnedRecordsPostgresTests
{
    // ── The merge refuses what it can refuse before it queues for any tenant-wide finance lock ──────

    [Fact]
    public async Task Merge_AcrossCompanies_RefusesBeforeTakingAnyFinanceLock()
    {
        var t = await SeedTwinsAsync();
        await SetCompaniesAsync(t, t.CompanyId, await SeedOtherCompanyAsync(t));
        var seen = new List<(string Scope, Guid TenantId, Guid AggregateId)>();
        FinanceDecisionSerializer.Recorder.Value = seen;
        IActionResult result;
        try { result = await MergeAsync(t); }
        finally { FinanceDecisionSerializer.Recorder.Value = null; }

        Assert.IsAssignableFrom<ObjectResult>(result).StatusCode.Should().Be(Microsoft.AspNetCore.Http.StatusCodes.Status409Conflict);
        seen.Should().BeEmpty("a merge that is going to be refused for its companies must not take finance.loan-create or finance.advance-create");
    }

    [Fact]
    public async Task Merge_AcrossCompanies_DoesNotWaitForAFinanceCreateLockThatSomeoneElseHolds()
    {
        var t = await SeedTwinsAsync();
        await SetCompaniesAsync(t, t.CompanyId, await SeedOtherCompanyAsync(t));
        var (holder, release) = await HoldFinanceLockAsync(t, "finance.loan-create");

        var merge = MergeAsync(t);
        var finished = await Task.WhenAny(merge, Task.Delay(8000));
        var returned = finished == merge;
        release.SetResult();
        await holder;

        returned.Should().BeTrue("a cross-company merge is refused before it queues behind a loan being created");
        Assert.IsAssignableFrom<ObjectResult>(await merge).StatusCode.Should().Be(Microsoft.AspNetCore.Http.StatusCodes.Status409Conflict);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("deleted")]
    public async Task Merge_IntoAMissingOrDeletedSurvivor_RefusesBeforeTakingAnyFinanceLock(string kind)
    {
        var t = await SeedTwinsAsync();
        var twins = t;
        if (kind == "missing") twins = new Twins(t.TenantId, t.CompanyId, t.Duplicate, new Employee { Id = 987_654_321 });
        else
        {
            await using var seed = _fx.CreateDb();
            var survivor = await seed.Employees.SingleAsync(e => e.Id == t.Survivor.Id);
            survivor.IsDeleted = true; survivor.DeletedAtUtc = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }
        var seen = new List<(string Scope, Guid TenantId, Guid AggregateId)>();
        FinanceDecisionSerializer.Recorder.Value = seen;
        IActionResult result;
        try { result = await MergeAsync(twins); }
        finally { FinanceDecisionSerializer.Recorder.Value = null; }

        Assert.IsAssignableFrom<ObjectResult>(result).StatusCode.Should().Be(Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest);
        seen.Should().BeEmpty();
        (await IsDeletedAsync(t.Duplicate.Id)).Should().BeFalse();
    }
}
