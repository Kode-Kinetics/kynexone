using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// The employee directory API the People screen calls (<c>frontend/src/views/EmployeesPage.tsx</c>,
/// <c>frontend/src/api/employees.ts</c>, <c>frontend/src/api/employeeAccess.ts</c>), driven through the REAL pipeline with the
/// SEEDED role bundles (so a role's effective permissions are exactly what a customer's user gets).
///
/// Three questions: (1) does every route the screen calls exist, with the verb it uses; (2) does the JSON carry every field the
/// screen types say it reads; (3) do paging, scope, role and permission behave the way the screen assumes.
///
/// Tests marked Skip are contract gaps found by this suite: each states the defect, and the live assertion is the behaviour the
/// screen needs. They are listed in /tmp/claude/people-test/backend.md and stay skipped until the product is fixed.
/// </summary>
public sealed class EmployeeDirectoryContractHttpTests(EmployeeDirectoryHttpFixture f) : IClassFixture<EmployeeDirectoryHttpFixture>
{
    private static string NewTag() => "T" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetJson(string url, string token)
    {
        using var r = await f.Send(HttpMethod.Get, url, token);
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    // ═════════════════════════════ 1. Routes: every call the screen makes exists, same verb ═════════════════════════════

    [Fact]
    public void EveryEmployeeApiCallInTheFrontend_HasABackendRouteWithTheSameVerb()
    {
        var root = RepoFiles.Root();
        var files = new List<string> { Path.Combine(root, "frontend", "src", "views", "EmployeesPage.tsx") };
        files.AddRange(Directory.GetFiles(Path.Combine(root, "frontend", "src", "api"), "employee*.ts"));
        var rx = new Regex(@"client\s*\.\s*(get|post|put|patch|delete)\s*(?:<[^(]*?>)?\s*\(\s*([`'""])(/api/employee[^`'""]*)\2", RegexOptions.Singleline);
        var calls = files.SelectMany(file => rx.Matches(File.ReadAllText(file)).Select(m => (Verb: m.Groups[1].Value.ToUpperInvariant(), Path: NormaliseFrontend(m.Groups[3].Value), File: Path.GetFileName(file)))).Distinct().ToList();
        calls.Count.Should().BeGreaterThan(40, "the scan must actually find the screen's calls, or it proves nothing");

        var routes = f.Host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>()).Select(v => (Verb: v.ToUpperInvariant(), Path: NormaliseRoute(e.RoutePattern.RawText ?? ""))))
            .ToHashSet();
        var missing = calls.Where(c => !routes.Contains((c.Verb, c.Path))).Select(c => $"{c.Verb} {c.Path} ({c.File})").ToList();
        missing.Should().BeEmpty("a frontend call with no backend route (or the wrong verb) is a 404/405 the user meets as a broken button");
    }

    [Theory]
    [InlineData("GET", "api/employees")]
    [InlineData("GET", "api/employees/ex-employees")]
    [InlineData("GET", "api/employees/{}")]
    [InlineData("POST", "api/employees")]
    [InlineData("PUT", "api/employees/{}")]
    [InlineData("DELETE", "api/employees/{}")]
    [InlineData("PATCH", "api/employees/{}/status")]
    [InlineData("POST", "api/employees/{}/activate")]
    [InlineData("POST", "api/employees/{}/terminate")]
    [InlineData("GET", "api/employees/{}/readiness")]
    [InlineData("POST", "api/employees/import")]
    [InlineData("POST", "api/employees/import-preview")]
    [InlineData("GET", "api/employees/import-template")]
    [InlineData("GET", "api/employees/export")]
    [InlineData("POST", "api/employees/bulk")]
    [InlineData("POST", "api/employees/bulk-activate")]
    [InlineData("POST", "api/employees/duplicate-check")]
    [InlineData("POST", "api/employees/derive-work-email")]
    [InlineData("POST", "api/employees/{}/resolve-duplicate")]
    [InlineData("POST", "api/employees/{}/bank-details/confirm")]
    [InlineData("GET", "api/employee-access/{}")]
    [InlineData("GET", "api/employee-access/summary")]
    [InlineData("POST", "api/employee-access/codes")]
    [InlineData("POST", "api/employee-access/work-emails")]
    public void TheDirectoryRoutes_ExistWithTheirVerb(string verb, string path)
    {
        var routes = f.Host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>()).Select(v => (Verb: v.ToUpperInvariant(), Path: NormaliseRoute(e.RoutePattern.RawText ?? ""))))
            .ToHashSet();
        routes.Should().Contain((verb, path));
    }

    private static string NormaliseFrontend(string path) =>
        Regex.Replace(path.Split('?')[0].Trim('/'), @"\$\{[^}]*\}", "{}").ToLowerInvariant();

    private static string NormaliseRoute(string raw) => Regex.Replace(raw.Trim('/'), @"\{[^}]*\}", "{}").ToLowerInvariant();

    // ═════════════════════════════ 2. Wire shape: every field the screen types read is in the JSON ═════════════════════════════

    [Fact]
    public async Task List_EachRowCarriesEveryFieldTheScreenReads_AndTheEnvelopeHasPagingFields()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 1, tweak: (e, _) =>
        {
            e.VisaExpiryDate = new DateOnly(2027, 3, 4);
            e.PassportExpiryDate = new DateOnly(2030, 1, 2);
            e.WorkEmail = "shape@dir.test";
            e.ReadinessState = "NeedsAttention";
            e.ActivationBlockersCount = 2;
            e.ProfileCompletenessScore = 62.5m;
        });

        var (status, body) = await GetJson($"/api/employees?search={tag}", f.Token("HR Officer"));

        status.Should().Be(HttpStatusCode.OK);
        foreach (var key in new[] { "items", "total", "page", "pageSize" }) body.TryGetProperty(key, out _).Should().BeTrue("the envelope field '" + key + "' is read by requirePage / fetchAllPages");
        var row = body.GetProperty("items").EnumerateArray().Single();
        // iqamaNumber is typed (required) on EmployeeListItem but the list projection never carries it: see List_RowsNeverCarryIdentityDocumentNumbers.
        foreach (var field in TsFields("EmployeeListItem").Where(x => x != "iqamaNumber"))
            row.TryGetProperty(field, out _).Should().BeTrue($"EmployeeListItem.{field} is declared by the screen's type but missing from GET /api/employees rows");
        row.GetProperty("visaExpiryDate").GetString().Should().Be("2027-03-04");
        row.GetProperty("passportExpiryDate").GetString().Should().Be("2030-01-02");
        row.GetProperty("readinessState").GetString().Should().Be("NeedsAttention");
        row.GetProperty("activationBlockersCount").GetInt32().Should().Be(2);
        row.GetProperty("profileCompletenessScore").GetDecimal().Should().Be(62.5m);
        row.GetProperty("publicId").GetGuid().Should().NotBe(Guid.Empty);
        row.GetProperty("workEmail").GetString().Should().Be("shape@dir.test");
        row.GetProperty("accessState").GetString().Should().BeOneOf("waiting_for_work_email", "not_started", "code_given", "active", "stopped", "blocked");
        row.GetProperty("loginEnabled").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        row.GetProperty("passwordConfigured").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
    }

    [Fact]
    public async Task List_RowsNeverCarryIdentityDocumentNumbers_SoTheTypeShouldNotPromiseThem()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 1, tweak: (e, _) => { e.IqamaNumber = "2123456789"; e.BankIban = "SA0380000000608010167519"; });
        var (_, body) = await GetJson($"/api/employees?search={tag}", f.Token("Payroll Officer"));
        var row = body.GetProperty("items").EnumerateArray().Single();
        row.TryGetProperty("iqamaNumber", out _).Should().BeFalse("the directory projection is deliberately non-sensitive, even for a caller holding employees.sensitive");
        body.GetRawText().Should().NotContain("2123456789").And.NotContain("SA0380000000608010167519");
        // Contract gap (Low): frontend/src/api/employees.ts declares `iqamaNumber: string` (required) on EmployeeListItem; nothing reads it, but the type lies.
    }

    [Fact]
    public async Task ExEmployees_EachRowCarriesEveryFieldTheArchiveTypeReads()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 1, status: "Terminated");
        var (status, body) = await GetJson($"/api/employees/ex-employees?search={tag}", f.Token("HR Officer"));
        status.Should().Be(HttpStatusCode.OK);
        var row = body.GetProperty("items").EnumerateArray().Single();
        foreach (var field in TsFields("ExEmployeeListItem"))
            row.TryGetProperty(field, out _).Should().BeTrue($"ExEmployeeListItem.{field} missing from GET /api/employees/ex-employees");
        row.GetProperty("lastStatus").GetString().Should().Be("Terminated");
    }

    [Fact]
    public async Task Detail_CarriesEveryFieldTheEmployeeEntityTypeDeclares()
    {
        var e = (await f.SeedAsync(NewTag(), 1)).Single();
        var (status, body) = await GetJson($"/api/employees/{e.Id}", f.Token("HR Manager"));
        status.Should().Be(HttpStatusCode.OK);
        var missing = TsFields("EmployeeEntity").Where(field => field != "tenantId" && !body.TryGetProperty(field, out _)).ToList();
        missing.Should().BeEmpty("these EmployeeEntity fields are read by the profile drawer but absent from GET /api/employees/{id}");
        foreach (var field in new[] { "documents", "history", "transfers", "complianceRecords" })
            body.TryGetProperty(field, out var v).Should().BeTrue(field + " is non-optional on EmployeeDetail");
    }

    [Fact]
    public async Task EmployeeAccess_Get_CarriesEveryFieldTheAccessDtoTypeDeclares()
    {
        var e = (await f.SeedAsync(NewTag(), 1)).Single();
        var (status, body) = await GetJson($"/api/employee-access/{e.Id}", f.Token("HR Officer"));
        status.Should().Be(HttpStatusCode.OK);
        var missing = TsFields("EmployeeAccessDto", "api/employeeAccess.ts").Where(field => !body.TryGetProperty(field, out _)).ToList();
        missing.Should().BeEmpty("these EmployeeAccessDto fields are typed by the access client but absent from GET /api/employee-access/{id}");
        body.GetProperty("state").GetString().Should().Be("waiting_for_work_email");
        body.GetProperty("loginEnabled").GetBoolean().Should().BeFalse();
        body.GetProperty("passwordConfigured").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task EmployeeAccess_Summary_KeysAreTheClientsStateNames()
    {
        await f.SeedAsync(NewTag(), 1);
        var (status, body) = await GetJson("/api/employee-access/summary", f.Token("HR Officer"));
        status.Should().Be(HttpStatusCode.OK);
        var known = new[] { "waiting_for_work_email", "not_started", "code_given", "active", "stopped", "blocked" };
        body.EnumerateObject().Select(p => p.Name).Should().OnlyContain(n => known.Contains(n));
        body.GetProperty("waiting_for_work_email").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact(Skip = "CONTRACT GAP (Low): api/employeeAccess.ts types conflicts[].username (the kept username of an active login) and AddWorkEmailsDialog prints it for 'username_differs', "
        + "but BackfillConflictDto has no such property; the dialog silently drops the whole sentence (row.username is always undefined).")]
    public async Task WorkEmailBackfill_ConflictRows_CarryTheUsernameTheDialogPrints()
    {
        // AddWorkEmailsDialog prints the kept username for a `username_differs` conflict (api/employeeAccess.ts: `username?`), and
        // suppresses the whole sentence when it is absent. The server's conflict row has no such property to fill.
        typeof(BackfillConflictDto).GetProperty("Username").Should().NotBeNull(
            "WorkEmailBackfillResult.conflicts[].username is typed by the client; BackfillConflictDto only has EmployeeCode, WorkEmail, Reason");
    }

    // ═════════════════════════════ 3. Paging: the page=1/pageSize=1 count call, caps, overflow ═════════════════════════════

    [Theory]
    [InlineData(1, 25, 1, 25, 7)]      // the default list
    [InlineData(1, 0, 1, 1, 1)]        // pageSize 0 is clamped up to 1, not an empty page and not a divide-by-zero
    [InlineData(1, -5, 1, 1, 1)]       // negative pageSize clamped up to 1
    [InlineData(0, 3, 1, 3, 3)]        // page 0 -> page 1
    [InlineData(-2, 3, 1, 3, 3)]       // negative page -> page 1
    [InlineData(1, 1000, 1, 100, 7)]   // the huge pageSize is capped at 100 and echoed as 100
    [InlineData(3, 3, 3, 3, 1)]        // last partial page
    [InlineData(99, 5, 99, 5, 0)]      // page beyond the last: empty items, the real total, the requested page echoed
    [InlineData(1, 1, 1, 1, 1)]        // the "Everyone" count call: one row, total still the whole population
    public async Task List_Paging_ClampsAndEchoes(int page, int pageSize, int expectedPage, int expectedPageSize, int expectedItems)
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 7);

        var (status, body) = await GetJson($"/api/employees?search={tag}&page={page}&pageSize={pageSize}", f.Token("HR Officer"));

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("total").GetInt32().Should().Be(7, "total counts the whole matching set whatever page is asked for");
        body.GetProperty("page").GetInt32().Should().Be(expectedPage);
        body.GetProperty("pageSize").GetInt32().Should().Be(expectedPageSize);
        body.GetProperty("items").GetArrayLength().Should().Be(expectedItems);
    }

    [Fact]
    public async Task List_PageSizeCap_Is100_AndPagesDoNotOverlapOrSkip()
    {
        // fetchAllPages(100) and resolveAllMatching(100) rely on a 100-row page and on total being exact.
        var tag = NewTag();
        await f.SeedAsync(tag, 105);
        var token = f.Token("HR Officer");

        var (_, p1) = await GetJson($"/api/employees?search={tag}&page=1&pageSize=100", token);
        var (_, p2) = await GetJson($"/api/employees?search={tag}&page=2&pageSize=100", token);
        var (_, capped) = await GetJson($"/api/employees?search={tag}&page=1&pageSize=500", token);

        p1.GetProperty("total").GetInt32().Should().Be(105);
        p1.GetProperty("items").GetArrayLength().Should().Be(100);
        p2.GetProperty("items").GetArrayLength().Should().Be(5);
        capped.GetProperty("items").GetArrayLength().Should().Be(100, "pageSize=500 is answered with 100 rows");
        capped.GetProperty("pageSize").GetInt32().Should().Be(100);
        var codes = p1.GetProperty("items").EnumerateArray().Concat(p2.GetProperty("items").EnumerateArray()).Select(r => r.GetProperty("employeeCode").GetString()!).ToList();
        codes.Should().OnlyHaveUniqueItems().And.HaveCount(105).And.BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("pageSize=abc")]
    [InlineData("page=1.5")]
    [InlineData("pageSize=99999999999")]
    [InlineData("page=")]
    public async Task List_NonNumericPaging_IsA400_NotA500(string query)
    {
        var (status, _) = await GetJson("/api/employees?" + query, f.Token("HR Officer"));
        status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_AbsurdlyHighPage_IsAnEmptyPage_NotThePageOneRowsOr500()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 3);
        var (status, body) = await GetJson($"/api/employees?search={tag}&page=2147483647&pageSize=100", f.Token("HR Officer"));
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    // ═════════════════════════════ 4. Current list versus the Ex-Employees archive ═════════════════════════════

    [Fact]
    public async Task CurrentList_ExcludesLeaversAndDeleted_ArchiveHoldsThem_AndNeitherHoldsTheOther()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 1, status: "Active", suffix: "A");
        await f.SeedAsync(tag, 1, status: "Draft", suffix: "D");
        await f.SeedAsync(tag, 1, status: "Suspended", suffix: "S");
        foreach (var exit in new[] { "Terminated", "Archived", "Offboarded", "Exited" })
            await f.SeedAsync(tag, 1, status: exit, suffix: exit[..3]);
        await f.SeedAsync(tag, 1, status: "Active", suffix: "DEL", tweak: (e, _) => { e.IsDeleted = true; e.DeletedAtUtc = DateTime.UtcNow; });
        var token = f.Token("HR Officer");

        var (_, current) = await GetJson($"/api/employees?search={tag}&pageSize=100", token);
        var (_, archive) = await GetJson($"/api/employees/ex-employees?search={tag}&pageSize=100", token);

        var currentStatuses = current.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("status").GetString()).ToList();
        currentStatuses.Should().BeEquivalentTo(new[] { "Active", "Draft", "Suspended" });
        current.GetProperty("total").GetInt32().Should().Be(3);
        archive.GetProperty("total").GetInt32().Should().Be(5, "four exit statuses plus the soft-deleted row");
        archive.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("employeeCode").GetString()).Intersect(
            current.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("employeeCode").GetString())).Should().BeEmpty();
        archive.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("isDeleted").GetBoolean()).GetProperty("lastStatus").GetString().Should().Be("Active");
    }

    [Fact]
    public async Task CurrentList_StatusTerminated_IsEmpty_BecauseLeaversLiveInTheArchive()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 2, status: "Terminated");
        var (_, body) = await GetJson($"/api/employees?search={tag}&status=Terminated", f.Token("HR Officer"));
        body.GetProperty("total").GetInt32().Should().Be(0);
        var (_, archive) = await GetJson($"/api/employees/ex-employees?search={tag}&status=Terminated", f.Token("HR Officer"));
        archive.GetProperty("total").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ExEmployees_Paging_ClampsLikeTheCurrentList()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 4, status: "Archived");
        var token = f.Token("HR Officer");
        var (_, zero) = await GetJson($"/api/employees/ex-employees?search={tag}&page=0&pageSize=0", token);
        zero.GetProperty("page").GetInt32().Should().Be(1);
        zero.GetProperty("pageSize").GetInt32().Should().Be(1);
        zero.GetProperty("items").GetArrayLength().Should().Be(1);
        zero.GetProperty("total").GetInt32().Should().Be(4);
        var (_, beyond) = await GetJson($"/api/employees/ex-employees?search={tag}&page=9&pageSize=3", token);
        beyond.GetProperty("items").GetArrayLength().Should().Be(0);
        beyond.GetProperty("total").GetInt32().Should().Be(4);
        var (_, huge) = await GetJson($"/api/employees/ex-employees?search={tag}&pageSize=9999", token);
        huge.GetProperty("pageSize").GetInt32().Should().Be(100);
    }

    [Fact]
    public async Task ResignedStaysInTheCurrentList_RetiredIsInTheArchive_EachInExactlyOne()
    {
        // Resigned means "serving the notice period": still employed, paid and punching in, so not a leaver. Retired is over.
        var tag = NewTag();
        await f.SeedAsync(tag, 1, status: "Resigned", suffix: "R");
        await f.SeedAsync(tag, 1, status: "Retired", suffix: "T");
        var token = f.Token("HR Officer");
        var (_, current) = await GetJson($"/api/employees?search={tag}", token);
        var (_, archive) = await GetJson($"/api/employees/ex-employees?search={tag}", token);
        (current.GetProperty("total").GetInt32() + archive.GetProperty("total").GetInt32()).Should().Be(2);
        current.GetProperty("total").GetInt32().Should().Be(1, "a person who resigned is still employed through the notice period");
        current.GetProperty("items")[0].GetProperty("status").GetString().Should().Be("Resigned");
        archive.GetProperty("total").GetInt32().Should().Be(1);
        archive.GetProperty("items")[0].GetProperty("lastStatus").GetString().Should().Be("Retired");
    }

    /// <summary>Not a status: a typo, an arbitrary label, a near-miss, a value that only differs by padding or punctuation.</summary>
    [Theory]
    [InlineData("Banana")]
    [InlineData("Act1ve")]
    [InlineData("Active ")]
    [InlineData("Pre boarding")]
    [InlineData("Fired")]
    [InlineData("<script>alert(1)</script>")]
    public async Task ChangeStatus_AnUnknownStatus_IsRefused(string unknown)
    {
        var e = (await f.SeedAsync(NewTag(), 1)).Single();
        using var r = await f.Send(HttpMethod.Patch, $"/api/employees/{e.Id}/status", f.Token("HR Manager"),
            new { status = unknown, effectiveDate = DateTime.UtcNow.ToString("yyyy-MM-dd"), reason = "contract test" });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await r.Content.ReadAsStringAsync();
        text.Should().Contain("unknown_status").And.Contain("Terminated", "the refusal names the statuses that are accepted");
        (await f.StatusOf(e.Id)).Should().Be("Active", "a refused status change writes nothing");
    }

    [Fact]
    public async Task ChangeStatus_AnUnknownStatus_IsRefusedForTheWriteOnlyRoleToo_AndNothingIsRecorded()
    {
        var e = (await f.SeedAsync(NewTag(), 1)).Single();
        using var r = await f.Send(HttpMethod.Patch, $"/api/employees/{e.Id}/status", f.Token("HR Officer"),
            new { status = "Banana", effectiveDate = DateTime.UtcNow.ToString("yyyy-MM-dd"), reason = "contract test" });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var scope = f.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await db.EmployeeStatusHistories.IgnoreQueryFilters().AnyAsync(h => h.EmployeeId == e.Id && h.NewStatus == "Banana")).Should().BeFalse();
    }

    /// <summary>
    /// The vocabulary guard must not narrow what the route accepts today: every status the selector on this screen offers, and
    /// every status the backend defines (EmployeeStatuses), still gets past validation (a 400 here would be a regression; a readiness
    /// 422 or similar is a different, existing rule). The permission is unchanged: write-level callers reach the same statuses.
    /// </summary>
    [Fact]
    public async Task ChangeStatus_EveryStatusTheScreenOffersOrTheBackendDefines_StillPassesValidation()
    {
        var text = File.ReadAllText(Path.Combine(RepoFiles.Root(), "frontend", "src", "views", "EmployeesPage.tsx"));
        var m = Regex.Match(text, @"const statusOptions: StatusFilter\[\] = \[(.*?)\];", RegexOptions.Singleline);
        m.Success.Should().BeTrue("the status selector's option list must be findable in EmployeesPage.tsx");
        var ui = Regex.Matches(m.Groups[1].Value, @"'([^']*)'").Select(x => x.Groups[1].Value).Where(x => x.Length > 0).ToList();
        ui.Should().Contain(["Active", "Resigned", "Pre-boarding", "Notice period"]);
        var backend = new[] { "Draft", "Invited", "Active", "Suspended", "Offboarded", "Archived", "Inactive", "Terminated", "Exited" };
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        foreach (var status in ui.Concat(backend).Distinct())
            foreach (var role in new[] { "HR Manager", "HR Officer" })
            {
                var e = (await f.SeedAsync(NewTag(), 1, status: "Draft")).Single();
                using var r = await f.Send(HttpMethod.Patch, $"/api/employees/{e.Id}/status", f.Token(role),
                    new { status, effectiveDate = today, reason = "vocabulary regression" });
                var responseText = await r.Content.ReadAsStringAsync();
                responseText.Should().NotContain("unknown_status", $"'{status}' is a status the product knows ({role}, HTTP {(int)r.StatusCode})");
                r.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, $"{role} (employees.write) must keep reaching '{status}' on this route: the permission is an open owner decision");
            }
    }

    [Theory]
    [InlineData("inactive", "Inactive")]
    [InlineData("TERMINATED", "Terminated")]
    [InlineData("on LEAVE", "On leave")]
    public async Task ChangeStatus_AKnownStatusInAnotherCase_IsStoredInItsCanonicalSpelling(string sent, string stored)
    {
        var e = (await f.SeedAsync(NewTag(), 1)).Single();
        using var r = await f.Send(HttpMethod.Patch, $"/api/employees/{e.Id}/status", f.Token("HR Manager"),
            new { status = sent, effectiveDate = DateTime.UtcNow.ToString("yyyy-MM-dd"), reason = "case" });
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        (await f.StatusOf(e.Id)).Should().Be(stored, "the lifecycle rules compare the status ordinally; a lower-case copy would skip credential revocation");
    }

    // ═════════════════════════════ 5. Authorization: role, permission, tenant, company ═════════════════════════════

    [Fact]
    public async Task List_WithoutAToken_Is401()
    {
        using var r = await f.Client.GetAsync("/api/employees");
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("HR Manager")]
    [InlineData("HR Officer")]
    [InlineData("Payroll Officer")]
    [InlineData("Manager")]
    [InlineData("Auditor")]
    public async Task List_AdmitsTheRolesNamedOnTheEndpoint(string role)
    {
        var (status, body) = await GetJson("/api/employees?pageSize=1", f.Token(role));
        status.Should().Be(HttpStatusCode.OK, role);
        body.TryGetProperty("items", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("Employee")]
    [InlineData("Kiosk Operator")]
    public async Task List_RefusesRolesWithoutEmployeesRead(string role)
    {
        var (status, _) = await GetJson("/api/employees", f.Token(role));
        status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("HR Director")]
    [InlineData("Payroll Manager")]
    [InlineData("Finance")]
    [InlineData("Finance Approver")]
    [InlineData("Compliance Officer")]
    [InlineData("Supervisor")]
    [InlineData("Recruiter")]
    [InlineData("HR Assistant")]
    public async Task List_AdmitsEveryRoleHoldingEmployeesRead_BecauseTheRoleGateIsReconciledToThePermission(string role)
    {
        var (status, _) = await GetJson("/api/employees?pageSize=1", f.Token(role));
        status.Should().Be(HttpStatusCode.OK, role + " holds employees.read, the only thing the People menu checks");
    }

    [Fact]
    public async Task Update_AdmitsEveryRoleHoldingEmployeesWrite_HrDirectorIncluded()
    {
        var e = (await f.SeedAsync(NewTag(), 1)).Single();
        using var r = await f.Send(HttpMethod.Put, $"/api/employees/{e.Id}", f.Token("HR Director"),
            new { effectiveDate = "2026-11-01", changes = new Dictionary<string, object> { ["jobTitle"] = "Head of People" } });
        r.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task GetById_AdmitsPermissionHolders_AndRefusesRolesWithout()
    {
        var e = (await f.SeedAsync(NewTag(), 1)).Single();
        (await GetJson($"/api/employees/{e.Id}", f.Token("HR Director"))).Status.Should().Be(HttpStatusCode.OK);
        (await GetJson($"/api/employees/{e.Id}", f.Token("Employee"))).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetById_UnknownId_Is404_NotA500OrA403()
    {
        (await GetJson($"/api/employees/{int.MaxValue}", f.Token("HR Officer"))).Status.Should().Be(HttpStatusCode.NotFound);
        (await GetJson($"/api/employees/{int.MaxValue}", f.ScopedToken("HR Officer"))).Status.Should().Be(HttpStatusCode.NotFound,
            "an id that does not exist is the action's own 404 for a company-scoped caller too");
    }

    [Fact]
    public async Task GetById_SensitiveFieldsAreMasked_WithoutEmployeesSensitive()
    {
        var e = (await f.SeedAsync(NewTag(), 1, tweak: (x, _) => { x.IqamaNumber = "2123456789"; x.BankIban = "SA0380000000608010167519"; })).Single();
        // HR Officer holds no employees.sensitive; Payroll Officer does.
        var (_, plain) = await GetJson($"/api/employees/{e.Id}", f.Token("HR Officer"));
        var (_, full) = await GetJson($"/api/employees/{e.Id}", f.Token("Payroll Officer"));
        plain.GetRawText().Should().NotContain("2123456789").And.NotContain("SA0380000000608010167519");
        full.GetProperty("iqamaNumber").GetString().Should().Be("2123456789");
    }

    [Fact]
    public async Task List_AnotherTenantsCaller_SeesNothingOfThisTenant()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 3);
        var (status, body) = await GetJson($"/api/employees?search={tag}", f.OtherTenantToken("HR Officer"));
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("total").GetInt32().Should().Be(0);
        (await GetJson($"/api/employees/ex-employees?search={tag}", f.OtherTenantToken("HR Officer"))).Body.GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task EveryByIdRoute_RefusesAnotherTenantsEmployee_AndLeaksNothing()
    {
        var e = (await f.SeedAsync(NewTag(), 1, tweak: (x, _) => x.FullName = "Cross Tenant Secret")).Single();
        var other = f.OtherTenantToken("HR Manager");
        var calls = new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, $"/api/employees/{e.Id}", null),
            (HttpMethod.Get, $"/api/employees/{e.Id}/readiness", null),
            (HttpMethod.Get, $"/api/employee-access/{e.Id}", null),
            (HttpMethod.Put, $"/api/employees/{e.Id}", new { effectiveDate = "2026-11-01", changes = new Dictionary<string, object> { ["jobTitle"] = "x" } }),
            (HttpMethod.Delete, $"/api/employees/{e.Id}?reason=cross", null),
        };
        foreach (var (method, url, payload) in calls)
        {
            using var r = await f.Send(method, url, other, payload);
            ((int)r.StatusCode).Should().BeOneOf(new[] { 403, 404 }, $"{method} {url}");
            (await r.Content.ReadAsStringAsync()).Should().NotContain("Cross Tenant Secret");
        }
        (await f.StatusOf(e.Id)).Should().Be("Active", "the cross-tenant delete/update must not have touched the row");
        (await f.IsDeleted(e.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task List_CompanyScopedCaller_SeesOnlyTheirCompany_AndTotalsAgree()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 3, companyId: f.CompanyId, suffix: "own");
        await f.SeedAsync(tag, 2, companyId: f.OtherCompanyId, suffix: "far");
        var scoped = f.ScopedToken("HR Officer");

        var (_, scopedList) = await GetJson($"/api/employees?search={tag}&pageSize=2", scoped);
        var (_, groupList) = await GetJson($"/api/employees?search={tag}", f.Token("HR Officer"));

        groupList.GetProperty("total").GetInt32().Should().Be(5);
        scopedList.GetProperty("total").GetInt32().Should().Be(3, "total must count only the company the caller may see");
        scopedList.GetProperty("items").GetArrayLength().Should().Be(2);
        var (_, page2) = await GetJson($"/api/employees?search={tag}&pageSize=2&page=2", scoped);
        page2.GetProperty("items").GetArrayLength().Should().Be(1);
        scopedList.GetProperty("items").EnumerateArray().Concat(page2.GetProperty("items").EnumerateArray())
            .Select(r => r.GetProperty("employeeCode").GetString()).Should().OnlyContain(c => c!.Contains("own"));
    }

    [Fact]
    public async Task EveryByIdRoute_RefusesAnotherCompanysEmployee_ToACompanyScopedCaller()
    {
        var tag = NewTag();
        var far = (await f.SeedAsync(tag, 1, companyId: f.OtherCompanyId, suffix: "far")).Single();
        var scoped = f.ScopedToken("HR Officer");
        var calls = new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, $"/api/employees/{far.Id}", null),
            (HttpMethod.Get, $"/api/employees/{far.Id}/readiness", null),
            (HttpMethod.Get, $"/api/employee-access/{far.Id}", null),
            (HttpMethod.Put, $"/api/employees/{far.Id}", new { effectiveDate = "2026-11-01", changes = new Dictionary<string, object> { ["jobTitle"] = "x" } }),
        };
        foreach (var (method, url, payload) in calls)
        {
            using var r = await f.Send(method, url, scoped, payload);
            ((int)r.StatusCode).Should().BeOneOf(new[] { 403, 404 }, $"{method} {url}");
        }
    }

    [Fact]
    public async Task ExEmployees_ACompanyScopedCaller_SeesTheirCompanysSoftDeletedPeople()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 1, suffix: "del", tweak: (e, _) => { e.IsDeleted = true; e.DeletedAtUtc = DateTime.UtcNow; e.Status = "Inactive"; });
        (await GetJson($"/api/employees/ex-employees?search={tag}", f.Token("HR Officer"))).Body.GetProperty("total").GetInt32().Should().Be(1);
        (await GetJson($"/api/employees/ex-employees?search={tag}", f.ScopedToken("HR Officer"))).Body.GetProperty("total").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ExEmployees_AreScopedToo_AndRefuseRolesOutsideTheArchiveAudience()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 2, status: "Terminated", companyId: f.CompanyId, suffix: "own");
        await f.SeedAsync(tag, 3, status: "Terminated", companyId: f.OtherCompanyId, suffix: "far");

        (await GetJson($"/api/employees/ex-employees?search={tag}", f.ScopedToken("HR Officer"))).Body.GetProperty("total").GetInt32().Should().Be(2);
        (await GetJson($"/api/employees/ex-employees?search={tag}", f.Token("HR Officer"))).Body.GetProperty("total").GetInt32().Should().Be(5);
        (await GetJson("/api/employees/ex-employees", f.Token("Employee"))).Status.Should().Be(HttpStatusCode.Forbidden);
        (await GetJson("/api/employees/ex-employees", f.Token("Kiosk Operator"))).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory(Skip = "DEFECT (Low): GET /api/employees/ex-employees is declared [Authorize(Roles=\"Admin,HR Manager,HR Officer,Auditor\")] but PermissionAwareRolesAuthorizationHandler lets any holder of the endpoint's permission (employees.read) satisfy the role list, "
        + "so Recruiter, Finance, Payroll Officer, HR Assistant, Compliance Officer... read the former-staff archive (exit date, retention date, privacy status). The attribute documents an audience the runtime does not enforce.")]
    [InlineData("Recruiter")]
    [InlineData("Finance")]
    [InlineData("Payroll Officer")]
    [InlineData("HR Assistant")]
    [InlineData("Compliance Officer")]
    public async Task ExEmployees_AreRefusedToRolesOutsideTheDeclaredArchiveAudience(string role)
    {
        (await GetJson("/api/employees/ex-employees", f.Token(role))).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    // ═════════════════════════════ 6. Access filter, chip counts and the list agree ═════════════════════════════

    [Fact]
    public async Task AccessFilter_ListTotal_EqualsTheChipCount_ForEveryState()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 2, suffix: "none");                                                   // waiting_for_work_email
        await f.SeedAsync(tag, 1, suffix: "mail", tweak: (e, _) => e.WorkEmail = $"{tag.ToLower()}@dir.test");   // not_started
        await f.SeedAsync(tag, 1, suffix: "sus", status: "Suspended");                              // stopped
        await f.SeedAsync(tag, 1, suffix: "term", status: "Terminated");                            // stopped (leaver)
        var token = f.Token("HR Officer");

        var (_, summary) = await GetJson("/api/employee-access/summary", token);
        foreach (var state in new[] { "waiting_for_work_email", "not_started", "code_given", "active", "stopped", "blocked" })
        {
            var (_, list) = await GetJson($"/api/employees?access={state}&pageSize=1", token);
            var chip = summary.TryGetProperty(state, out var c) ? c.GetInt32() : 0;
            list.GetProperty("total").GetInt32().Should().Be(chip, $"the '{state}' chip and the list it filters must show the same number");
        }
        var (_, stopped) = await GetJson($"/api/employees?access=stopped&search={tag}&pageSize=100", token);
        stopped.GetProperty("total").GetInt32().Should().Be(2, "'Access stopped' includes the leaver, the other states do not");
    }

    [Fact]
    public async Task AccessFilter_AcceptsCommaSeparatedStates_AndRowsCarryTheirOwnState()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 1, suffix: "none");
        await f.SeedAsync(tag, 1, suffix: "mail", tweak: (e, _) => e.WorkEmail = $"{tag.ToLower()}@dir.test");
        await f.SeedAsync(tag, 1, suffix: "sus", status: "Suspended");
        var (_, body) = await GetJson($"/api/employees?search={tag}&access=not_started,waiting_for_work_email", f.Token("HR Officer"));
        body.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("accessState").GetString())
            .Should().BeEquivalentTo(new[] { "not_started", "waiting_for_work_email" });
    }

    [Fact]
    public async Task AccessFilter_AnUnknownState_ListsNobody_NotEveryone()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 2);
        var (status, body) = await GetJson($"/api/employees?search={tag}&access=bogus", f.Token("HR Officer"));
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("total").GetInt32().Should().Be(0, "a stale chip value must never widen into the whole directory");
    }

    // ═════════════════════════════ 7. Update (profile and work email) ═════════════════════════════

    [Fact]
    public async Task Update_WorkEmail_ShowsInTheListAndMovesTheAccessState()
    {
        var tag = NewTag();
        var e = (await f.SeedAsync(tag, 1)).Single();
        using var r = await f.Send(HttpMethod.Put, $"/api/employees/{e.Id}", f.Token("HR Officer"),
            new { effectiveDate = DateTime.UtcNow.ToString("yyyy-MM-dd"), changes = new Dictionary<string, object> { ["workEmail"] = $"{tag.ToLower()}@dir.test" } });
        r.StatusCode.Should().BeOneOf(new[] { HttpStatusCode.OK, HttpStatusCode.Accepted }, await r.Content.ReadAsStringAsync());

        var (_, list) = await GetJson($"/api/employees?search={tag}", f.Token("HR Officer"));
        var row = list.GetProperty("items").EnumerateArray().Single();
        row.GetProperty("workEmail").GetString().Should().Be($"{tag.ToLower()}@dir.test");
        row.GetProperty("accessState").GetString().Should().NotBe("waiting_for_work_email");
    }

    [Fact]
    public async Task Update_AnUnknownFieldKey_Is400_AndNothingIsApplied()
    {
        var e = (await f.SeedAsync(NewTag(), 1, tweak: (x, _) => x.JobTitle = "Before")).Single();
        using var r = await f.Send(HttpMethod.Put, $"/api/employees/{e.Id}", f.Token("HR Officer"),
            new { effectiveDate = "2026-11-01", changes = new Dictionary<string, object> { ["jobTitle"] = "After", ["notAField"] = "x" } });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await f.JobTitleOf(e.Id)).Should().Be("Before");
    }

    [Theory]
    [InlineData("Manager")]
    [InlineData("Auditor")]
    [InlineData("Employee")]
    public async Task Update_RefusesRolesWithoutEmployeesWrite(string role)
    {
        var e = (await f.SeedAsync(NewTag(), 1)).Single();
        using var r = await f.Send(HttpMethod.Put, $"/api/employees/{e.Id}", f.Token(role),
            new { effectiveDate = "2026-11-01", changes = new Dictionary<string, object> { ["jobTitle"] = "x" } });
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_RequiresEmployeesWrite_AndAnAnonymousCallerIs401()
    {
        using var anon = await f.Client.PostAsJsonAsync("/api/employees", new { englishName = "x", gender = "Male", manualEmployeeCode = false });
        anon.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        foreach (var role in new[] { "Auditor", "Manager", "Employee" })
        {
            using var r = await f.Send(HttpMethod.Post, "/api/employees", f.Token(role), new { englishName = "x", gender = "Male", manualEmployeeCode = false });
            r.StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }
    }

    [Fact]
    public async Task DeriveWorkEmail_AndDuplicateCheck_NeedEmployeesWrite()
    {
        foreach (var url in new[] { "/api/employees/derive-work-email", "/api/employees/duplicate-check" })
        {
            using var denied = await f.Send(HttpMethod.Post, url, f.Token("Auditor"), new { englishName = "Sara Test", identityValues = Array.Empty<object>() });
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
            using var allowed = await f.Send(HttpMethod.Post, url, f.Token("HR Officer"), new { englishName = "Sara Test", identityValues = Array.Empty<object>() });
            allowed.StatusCode.Should().Be(HttpStatusCode.OK, url + " " + await allowed.Content.ReadAsStringAsync());
        }
    }

    // ═════════════════════════════ 8. Employee access (reset sign-in, give access, work emails) ═════════════════════════════

    [Fact]
    public async Task EmployeeAccess_Reads_NeedEmployeesRead_Writes_NeedTheIssueKey()
    {
        var e = (await f.SeedAsync(NewTag(), 1)).Single();
        (await GetJson($"/api/employee-access/{e.Id}", f.Token("Employee"))).Status.Should().Be(HttpStatusCode.Forbidden);
        (await GetJson("/api/employee-access/summary", f.Token("Employee"))).Status.Should().Be(HttpStatusCode.Forbidden);
        using var anon = await f.Client.GetAsync($"/api/employee-access/{e.Id}");
        anon.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        foreach (var role in new[] { "Auditor", "Manager", "HR Director" })
        {
            using var codes = await f.Send(HttpMethod.Post, "/api/employee-access/codes", f.Token(role), new { employeeIds = new[] { e.Id } });
            codes.StatusCode.Should().Be(HttpStatusCode.Forbidden, role + " does not hold employees.access.issue");
            using var emails = await f.Send(HttpMethod.Post, "/api/employee-access/work-emails", f.Token(role), new { rows = Array.Empty<object>(), dryRun = true });
            emails.StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }
    }

    [Fact]
    public async Task EmployeeAccess_Codes_ForAnotherCompanysEmployee_AreSkippedNotIssued()
    {
        var tag = NewTag();
        var own = (await f.SeedAsync(tag, 1, suffix: "own", tweak: (x, _) => x.WorkEmail = $"own{Guid.NewGuid():N}@dir.test")).Single();
        var far = (await f.SeedAsync(tag, 1, companyId: f.OtherCompanyId, suffix: "far", tweak: (x, _) => x.WorkEmail = $"far{Guid.NewGuid():N}@dir.test")).Single();
        using var r = await f.Send(HttpMethod.Post, "/api/employee-access/codes", f.ScopedToken("HR Officer"), new { employeeIds = new[] { own.Id, far.Id }, delivery = "print" });
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("issued").EnumerateArray().Select(i => i.GetProperty("employeeId").GetInt32()).Should().NotContain(far.Id,
            "a code for a person outside the caller's companies must never be issued, and never printed");
        body.GetProperty("skipped").EnumerateArray().Select(i => i.GetProperty("employeeId").GetInt32()).Should().Contain(far.Id);
        body.GetProperty("skipped").EnumerateArray().Single(i => i.GetProperty("employeeId").GetInt32() == far.Id).GetProperty("reasonCode").GetString().Should().Be("not_found");
    }

    [Fact(Skip = "CONTRACT GAP (Low): when EVERY id sent is outside the caller's data scope the controller filters them all away and the service answers 400 employee_ids_required 'Choose at least one employee.', "
        + "whereas a mixed selection gets 200 with per-id skipped:not_found. The same mistake yields two different shapes.")]
    public async Task EmployeeAccess_Codes_WhenEveryIdIsOutOfScope_IsStillPerIdSkipped()
    {
        var far = (await f.SeedAsync(NewTag(), 1, companyId: f.OtherCompanyId, suffix: "far")).Single();
        using var r = await f.Send(HttpMethod.Post, "/api/employee-access/codes", f.ScopedToken("HR Officer"), new { employeeIds = new[] { far.Id } });
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EmployeeAccess_Codes_MoreThan500Ids_Is400_WithACode()
    {
        using var r = await f.Send(HttpMethod.Post, "/api/employee-access/codes", f.Token("HR Officer"), new { employeeIds = Enumerable.Range(1, 501).ToArray() });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await r.Content.ReadAsStringAsync()).Should().Contain("code");
    }

    [Fact]
    public async Task EmployeeAccess_WorkEmails_AnotherCompanysCode_LandsInNotFound_AndDryRunWritesNothing()
    {
        var tag = NewTag();
        var far = (await f.SeedAsync(tag, 1, companyId: f.OtherCompanyId, suffix: "far")).Single();
        using var r = await f.Send(HttpMethod.Post, "/api/employee-access/work-emails", f.ScopedToken("HR Officer"),
            new { rows = new[] { new { employeeCode = far.EmployeeCode, workEmail = $"{tag.ToLower()}@dir.test" } }, dryRun = false });
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("saved").GetInt32().Should().Be(0);
        body.GetProperty("notFound").EnumerateArray().Select(x => x.GetString()).Should().Contain(far.EmployeeCode);
        (await f.WorkEmailOf(far.Id)).Should().BeEmpty();
    }

    // ═════════════════════════════ 9. Bulk actions and export ═════════════════════════════

    private Task<HttpResponseMessage> Bulk(string token, object body) => f.Send(HttpMethod.Post, "/api/employees/bulk", token, body);

    [Fact]
    public async Task Bulk_EachActionNeedsItsOwnPermission()
    {
        var e = (await f.SeedAsync(NewTag(), 1)).Single();
        // HR Officer holds employees.write only; HR Manager holds approve and delete; Auditor holds none.
        using var activate = await Bulk(f.Token("HR Officer"), new { action = "activate", selectionMode = "ids", employeeIds = new[] { e.Id } });
        activate.StatusCode.Should().Be(HttpStatusCode.Forbidden, "activate needs employees.approve");
        using var delete = await Bulk(f.Token("HR Officer"), new { action = "delete", selectionMode = "ids", employeeIds = new[] { e.Id }, reason = "x" });
        delete.StatusCode.Should().Be(HttpStatusCode.Forbidden, "delete needs employees.delete");
        using var export = await Bulk(f.Token("Auditor"), new { action = "export", selectionMode = "ids", employeeIds = new[] { e.Id } });
        export.StatusCode.Should().Be(HttpStatusCode.Forbidden, "export needs employees.write, the same as the whole-people export");
        using var unknown = await Bulk(f.Token("HR Manager"), new { action = "explode", selectionMode = "ids", employeeIds = new[] { e.Id } });
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await f.StatusOf(e.Id)).Should().Be("Active");
    }

    [Fact]
    public async Task Bulk_AnonymousCaller_Is401()
    {
        using var r = await f.Client.PostAsJsonAsync("/api/employees/bulk", new { action = "export", selectionMode = "ids", employeeIds = new[] { 1 } });
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("""{"action":"export","selectionMode":"ids"}""")]                                            // ids mode without ids
    [InlineData("""{"action":"export","selectionMode":"ids","employeeIds":[]}""")]
    [InlineData("""{"action":"export","selectionMode":"allMatching","employeeIds":[1]}""")]                  // allMatching with ids
    [InlineData("""{"action":"export","selectionMode":"ids","employeeIds":[1],"filter":{"search":"x"}}""")]   // both
    [InlineData("""{"action":"export"}""")]                                                                  // no selection mode, never inferred
    [InlineData("""{"action":"deactivate","selectionMode":"ids","employeeIds":[1]}""")]                      // reason missing
    [InlineData("""{"action":"deactivate","selectionMode":"ids","employeeIds":[1],"reason":"r","targetStatus":"Terminated"}""")]  // never a terminate
    public async Task Bulk_InvalidRequests_Are400(string json)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/employees/bulk") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", f.Token("HR Manager"));
        using var r = await f.Client.SendAsync(req);
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Bulk_Ids_MoreThan5000_Is400()
    {
        using var r = await Bulk(f.Token("HR Manager"), new { action = "export", selectionMode = "ids", employeeIds = Enumerable.Range(1, 5001).ToArray() });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Bulk_Export_IsCsv_AndOnlyHoldsTheCallersScope_AndTenant()
    {
        var tag = NewTag();
        var own = (await f.SeedAsync(tag, 2, suffix: "own")).ToList();
        var far = (await f.SeedAsync(tag, 1, companyId: f.OtherCompanyId, suffix: "far", tweak: (x, _) => x.IqamaNumber = "2999999999")).Single();
        var foreign = (await f.SeedForeignTenantAsync(tag, "foreign")).Single();
        var ids = own.Select(x => x.Id).Concat(new[] { far.Id, foreign.Id }).ToArray();

        using var r = await Bulk(f.ScopedToken("HR Officer"), new { action = "export", selectionMode = "ids", employeeIds = ids });

        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        r.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var csv = await r.Content.ReadAsStringAsync();
        csv.Should().Contain(own[0].EmployeeCode).And.Contain(own[1].EmployeeCode);
        csv.Should().NotContain(far.EmployeeCode, "another company's employee is outside the scoped caller");
        csv.Should().NotContain(foreign.EmployeeCode, "another tenant's employee must never be exported").And.NotContain("2999999999");
    }

    [Fact]
    public async Task Bulk_Export_WithoutEmployeesSensitive_LeavesSensitiveColumnsEmpty()
    {
        var e = (await f.SeedAsync(NewTag(), 1, tweak: (x, _) => { x.IqamaNumber = "2123456780"; x.Phone = "0551230000"; })).Single();
        using var plain = await Bulk(f.Token("HR Officer"), new { action = "export", selectionMode = "ids", employeeIds = new[] { e.Id } });
        (await plain.Content.ReadAsStringAsync()).Should().NotContain("2123456780").And.NotContain("0551230000");
        using var full = await Bulk(f.Token("Admin"), new { action = "export", selectionMode = "ids", employeeIds = new[] { e.Id } });
        (await full.Content.ReadAsStringAsync()).Should().Contain(e.EmployeeCode);
    }

    [Fact]
    public async Task Bulk_AllMatching_SelectsExactlyWhatTheListShows_ForSearchStatusAndReadiness()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 3, suffix: "d", status: "Draft", tweak: (e, _) => e.ReadinessState = "NeedsAttention");
        await f.SeedAsync(tag, 2, suffix: "a", status: "Active", tweak: (e, _) => e.ReadinessState = "Ready");
        var token = f.Token("HR Manager");

        var (_, list) = await GetJson($"/api/employees?search={tag}&status=Draft&readiness=NeedsAttention&pageSize=1", token);
        using var export = await Bulk(token, new { action = "export", selectionMode = "allMatching", filter = new { search = tag, status = "Draft", readiness = "NeedsAttention" } });

        list.GetProperty("total").GetInt32().Should().Be(3);
        var lines = (await export.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Count(l => l.Contains(tag)).Should().Be(3);
    }

    [Fact(Skip = "DEFECT (Low): the bulk 'select all matching' filter cannot express the list's access filter on the client (BulkSelectAllFilter in api/employees.ts has no `access` although the server record has one), "
        + "and when it is sent the server resolves a different set: the list's access=stopped includes former employees, ResolveTargetIdsAsync never does. "
        + "The page works around it by hiding select-all while a chip is on (canOfferSelectAllMatching && !accessFilter); a destructive bulk with expectedCount=list total would 409 selection_changed.")]
    public async Task Bulk_AllMatching_WithAnAccessFilter_ResolvesTheSameSetTheListShows()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 1, suffix: "sus", status: "Suspended");
        await f.SeedAsync(tag, 1, suffix: "term", status: "Terminated");
        var token = f.Token("HR Manager");
        var (_, list) = await GetJson($"/api/employees?search={tag}&access=stopped&pageSize=1", token);
        using var export = await Bulk(token, new { action = "export", selectionMode = "allMatching", filter = new { search = tag, access = "stopped" } });
        var rows = (await export.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Contains(tag));
        rows.Should().Be(list.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Bulk_AllMatching_Delete_NeedsExpectedCount_409sOnDrift_AndIsIdempotentOnRetry()
    {
        var tag = NewTag();
        var made = (await f.SeedAsync(tag, 3)).ToList();
        var token = f.Token("HR Manager");
        var filter = new { search = tag };

        using var noCount = await Bulk(token, new { action = "delete", selectionMode = "allMatching", filter, reason = "cleanup" });
        noCount.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a destructive select-all without expectedCount is refused");

        using var drift = await Bulk(token, new { action = "delete", selectionMode = "allMatching", filter, reason = "cleanup", expectedCount = 5 });
        drift.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var driftBody = await drift.Content.ReadFromJsonAsync<JsonElement>();
        driftBody.GetProperty("error").GetString().Should().Be("selection_changed");
        driftBody.GetProperty("expected").GetInt32().Should().Be(5);
        driftBody.GetProperty("actual").GetInt32().Should().Be(3);
        (await f.IsDeleted(made[0].Id)).Should().BeFalse("a 409 must not have deleted anything");

        using var ok = await Bulk(token, new { action = "delete", selectionMode = "allMatching", filter, reason = "cleanup", expectedCount = 3 });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        var result = await ok.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("succeeded").GetInt32().Should().Be(3);
        foreach (var field in new[] { "action", "requested", "succeeded", "skipped", "failed", "items", "summary" }) result.TryGetProperty(field, out _).Should().BeTrue(field);
        (await GetJson($"/api/employees?search={tag}", token)).Body.GetProperty("total").GetInt32().Should().Be(0);

        // A double click / retry of the same request: the rows are gone from the population, so the count drifts to 0 and nothing is touched twice.
        using var retry = await Bulk(token, new { action = "delete", selectionMode = "allMatching", filter, reason = "cleanup", expectedCount = 3 });
        retry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var retryIds = await Bulk(token, new { action = "delete", selectionMode = "ids", employeeIds = made.Select(m => m.Id).ToArray(), reason = "cleanup" });
        retryIds.StatusCode.Should().Be(HttpStatusCode.OK);
        var again = await retryIds.Content.ReadFromJsonAsync<JsonElement>();
        again.GetProperty("succeeded").GetInt32().Should().Be(0);
        again.GetProperty("items").EnumerateArray().Should().OnlyContain(i => i.GetProperty("outcome").GetString() == "skipped" && i.GetProperty("reason").GetString() == "forbidden");
    }

    [Fact]
    public async Task Bulk_Deactivate_ByIds_OutOfScopeIdsAreSkipped_AndNeverActedOn()
    {
        var tag = NewTag();
        var own = (await f.SeedAsync(tag, 1, suffix: "own")).Single();
        var far = (await f.SeedAsync(tag, 1, companyId: f.OtherCompanyId, suffix: "far")).Single();
        var foreign = (await f.SeedForeignTenantAsync(tag, "foreign")).Single();
        // A company-scoped HR Manager would be ideal; the seeded scoped caller is an HR Officer (employees.write), which is what deactivate needs.
        using var r = await Bulk(f.ScopedToken("HR Officer"), new { action = "deactivate", selectionMode = "ids", employeeIds = new[] { own.Id, far.Id, foreign.Id }, reason = "scope test", targetStatus = "Suspended" });

        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("requested").GetInt32().Should().Be(3);
        body.GetProperty("succeeded").GetInt32().Should().Be(1);
        body.GetProperty("skipped").GetInt32().Should().Be(2);
        (await f.StatusOf(own.Id)).Should().Be("Suspended");
        (await f.StatusOf(far.Id)).Should().Be("Active");
        (await f.StatusOfForeign(foreign.Id)).Should().Be("Active");
    }

    // ═════════════════════════════ 10. Import, template, export routes ═════════════════════════════

    [Fact]
    public async Task ImportPreview_Import_AndTheCsvDownloads_NeedTheirPermissions()
    {
        var csv = "EmployeeCode,FullName,JoiningDate\nX1,Test One,2026-01-01\n";
        foreach (var role in new[] { "Auditor", "Manager", "Employee", "Payroll Officer" })
        {
            using var preview = await f.Send(HttpMethod.Post, "/api/employees/import-preview", f.Token(role), new { csvContent = csv });
            preview.StatusCode.Should().Be(HttpStatusCode.Forbidden, role + " preview");
            using var import = await f.Send(HttpMethod.Post, "/api/employees/import", f.Token(role), new { csvContent = csv });
            import.StatusCode.Should().Be(HttpStatusCode.Forbidden, role + " import");
            using var export = await f.Send(HttpMethod.Get, "/api/employees/export", f.Token(role));
            export.StatusCode.Should().Be(HttpStatusCode.Forbidden, role + " export");
            using var template = await f.Send(HttpMethod.Get, "/api/employees/import-template", f.Token(role));
            template.StatusCode.Should().Be(HttpStatusCode.Forbidden, role + " template");
        }
    }

    [Fact]
    public async Task Template_AndExport_AreCsvDownloads_ForHrRoles_AndTheTemplateHeaderIsTheImportersHeader()
    {
        var e = (await f.SeedAsync(NewTag(), 1)).Single();
        using var template = await f.Send(HttpMethod.Get, "/api/employees/import-template", f.Token("HR Officer"));
        template.StatusCode.Should().Be(HttpStatusCode.OK);
        template.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var templateHeader = (await template.Content.ReadAsStringAsync()).TrimStart('﻿').Split('\n')[0].Trim();

        using var export = await f.Send(HttpMethod.Get, "/api/employees/export", f.Token("HR Officer"));
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        export.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var exportBody = (await export.Content.ReadAsStringAsync()).TrimStart('﻿');
        exportBody.Split('\n')[0].Trim().Should().Be(templateHeader, "an exported file must be re-importable: the header is the template's header");
        exportBody.Should().Contain(e.EmployeeCode);
    }

    [Fact]
    public async Task Import_ANonGuidImportKey_Is400_NotA500()
    {
        // The screen sends importKey as a string; the server binds a Guid. A key that is not a UUID is a model-state 400.
        using var content = new StringContent("""{"csvContent":"EmployeeCode,FullName,JoiningDate\nX1,Test One,2026-01-01\n","importKey":"not-a-uuid"}""", System.Text.Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/employees/import") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", f.Token("HR Officer"));
        using var r = await f.Client.SendAsync(req);
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ═════════════════════════════ 7. resolve-duplicate: merge and unmerge need employees.write, as before ═════════════════════════════

    private async Task<(Employee Duplicate, Employee Survivor)> Twins()
    {
        var tag = NewTag();
        var made = await f.SeedAsync(tag, 2);
        return (made[0], made[1]);
    }

    private Task<HttpResponseMessage> Merge(string role, Employee duplicate, Employee survivor) =>
        f.Send(HttpMethod.Post, $"/api/employees/{duplicate.Id}/resolve-duplicate", f.Token(role), new { resolution = "merge", intoEmployeeId = survivor.Id, reason = "same person" });

    private Task<HttpResponseMessage> Unmerge(string role, Employee duplicate) =>
        f.Send(HttpMethod.Post, $"/api/employees/{duplicate.Id}/resolve-duplicate", f.Token(role), new { resolution = "unmerge", reason = "wrong twins" });

    [Fact]
    public async Task ResolveDuplicate_Merge_ByTheWriteOnlyRole_Merges()
    {
        var (dup, survivor) = await Twins();
        using var r = await Merge("HR Officer", dup, survivor);
        r.StatusCode.Should().Be(HttpStatusCode.OK, "owner decision: merge needs employees.write, as it did before the delete-permission refusal: " + await r.Content.ReadAsStringAsync());
        (await f.IsDeleted(dup.Id)).Should().BeTrue();
        (await f.IsDeleted(survivor.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task ResolveDuplicate_Unmerge_ByTheWriteOnlyRole_Restores()
    {
        var (dup, survivor) = await Twins();
        using (var merged = await Merge("HR Officer", dup, survivor)) merged.StatusCode.Should().Be(HttpStatusCode.OK);
        using var r = await Unmerge("HR Officer", dup);
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        (await f.IsDeleted(dup.Id)).Should().BeFalse();
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("HR Director")]
    [InlineData("HR Manager")]
    [InlineData("HR Officer")]
    public async Task ResolveDuplicate_Merge_ByTheRolesHoldingWrite_Merges(string role)
    {
        var (dup, survivor) = await Twins();
        using var r = await Merge(role, dup, survivor);
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        (await f.IsDeleted(dup.Id)).Should().BeTrue();
        (await f.IsDeleted(survivor.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task ResolveDuplicate_MergeNeedsExactlyEmployeesWrite_ForEverySeededRole()
    {
        // The expectation is computed from the seeded bundles, so a future change to a role's grants cannot silently move the door.
        var held = await f.PermissionsByRoleAsync();
        foreach (var role in EmployeeDirectoryHttpFixture.Roles)
        {
            var (dup, survivor) = await Twins();
            using var merge = await Merge(role, dup, survivor);
            var mayMerge = held[role].Contains("employees.write");
            (merge.StatusCode == HttpStatusCode.OK).Should().Be(mayMerge, $"{role}: merge reached {(int)merge.StatusCode}");
            if (!mayMerge) merge.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
            (await f.IsDeleted(dup.Id)).Should().Be(mayMerge, $"{role}: the duplicate is removed exactly when the merge succeeded");
        }
    }

    [Fact]
    public async Task ResolveDuplicate_Distinct_StaysAtWriteLevel()
    {
        var (dup, _) = await Twins();
        using var r = await f.Send(HttpMethod.Post, $"/api/employees/{dup.Id}/resolve-duplicate", f.Token("HR Officer"), new { resolution = "distinct", reason = "different people" });
        r.StatusCode.Should().Be(HttpStatusCode.OK, "clearing a duplicate flag deletes nothing; its permission is unchanged");
        (await f.IsDeleted(dup.Id)).Should().BeFalse();
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static IEnumerable<string> TsFields(string interfaceName, string file = "api/employees.ts")
    {
        var text = File.ReadAllText(Path.Combine(RepoFiles.Root(), "frontend", "src", file));
        var m = Regex.Match(text, @"export interface " + interfaceName + @"(?: extends (\w+))? \{(.*?)\n\}", RegexOptions.Singleline);
        m.Success.Should().BeTrue(interfaceName + " must exist in " + file);
        var fields = new List<string>();
        foreach (var line in m.Groups[2].Value.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("*") || t.StartsWith("/")) continue;
            var fm = Regex.Match(t, @"^(?:readonly )?(\w+)\??:");
            if (fm.Success) fields.Add(fm.Groups[1].Value);
        }
        if (m.Groups[1].Success && m.Groups[1].Value != "EmployeeEntity") fields.AddRange(TsFields(m.Groups[1].Value, file));
        return fields.Distinct();
    }
}

internal static class RepoFiles
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend-dotnet", "Zayra.Api"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}

/// <summary>Real Program.cs pipeline over SQLite with the SEEDED role bundles; one group-scoped user per role in tenant A, a company-scoped HR Officer, and an HR user in tenant B.</summary>
public sealed class EmployeeDirectoryHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid OtherTenantId { get; } = Guid.NewGuid();
    public Guid CompanyId { get; } = Guid.NewGuid();
    public Guid OtherCompanyId { get; } = Guid.NewGuid();
    public Guid OtherTenantCompanyId { get; } = Guid.NewGuid();

    public static readonly string[] Roles =
    {
        "Admin", "HR Director", "HR Manager", "HR Officer", "Payroll Manager", "Payroll Officer", "Finance", "Finance Approver", "Compliance Officer",
        "Manager", "Supervisor", "Recruiter", "HR Assistant", "Auditor", "Kiosk Operator", "Employee",
    };

    public string Token(string role) => _tokens[role];
    public string ScopedToken(string role) => _tokens["scoped:" + role];
    public string OtherTenantToken(string role) => _tokens["other:" + role];

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:emp-dir-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        // Several tests create dozens of employees as one role in seconds; the per-user create limit (10/min) is exercised by HireIntegrityHttpTests.
        Host = new AuthorizationPipelineHost(connection, configuration: new Dictionary<string, string?>
        {
            ["RateLimit:Employees:employee-create:PerUser"] = "10000",
            ["RateLimit:Employees:employee-create:PerTenant"] = "100000",
        });
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();

        foreach (var (tenantId, name) in new[] { (TenantId, "Directory A"), (OtherTenantId, "Directory B") })
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = name, Slug = $"dir-{tenantId:N}", IsActive = true });
            db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenantId, Plan = "Enterprise", Status = "Active", MaxEmployees = 100_000 });
        }
        db.Companies.Add(new Company { Id = CompanyId, TenantId = TenantId, LegalNameEn = "Dir One", CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR" });
        db.Companies.Add(new Company { Id = OtherCompanyId, TenantId = TenantId, LegalNameEn = "Dir Two", CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR" });
        db.Companies.Add(new Company { Id = OtherTenantCompanyId, TenantId = OtherTenantId, LegalNameEn = "Dir Foreign", CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR" });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);
        await new AuthSeeder(db).EnsureTenantRolesAsync(OtherTenantId, CancellationToken.None);

        foreach (var role in Roles) _tokens[role] = await MintAsync(scope, TenantId, role, null, role);
        _tokens["scoped:HR Officer"] = await MintAsync(scope, TenantId, "HR Officer", CompanyId, "scoped-HR Officer");
        _tokens["scoped:HR Manager"] = await MintAsync(scope, TenantId, "HR Manager", CompanyId, "scoped-HR Manager");
        _tokens["other:HR Officer"] = await MintAsync(scope, OtherTenantId, "HR Officer", null, "other-HR Officer");
        _tokens["other:HR Manager"] = await MintAsync(scope, OtherTenantId, "HR Manager", null, "other-HR Manager");
    }

    private static async Task<string> MintAsync(IServiceScope scope, Guid tenantId, string role, Guid? scopedCompany, string key)
    {
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var email = $"{key.Replace(' ', '-')}-{tenantId:N}@dir.local";
        var user = new User
        {
            TenantId = tenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = key, PasswordHash = "no-login-path",
            Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = scopedCompany is null,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var roleRow = await db.Roles.SingleAsync(r => r.TenantId == tenantId && r.Name == role);
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = roleRow.Id });
        if (scopedCompany is Guid cid) db.Set<UserEntityAccess>().Add(new UserEntityAccess { TenantId = tenantId, UserId = user.Id, CompanyId = cid, Role = role });
        await db.SaveChangesAsync();

        var loaded = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.Id == user.Id);
        var grants = loaded.EntityAccesses.Where(e => e.IsActive).Select(e => new EntityAccessGrant(e.CompanyId, e.Role, e.GrantMode)).ToArray();
        return scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(loaded, AuthService.GetRoles(loaded), AuthService.GetPermissions(loaded),
            loaded.Tenant!, grants, EntityScopeClaims.Resolve(loaded.IsGroupScope, grants, Array.Empty<Guid>()), out _);
    }

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string bearer, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request);
    }

    /// <summary>The permission keys each SEEDED role holds in tenant A (what a customer's user gets from the role alone).</summary>
    public async Task<Dictionary<string, HashSet<string>>> PermissionsByRoleAsync()
    {
        using var scope = Host.Services.CreateScope();
        var rows = await Db(scope).Set<RolePermission>().AsNoTracking().Where(rp => rp.Role!.TenantId == TenantId).Select(rp => new { rp.Role!.Name, rp.Permission!.Key }).ToListAsync();
        return rows.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.Select(x => x.Key).ToHashSet());
    }

    private ZayraDbContext Db(IServiceScope s) => s.ServiceProvider.GetRequiredService<ZayraDbContext>();

    /// <summary>Runs one piece of direct database work (seeding something the API cannot create, or reading back what it left) in its own scope.</summary>
    public async Task<T> WithDbAsync<T>(Func<ZayraDbContext, Task<T>> body)
    {
        using var scope = Host.Services.CreateScope();
        return await body(Db(scope));
    }

    public async Task<List<Employee>> SeedAsync(string tag, int count, string status = "Active", Guid? companyId = null, string suffix = "", Action<Employee, int>? tweak = null)
    {
        using var scope = Host.Services.CreateScope();
        var db = Db(scope);
        var made = new List<Employee>();
        for (var i = 1; i <= count; i++)
        {
            var code = $"{tag}-{suffix}{i:D3}";
            var e = new Employee
            {
                TenantId = TenantId, CompanyId = companyId ?? CompanyId, EmployeeCode = code, FullName = $"Dir {code}", EnglishName = $"Dir {code}",
                Status = status, JoiningDate = DateTime.UtcNow.Date, ReadinessState = "Ready",
            };
            tweak?.Invoke(e, i);
            made.Add(e);
        }
        db.Employees.AddRange(made);
        await db.SaveChangesAsync();
        return made;
    }

    public async Task<List<Employee>> SeedForeignTenantAsync(string tag, string suffix)
    {
        using var scope = Host.Services.CreateScope();
        var db = Db(scope);
        var e = new Employee { TenantId = OtherTenantId, CompanyId = OtherTenantCompanyId, EmployeeCode = $"{tag}-{suffix}", FullName = $"Foreign {tag}", EnglishName = $"Foreign {tag}", Status = "Active", JoiningDate = DateTime.UtcNow.Date };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        return new List<Employee> { e };
    }

    public async Task<string> StatusOf(int id) { using var s = Host.Services.CreateScope(); return await Db(s).Employees.IgnoreQueryFilters().Where(e => e.Id == id).Select(e => e.Status).SingleAsync(); }
    public async Task<string> StatusOfForeign(int id) => await StatusOf(id);
    public async Task<bool> IsDeleted(int id) { using var s = Host.Services.CreateScope(); return await Db(s).Employees.IgnoreQueryFilters().Where(e => e.Id == id).Select(e => e.IsDeleted).SingleAsync(); }
    public async Task<string> JobTitleOf(int id) { using var s = Host.Services.CreateScope(); return await Db(s).Employees.IgnoreQueryFilters().Where(e => e.Id == id).Select(e => e.JobTitle).SingleAsync(); }
    public async Task<string> WorkEmailOf(int id) { using var s = Host.Services.CreateScope(); return await Db(s).Employees.IgnoreQueryFilters().Where(e => e.Id == id).Select(e => e.WorkEmail ?? "").SingleAsync(); }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
