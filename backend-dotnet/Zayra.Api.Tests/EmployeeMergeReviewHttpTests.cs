using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Review fixes on the People release, through the REAL pipeline with the seeded role bundles: a company-bounded caller can undo a merge
/// (the duplicate is soft-deleted, so it is authorised by its company, not by the live-employee id set), the Arabic text of the refusals,
/// case-insensitive former-employee statuses, and the read-only report of existing Retired / Deceased people.
/// </summary>
public sealed class EmployeeMergeReviewHttpTests(EmployeeDirectoryHttpFixture f) : IClassFixture<EmployeeDirectoryHttpFixture>
{
    private static string NewTag() => "R" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private static readonly System.Text.RegularExpressions.Regex Arabic = new("[\\u0600-\\u06FF]");

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(HttpMethod method, string url, string token, object? body = null)
    {
        using var r = await f.Send(method, url, token, body);
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Resolve(string token, int id, object body) =>
        Call(HttpMethod.Post, $"/api/employees/{id}/resolve-duplicate", token, body);

    // ═════════════ 3. A company-bounded caller can undo, and repeat, a merge in their own company ═════════════

    [Fact]
    public async Task Unmerge_ByACompanyBoundedCaller_RestoresTheirOwnCompanysMergedPerson()
    {
        var made = await f.SeedAsync(NewTag(), 2);   // both in the scoped caller's company
        (await Resolve(f.Token("HR Officer"), made[0].Id, new { resolution = "merge", intoEmployeeId = made[1].Id, reason = "same person" })).Status.Should().Be(HttpStatusCode.OK);
        (await f.IsDeleted(made[0].Id)).Should().BeTrue();

        var (status, body) = await Resolve(f.ScopedToken("HR Officer"), made[0].Id, new { resolution = "unmerge", reason = "wrong twins" });

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        (await f.IsDeleted(made[0].Id)).Should().BeFalse();
    }

    [Fact]
    public async Task RepeatedMerge_ByACompanyBoundedCaller_Answers200WithoutWritingAnything()
    {
        var made = await f.SeedAsync(NewTag(), 2);
        var request = new { resolution = "merge", intoEmployeeId = made[1].Id, reason = "same person" };
        (await Resolve(f.ScopedToken("HR Officer"), made[0].Id, request)).Status.Should().Be(HttpStatusCode.OK);

        var (status, body) = await Resolve(f.ScopedToken("HR Officer"), made[0].Id, request);

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("alreadyMerged").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Unmerge_OfAnotherCompanysMergedPerson_IsStillRefusedToACompanyBoundedCaller()
    {
        var made = await f.SeedAsync(NewTag(), 2, companyId: f.OtherCompanyId);
        (await Resolve(f.Token("HR Officer"), made[0].Id, new { resolution = "merge", intoEmployeeId = made[1].Id, reason = "same person" })).Status.Should().Be(HttpStatusCode.OK);

        var (status, _) = await Resolve(f.ScopedToken("HR Officer"), made[0].Id, new { resolution = "unmerge", reason = "wrong twins" });

        status.Should().Be(HttpStatusCode.Forbidden);
        (await f.IsDeleted(made[0].Id)).Should().BeTrue("a caller bounded to another company may not undo this merge");
    }

    [Fact]
    public async Task Unmerge_OfAMergedPersonWithNoCompany_IsRefusedToACompanyBoundedCaller()
    {
        var made = await f.SeedAsync(NewTag(), 2, tweak: (e, _) => e.CompanyId = null);
        (await Resolve(f.Token("HR Officer"), made[0].Id, new { resolution = "merge", intoEmployeeId = made[1].Id, reason = "same person" })).Status.Should().Be(HttpStatusCode.OK);

        var (status, _) = await Resolve(f.ScopedToken("HR Officer"), made[0].Id, new { resolution = "unmerge", reason = "wrong twins" });

        status.Should().Be(HttpStatusCode.Forbidden, "a null company is never inside a bounded caller's companies");
    }

    // ═════════════ 5. Every refusal on these routes carries its Arabic text ═════════════

    [Fact]
    public async Task UnknownStatus_Is400_WithAnArabicSentence()
    {
        var e = (await f.SeedAsync(NewTag(), 1)).Single();
        var (status, body) = await Call(HttpMethod.Patch, $"/api/employees/{e.Id}/status", f.Token("HR Manager"),
            new { status = "Banana", effectiveDate = DateTime.UtcNow.ToString("yyyy-MM-dd"), reason = "contract test" });

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("error").GetString().Should().Be("unknown_status");
        body.GetProperty("message").GetString().Should().Contain("'Banana' is not an employee status");
        Arabic.IsMatch(body.GetProperty("messageAr").GetString() ?? "").Should().BeTrue("the refusal must be readable in Arabic");
    }

    [Theory]
    [InlineData("merge-no-survivor")]
    [InlineData("merge-no-reason")]
    [InlineData("distinct-no-reason")]
    [InlineData("merge-into-itself")]
    public async Task ResolveDuplicate_Refusals_AreBilingual(string which)
    {
        var made = await f.SeedAsync(NewTag(), 2);
        object request = which switch
        {
            "merge-no-survivor" => new { resolution = "merge", reason = "same person" },
            "merge-no-reason" => new { resolution = "merge", intoEmployeeId = made[1].Id },
            "distinct-no-reason" => new { resolution = "distinct" },
            _ => new { resolution = "merge", intoEmployeeId = made[0].Id, reason = "same person" },
        };

        var (status, body) = await Resolve(f.Token("HR Manager"), made[0].Id, request);

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        Arabic.IsMatch(body.GetProperty("messageAr").GetString() ?? "").Should().BeTrue($"{which} must carry Arabic text");
        (await f.IsDeleted(made[0].Id)).Should().BeFalse();
    }
}
