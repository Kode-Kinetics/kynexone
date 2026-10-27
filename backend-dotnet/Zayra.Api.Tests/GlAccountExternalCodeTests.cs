using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The ERP account code (gl_accounts.external_code) is what journal exports send to the client's ERP.
/// Before this change nothing could set it: the account endpoints and the chart CSV ignored it. These
/// tests pin create / update (unchanged, replace, clear) / validation / CSV round-trip and the audit.
/// </summary>
public class GlAccountExternalCodeTests
{
    private static readonly string[] GlPerms = { "finance.gl.read", "finance.gl.manage" };

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static FinanceGlController Gl(ZayraDbContext db, Guid tid)
    {
        var claims = new List<Claim> { new("tenant_id", tid.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        claims.AddRange(GlPerms.Select(p => new Claim("permission", p)));
        var c = new FinanceGlController(db);
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
        };
        return c;
    }

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o);

    private static IFormFile CsvFile(string content) =>
        new FormFile(new MemoryStream(Encoding.UTF8.GetBytes(content)), 0, Encoding.UTF8.GetByteCount(content), "file", "coa.csv");

    private static async Task<GlAccount> Reload(ZayraDbContext db, Guid tid, string code) =>
        await db.GlAccounts.AsNoTracking().SingleAsync(a => a.TenantId == tid && a.Code == code);

    [Fact]
    public async Task Create_StoresTrimmedExternalCode_AndListReturnsIt()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var gl = Gl(db, tid);

        var created = (await gl.CreateAccount(null, new GlAccountRequest("5001", "Basic", "Expense", true, "  01-000-6100/00  "), default))
            .Should().BeOfType<OkObjectResult>().Subject.Value!;
        Prop(created, "ExternalCode").Should().Be("01-000-6100/00");
        (await Reload(db, tid, "5001")).ExternalCode.Should().Be("01-000-6100/00");

        (await gl.CreateAccount(null, new GlAccountRequest("5002", "Housing", "Expense"), default)).Should().BeOfType<OkObjectResult>();
        (await Reload(db, tid, "5002")).ExternalCode.Should().BeNull("no ERP code was given");

        var list = ((System.Collections.IEnumerable)(await gl.ListAccounts(null, default)).Should().BeOfType<OkObjectResult>().Subject.Value!)
            .Cast<object>().ToList();
        list.Select(a => Prop(a, "ExternalCode")).Should().BeEquivalentTo(new object?[] { "01-000-6100/00", null });

