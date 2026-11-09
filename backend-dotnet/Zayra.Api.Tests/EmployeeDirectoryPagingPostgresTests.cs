using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The People list and the Ex-Employees archive (<c>GET /api/employees</c>, <c>/ex-employees</c>) and the bulk "select all matching"
/// resolver on a REAL PostgreSQL. In-memory and SQLite hide exactly what matters here: OFFSET semantics, the translation of the
/// access-state id list (<c>keep.Contains</c>), the <c>IgnoreQueryFilters</c> archive query and its COALESCE ordering.
/// Both scope branches (group-level service path, company-scoped hand-written path) are driven and must agree.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeDirectoryPagingPostgresTests
{
    private readonly PostgresFixture _fx;
    public EmployeeDirectoryPagingPostgresTests(PostgresFixture fx) => _fx = fx;

    private sealed record Seed(Guid TenantId, Guid CompanyId, Guid OtherCompanyId);

    private async Task<Seed> SeedAsync(int current, int leavers = 0, int otherCompany = 0)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Dir PG", CountryCode = "SAU", Jurisdiction = "KSA-mainland", RegistrationNumber = $"D-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        var other = new Company { Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Dir PG Two", CountryCode = "SAU", Jurisdiction = "KSA-mainland", RegistrationNumber = $"D-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        db.Companies.AddRange(company, other);
        await db.SaveChangesAsync();
        var rows = new List<Employee>();
        for (var i = 1; i <= current; i++) rows.Add(Row(tenantId, company.Id, $"C-{i:D4}", "Active"));
        for (var i = 1; i <= otherCompany; i++) rows.Add(Row(tenantId, other.Id, $"O-{i:D4}", "Active"));
        for (var i = 1; i <= leavers; i++)
        {
            var e = Row(tenantId, company.Id, $"X-{i:D4}", i % 2 == 0 ? "Terminated" : "Archived");
            e.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-i);
            rows.Add(e);
        }
        db.Employees.AddRange(rows);
        await db.SaveChangesAsync();
        return new Seed(tenantId, company.Id, other.Id);
    }

    private static Employee Row(Guid tenantId, Guid companyId, string code, string status) => new()
    {
        TenantId = tenantId, CompanyId = companyId, EmployeeCode = code, FullName = $"PG {code}", EnglishName = $"PG {code}",
        Status = status, ReadinessState = "Ready", JoiningDate = DateTime.SpecifyKind(new DateTime(2025, 1, 1), DateTimeKind.Utc),
    };

    private EmployeesController Controller(ZayraDbContext db, Seed seed, bool companyScoped, params string[] permissions)
    {
        var controller = new EmployeesController(db, new Pbkdf2PasswordHasher(), new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db),
            new DirPgHijri(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new DirPgLetters());
        var claims = new List<Claim>
        {
            new("tenant_id", seed.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, "Admin"),
            new("permission", "employees.read"), new("permission", "employees.write"),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        claims.Add(companyScoped
            ? new Claim(EntityScopeContext.V2ClaimType, System.Text.Json.JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { seed.CompanyId.ToString() } }))
            : new Claim("is_group_scope", "true"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };
        return controller;
    }

    private static EmployeeManagementService Service(ZayraDbContext db) => new(db, new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db));

    private static async Task<PagedResult<EmployeeListItemDto>> ListAsync(EmployeesController c, ZayraDbContext db, int page, int pageSize, string? status = null, string? access = null)
    {
        var result = await c.Search(Service(db), null, status, null, null, null, null, page, pageSize, CancellationToken.None, access);
        return Assert.IsType<PagedResult<EmployeeListItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task List_PagesOf100_CoverAllRowsOnce_AndTheCountCallReportsTheSameTotal(bool companyScoped)
    {
        var seed = await SeedAsync(current: 105, leavers: 4, otherCompany: companyScoped ? 6 : 0);
        await using var db = _fx.CreateDb();
        var c = Controller(db, seed, companyScoped);

        var p1 = await ListAsync(c, db, 1, 100);
        var p2 = await ListAsync(c, db, 2, 100);
        var count = await ListAsync(c, db, 1, 1);
        var beyond = await ListAsync(c, db, 3, 100);

        p1.Total.Should().Be(105, "leavers and (for the scoped caller) the other company are not in the list");
        p1.Items.Should().HaveCount(100);
        p2.Items.Should().HaveCount(5);
        count.Items.Should().HaveCount(1);
        count.Total.Should().Be(105, "the 'Everyone' count call (page=1, pageSize=1) must report the full total");
        beyond.Items.Should().BeEmpty();
        beyond.Total.Should().Be(105);
        p1.Items.Concat(p2.Items).Select(x => x.EmployeeCode).Should().OnlyHaveUniqueItems().And.HaveCount(105).And.BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public async Task List_NonPositivePage_IsPageOne_OnPostgres(int page, int expectedPage)
    {
        var seed = await SeedAsync(current: 3);
        await using var db = _fx.CreateDb();
        var result = await ListAsync(Controller(db, seed, companyScoped: false), db, page, 2);
        result.Page.Should().Be(expectedPage);
        result.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task List_AnAbsurdlyHighPage_IsAnEmptyPage_NotAnError()
    {
        var seed = await SeedAsync(current: 3);
        await using var db = _fx.CreateDb();
        var result = await ListAsync(Controller(db, seed, companyScoped: false), db, int.MaxValue, 100);
        result.Items.Should().BeEmpty();
        result.Total.Should().Be(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task List_AccessFilterStopped_TranslatesOnPostgres_AndIncludesLeaversOnly_ForStopped(bool companyScoped)
    {
        var seed = await SeedAsync(current: 4, leavers: 3);
        await using (var db = _fx.CreateDb())
        {
            var one = await db.Employees.Where(e => e.TenantId == seed.TenantId && e.EmployeeCode == "C-0001").SingleAsync();
            one.Status = "Suspended";
            await db.SaveChangesAsync();
        }
        await using var read = _fx.CreateDb();
        var c = Controller(read, seed, companyScoped);

        var stopped = await ListAsync(c, read, 1, 100, access: "stopped");
        var waiting = await ListAsync(c, read, 1, 100, access: "waiting_for_work_email");

        stopped.Total.Should().Be(4, "one suspended current employee and three leavers");
        stopped.Items.Select(i => i.AccessState).Should().OnlyContain(s => s == "stopped");
        waiting.Total.Should().Be(3, "the three still-active employees without a work email; leavers and the suspended one are not 'waiting'");
    }

    [Fact]
    public async Task ExEmployees_PageWindowAndOrder_TranslateOnPostgres_AndScopeIsHonoured()
    {
        var seed = await SeedAsync(current: 2, leavers: 7, otherCompany: 3);
        await using (var db = _fx.CreateDb())
        {
            // The other company's leaver. (Soft-deleted rows are covered by the skipped HTTP test ExEmployees_ACompanyScopedCaller_SeesTheirCompanysSoftDeletedPeople.)
            db.Employees.Add(Row(seed.TenantId, seed.OtherCompanyId, "OX-0001", "Terminated"));
            await db.SaveChangesAsync();
        }
        await using var read = _fx.CreateDb();

        async Task<PagedResult<ExEmployeeListItemDto>> Archive(bool scoped, int page, int pageSize)
        {
            var res = await Controller(read, seed, scoped).ExEmployees(null, null, page, pageSize, CancellationToken.None);
            return Assert.IsType<PagedResult<ExEmployeeListItemDto>>(Assert.IsType<OkObjectResult>(res.Result).Value);
        }

        var group = await Archive(false, 1, 100);
        var scoped = await Archive(true, 1, 100);
        group.Total.Should().Be(8, "seven leavers + the other company's leaver");
        scoped.Total.Should().Be(7, "the other company's leaver is outside the scoped caller");
        scoped.Items.Select(i => i.EmployeeCode).Should().NotContain("OX-0001");

        var first = await Archive(false, 1, 4);
        var second = await Archive(false, 2, 4);
        var third = await Archive(false, 3, 4);
        first.Items.Concat(second.Items).Concat(third.Items).Select(i => i.EmployeeCode).Should().OnlyHaveUniqueItems().And.HaveCount(8);
        (await Archive(false, 4, 4)).Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExEmployees_AnAbsurdlyHighPage_IsAnEmptyPage_NotAnError(bool companyScoped)
    {
        var seed = await SeedAsync(current: 1, leavers: 3);
        await using var db = _fx.CreateDb();
        var res = await Controller(db, seed, companyScoped).ExEmployees(null, null, int.MaxValue, 100, CancellationToken.None);
        var page = Assert.IsType<PagedResult<ExEmployeeListItemDto>>(Assert.IsType<OkObjectResult>(res.Result).Value);
        page.Items.Should().BeEmpty();
        page.Total.Should().Be(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task List_AnAbsurdlyHighPage_WithTheScopedAndAccessFilters_IsAnEmptyPage_NotAnError(bool companyScoped)
    {
        var seed = await SeedAsync(current: 3, leavers: 2);
        await using var db = _fx.CreateDb();
        var result = await ListAsync(Controller(db, seed, companyScoped), db, int.MaxValue, 100, access: "stopped");
        result.Items.Should().BeEmpty();
        result.Total.Should().Be(2, "the two leavers are the only 'stopped' people");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredAndDeceased_AreInTheArchive_ButResignedNoticePeriodAndAbsconded_AreStillInTheCurrentList(bool companyScoped)
    {
        // A resigned employee works, is paid and punches in through the notice period, so Resigned is a CURRENT status (as is the separate
        // "Notice period"); Retired and Deceased are over and belong to the archive.
        var seed = await SeedAsync(current: 2);
        await using (var db = _fx.CreateDb())
        {
            db.Employees.AddRange(
                Row(seed.TenantId, seed.CompanyId, "R-0001", "Resigned"), Row(seed.TenantId, seed.CompanyId, "R-0002", "Retired"),
                Row(seed.TenantId, seed.CompanyId, "R-0003", "Deceased"),
                Row(seed.TenantId, seed.CompanyId, "N-0001", "Notice period"), Row(seed.TenantId, seed.CompanyId, "N-0002", "Absconded"));
            await db.SaveChangesAsync();
        }
        await using var read = _fx.CreateDb();
        var c = Controller(read, seed, companyScoped);

        var current = await ListAsync(c, read, 1, 100);
        var resignedFilter = await ListAsync(c, read, 1, 100, status: "Resigned");
        var archive = Assert.IsType<PagedResult<ExEmployeeListItemDto>>(Assert.IsType<OkObjectResult>((await c.ExEmployees(null, null, 1, 100, CancellationToken.None)).Result).Value);
        var archiveResigned = Assert.IsType<PagedResult<ExEmployeeListItemDto>>(Assert.IsType<OkObjectResult>((await c.ExEmployees(null, "Resigned", 1, 100, CancellationToken.None)).Result).Value);

        current.Items.Select(i => i.EmployeeCode).Should().BeEquivalentTo(new[] { "C-0001", "C-0002", "R-0001", "N-0001", "N-0002" });
        resignedFilter.Items.Select(i => i.EmployeeCode).Should().BeEquivalentTo(new[] { "R-0001" }, "a Resigned person is in the current list, so filtering it by Resigned finds them");
        archive.Items.Select(i => i.EmployeeCode).Should().BeEquivalentTo(new[] { "R-0002", "R-0003" });
        archiveResigned.Total.Should().Be(0, "a Resigned person is not in the archive");

        var export = await c.BulkAction(new EmployeesController.BulkActionRequest("export", "allMatching", null, new EmployeesController.BulkSelectAllFilter(null, null, null, null, null), null, null, null), Service(read), CancellationToken.None);
        var csv = System.Text.Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(export).FileContents);
        csv.Should().NotContain("R-0002").And.NotContain("R-0003", "the bulk resolver acts on the same current population the list shows");
        csv.Should().Contain("R-0001").And.Contain("N-0001");
    }

    [Fact]
    public async Task ExEmployees_ACompanyScopedCaller_SeesExactlyTheirScopesSoftDeletedPeople_AndNeverAnotherCompanysOrAnUnassigned()
    {
        var seed = await SeedAsync(current: 2, leavers: 1, otherCompany: 1);
        await using (var db = _fx.CreateDb())
        {
            Employee Deleted(Guid? companyId, string code, string status)
            {
                var e = Row(seed.TenantId, companyId ?? Guid.Empty, code, status);
                e.CompanyId = companyId;
                e.IsDeleted = true;
                e.DeletedAtUtc = DateTime.UtcNow;
                return e;
            }
            db.Employees.AddRange(
                Deleted(seed.CompanyId, "D-OWN", "Active"),
                Deleted(seed.CompanyId, "D-OWN2", "Inactive"),
                Deleted(seed.OtherCompanyId, "D-FAR", "Active"),
                Deleted(null, "D-NONE", "Active"));
            await db.SaveChangesAsync();
        }
        await using var read = _fx.CreateDb();

        async Task<string[]> Codes(bool scoped)
        {
            var res = await Controller(read, seed, scoped).ExEmployees(null, null, 1, 100, CancellationToken.None);
            return Assert.IsType<PagedResult<ExEmployeeListItemDto>>(Assert.IsType<OkObjectResult>(res.Result).Value).Items.Select(i => i.EmployeeCode).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }

        (await Codes(scoped: true)).Should().Equal("D-OWN", "D-OWN2", "X-0001");
        (await Codes(scoped: false)).Should().Equal("D-FAR", "D-NONE", "D-OWN", "D-OWN2", "X-0001");
    }

    [Fact]
    public async Task BulkAllMatching_Export_ResolvesTheListsPopulation_OnPostgres()
    {
        var seed = await SeedAsync(current: 6, leavers: 2);
        await using var db = _fx.CreateDb();
        var c = Controller(db, seed, companyScoped: false);
        var listed = await ListAsync(c, db, 1, 100);

        var result = await c.BulkAction(new EmployeesController.BulkActionRequest("export", "allMatching", null, new EmployeesController.BulkSelectAllFilter(null, null, null, null, null), null, null, null), Service(db), CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        var csv = System.Text.Encoding.UTF8.GetString(file.FileContents);
        listed.Total.Should().Be(6);
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Contains("C-0")).Should().Be(listed.Total);
        csv.Should().NotContain("X-0001", "leavers are not part of the active population a bulk action resolves");
    }
}

file sealed class DirPgHijri : Zayra.Api.Infrastructure.Localization.IHijriDateService
{
    public Zayra.Api.Infrastructure.Localization.DateConversionDto FromGregorian(DateOnly date) =>
        new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}

file sealed class DirPgLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
}
