using System.Text.RegularExpressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Zayra.Api.Tests;

/// <summary>
/// The set-based queries behind GET /api/pay-approvals (PayApprovalBatch) run against REAL Postgres, so their translation is proven on the production
/// provider, not only on SQLite. It seeds the same scenario as <see cref="PayApprovalsListQueryCountTests"/> and asserts the same response: a pending
/// package (with its designation and author), a package sent back (job-title fallback, rejection, a bank account shared with the first), a creator who is
/// barred from approving their own hire, another company's hire hidden from a company-scoped caller, and the employees that must not be listed.
/// Only what Postgres legitimately changes is normalised: employee ids (serial values shared with other tests), the "Z" on UTC times, and decimal scale.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class PayApprovalsListPostgresTests(PostgresFixture fixture)
{
    private async Task SeedOnceAsync()
    {
        await using var db = fixture.CreateDb();
        if (await db.Tenants.AnyAsync(t => t.Id == PayApprovalsListQueryCountTests.Tenant)) return;
        await PayApprovalsListQueryCountTests.SeedBaseAsync(db);
        await PayApprovalsListQueryCountTests.SeedScenarioAsync(db);
    }

    private static string Comparable(string json) =>
        Regex.Replace(Regex.Replace(PayApprovalsListQueryCountTests.Normalize(json), "\"employeeId\":\\d+", "\"employeeId\":0"), "(\\d{2}:\\d{2}:\\d{2}(?:\\.\\d+)?)Z\"", "$1\"");

    private async Task<string> ListAsync(Guid? scopedToCompany)
    {
        await SeedOnceAsync();
        await using var db = fixture.CreateDb();   // a fresh context, as a request has: nothing tracked
        var result = await PayApprovalsListQueryCountTests.Controller(db, scopedToCompany).List(CancellationToken.None);
        return Comparable(JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value));
    }

    [Fact]
    public async Task TheList_ForAGroupCaller_IsTheSameAsOnSqlite()
    {
        var actual = await ListAsync(null);

        actual.Should().Be(Comparable(PayApprovalsListSnapshots.Group));
        // The three cases the snapshot pins, named: pending with its author and designation, sent back, and the barred creator counted but not listed.
        actual.Should().Contain("\"employeeCode\":\"E-001\"").And.Contain("\"designation\":\"Senior Analyst\"").And.Contain("\"Name\":\"Huda HR Officer\"");
        actual.Should().Contain("\"employeeCode\":\"E-002\"").And.Contain("\"status\":\"rejected\"").And.Contain("\"ibanLast4\":\"7519\"");   // E-002 shares E-001's account
        actual.Should().NotContain("E-003").And.Contain("\"requiresOtherReviewerCount\":1");
    }

    [Fact]
    public async Task TheList_ForACallerScopedToOneCompany_IsTheSameAsOnSqlite()
    {
        var actual = await ListAsync(PayApprovalsListQueryCountTests.CompanyA);

        actual.Should().Be(Comparable(PayApprovalsListSnapshots.CompanyA));
        actual.Should().NotContain("E-004", "the other company's hire is outside the caller's scope");
    }
}