        var audit = await db.AuditLogs.AsNoTracking().FirstAsync(a => a.Action == "finance.gl.account.created");
        audit.Metadata.Should().Contain("01-000-6100/00");
    }

    [Fact]
    public async Task Update_NullLeavesUnchanged_ValueReplaces_EmptyClears_AndAuditsBeforeAfter()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var acct = new GlAccount { TenantId = tid, Code = "5001", Name = "Basic", AccountType = "Expense", ExternalCode = "6100" };
        db.GlAccounts.Add(acct);
        await db.SaveChangesAsync();
        var gl = Gl(db, tid);

        // null = unchanged (an older client that does not know the field must not wipe it).
        (await gl.UpdateAccount(acct.Id, false, new GlAccountRequest("5001", "Basic salary", "Expense", true, null), default))
            .Should().BeOfType<OkObjectResult>();
        (await Reload(db, tid, "5001")).ExternalCode.Should().Be("6100");

        // A value replaces it.
        (await gl.UpdateAccount(acct.Id, false, new GlAccountRequest("5001", "Basic salary", "Expense", true, " 6100_SAL "), default))
            .Should().BeOfType<OkObjectResult>();
        (await Reload(db, tid, "5001")).ExternalCode.Should().Be("6100_SAL");
        var replaced = await db.AuditLogs.AsNoTracking().Where(a => a.Action == "finance.gl.account.updated").OrderBy(a => a.CreatedAtUtc).ToListAsync();
        using (var doc = JsonDocument.Parse(replaced.Last().Metadata!))
        {
            doc.RootElement.GetProperty("before").GetProperty("ExternalCode").GetString().Should().Be("6100");
            doc.RootElement.GetProperty("after").GetProperty("ExternalCode").GetString().Should().Be("6100_SAL");
        }

        // "" clears it.
        (await gl.UpdateAccount(acct.Id, false, new GlAccountRequest("5001", "Basic salary", "Expense", true, ""), default))
            .Should().BeOfType<OkObjectResult>();
        (await Reload(db, tid, "5001")).ExternalCode.Should().BeNull();
    }

    [Theory]
    [InlineData("6100;DROP")]
    [InlineData("=HYPERLINK(1)")]
    [InlineData("6100'")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890")] // 61 characters
    public async Task CreateAndUpdate_RefuseAnInvalidExternalCode(string bad)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var acct = new GlAccount { TenantId = tid, Code = "5001", Name = "Basic", AccountType = "Expense", ExternalCode = "6100" };
        db.GlAccounts.Add(acct);
        await db.SaveChangesAsync();
        var gl = Gl(db, tid);

        var create = (await gl.CreateAccount(null, new GlAccountRequest("5002", "Housing", "Expense", true, bad), default))
            .Should().BeOfType<BadRequestObjectResult>().Subject.Value!;
        Prop(create, "code").Should().Be("GL_EXTERNAL_CODE_INVALID");
        (await db.GlAccounts.CountAsync(a => a.TenantId == tid)).Should().Be(1);

        (await gl.UpdateAccount(acct.Id, false, new GlAccountRequest("5001", "Basic", "Expense", true, bad), default))
            .Should().BeOfType<BadRequestObjectResult>();
        (await Reload(db, tid, "5001")).ExternalCode.Should().Be("6100");
    }

    [Fact]
    public async Task ChartCsv_RoundTripsExternalCode_MissingColumnIsUnchanged_EmptyCellClears()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        db.GlAccounts.AddRange(
            new GlAccount { TenantId = tid, Code = "5001", Name = "Basic", AccountType = "Expense", ExternalCode = "-6100" },
            new GlAccount { TenantId = tid, Code = "2100", Name = "Salaries Payable", AccountType = "Liability" });
        await db.SaveChangesAsync();
        var gl = Gl(db, tid);

        var csv = Encoding.UTF8.GetString((await gl.ExportAccounts(null, default)).Should().BeOfType<FileContentResult>().Subject.FileContents).TrimStart('\uFEFF');
        csv.Should().StartWith("Code,Name,Type,IsActive,ExternalCode");
        csv.Should().Contain("2100,Salaries Payable,Liability,true,");

        // Re-importing the exported file unchanged changes nothing — including the '-6100' that the
        // export guarded against formula injection with a leading apostrophe.
        var roundTrip = (await gl.ImportAccounts(null, false, CsvFile(csv), default)).Should().BeOfType<OkObjectResult>().Subject.Value!;
        Prop(roundTrip, "unchanged").Should().Be(2);
        Prop(roundTrip, "updated").Should().Be(0);
        (await Reload(db, tid, "5001")).ExternalCode.Should().Be("-6100");

        // A file WITHOUT the column (the pre-existing 4-column format) leaves ERP codes alone.
        var legacy = "Code,Name,Type,IsActive\n5001,Basic salary,Expense,true\n";
        var legacyResult = (await gl.ImportAccounts(null, false, CsvFile(legacy), default)).Should().BeOfType<OkObjectResult>().Subject.Value!;
        Prop(legacyResult, "updated").Should().Be(1);
        (await Reload(db, tid, "5001")).ExternalCode.Should().Be("-6100");

        // With the column: a value sets it, an empty cell clears it, a new row takes it.
        var edit = "Code,Name,Type,IsActive,ExternalCode\n5001,Basic salary,Expense,true,\n2100,Salaries Payable,Liability,true,2100-PAY\n6100,Bonus,Expense,true,6100/BON\n";
        var applied = (await gl.ImportAccounts(null, false, CsvFile(edit), default)).Should().BeOfType<OkObjectResult>().Subject.Value!;
        Prop(applied, "updated").Should().Be(2, "an ERP-code-only change is an update, not 'unchanged'");
        Prop(applied, "created").Should().Be(1);
        (await Reload(db, tid, "5001")).ExternalCode.Should().BeNull();
        (await Reload(db, tid, "2100")).ExternalCode.Should().Be("2100-PAY");
        (await Reload(db, tid, "6100")).ExternalCode.Should().Be("6100/BON");
        var audit = await db.AuditLogs.AsNoTracking().OrderBy(a => a.CreatedAtUtc).LastAsync(a => a.Action == "finance.gl.account.imported");
        audit.Metadata.Should().Contain("2100-PAY");
    }

    [Fact]
    public async Task ChartCsv_Import_RefusesAnInvalidExternalCode_AllOrNothing()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var gl = Gl(db, tid);

        var bad = "Code,Name,Type,IsActive,ExternalCode\n5001,Basic,Expense,true,6100\n5002,Housing,Expense,true,6100;6200\n";
        var refused = (await gl.ImportAccounts(null, false, CsvFile(bad), default)).Should().BeOfType<UnprocessableEntityObjectResult>().Subject.Value!;
        var errors = ((System.Collections.IEnumerable)Prop(refused, "errors")!).Cast<object>().ToList();
        errors.Should().ContainSingle();
        Prop(errors[0], "row").Should().Be(3);
        (await db.GlAccounts.CountAsync(a => a.TenantId == tid)).Should().Be(0, "all or nothing");
    }
}
