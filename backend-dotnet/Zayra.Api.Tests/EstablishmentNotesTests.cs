using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Editable plain-text notes: one per department row, one per department-level budget cell.
/// Written through the existing PutBudgets endpoint; read through the matrix (not salary data, never withheld).</summary>
public class EstablishmentNotesTests
{
    private sealed record Fx(Guid TenantId, Department Dept, StaffingLevel Level);

    private static async Task<Fx> Seed(ZayraDbContext db, Guid? tenantId = null, bool withBudget = true)
    {
        var t = tenantId ?? Guid.NewGuid();
        var dept = new Department { TenantId = t, Code = "OPS", NameEn = "Operations", IsActive = true, ApprovedHeadcount = 5 };
        var level = new StaffingLevel { TenantId = t, Code = "MGR", NameEn = "Manager", NameAr = "م", Rank = 2 };
        db.AddRange(dept, level);
        if (withBudget)
            db.DepartmentStaffingBudgets.Add(new DepartmentStaffingBudget { TenantId = t, DepartmentId = dept.Id, StaffingLevelId = level.Id, BudgetedHeadcount = 2 });
        await db.SaveChangesAsync();
        return new Fx(t, dept, level);
    }

    private static Task<IActionResult> Put(ZayraDbContext db, Fx fx, string? deptNote, string? cellNote, int? heads = 2, string[]? perms = null) =>
        EstablishmentApiTests.CreateController(db, fx.TenantId, perms ?? Array.Empty<string>()).PutBudgets(fx.Dept.Id,
            new DepartmentBudgetsRequest(new List<DepartmentBudgetRow> { new(fx.Level.Id, heads, cellNote) }, "notes", deptNote), CancellationToken.None);

    private static async Task<JsonElement> DeptJson(ZayraDbContext db, Guid tenantId)
    {
        var ok = (OkObjectResult)await EstablishmentApiTests.CreateController(db, tenantId).Matrix(CancellationToken.None);
        return JsonSerializer.SerializeToElement(ok.Value).GetProperty("departments")[0];
    }

    [Fact]
    public async Task Roundtrip_NotesAppearOnDepartmentRowAndLevelCell()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        (await Put(db, fx, "Q4 hiring freeze", "Two seats reserved")).Should().BeOfType<OkObjectResult>();
        var dept = await DeptJson(db, fx.TenantId);
        dept.GetProperty("note").GetString().Should().Be("Q4 hiring freeze");
        dept.GetProperty("levels")[0].GetProperty("note").GetString().Should().Be("Two seats reserved");
    }

    [Fact]
    public async Task NoNotes_ReturnNull()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        var dept = await DeptJson(db, fx.TenantId);
        dept.GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);
        dept.GetProperty("levels")[0].GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Notes_AreTrimmed_AndEmptyOrWhitespaceBecomesNull()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        await Put(db, fx, "  padded  ", "\t cell \n");
        (await db.Departments.SingleAsync(d => d.Id == fx.Dept.Id)).EstablishmentNote.Should().Be("padded");
        (await db.DepartmentStaffingBudgets.SingleAsync(b => b.DepartmentId == fx.Dept.Id)).Note.Should().Be("cell");
        await Put(db, fx, "   ", "");
        (await db.Departments.AsNoTracking().SingleAsync(d => d.Id == fx.Dept.Id)).EstablishmentNote.Should().BeNull();
        (await db.DepartmentStaffingBudgets.AsNoTracking().SingleAsync(b => b.DepartmentId == fx.Dept.Id)).Note.Should().BeNull();
    }

    [Fact]
    public async Task Omitted_LeavesNotesUnchanged()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        await Put(db, fx, "keep dept", "keep cell");
        await Put(db, fx, null, null, heads: 3);
        var dept = await DeptJson(db, fx.TenantId);
        dept.GetProperty("note").GetString().Should().Be("keep dept");
        dept.GetProperty("levels")[0].GetProperty("note").GetString().Should().Be("keep cell");
        dept.GetProperty("levels")[0].GetProperty("budgeted").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task EmptyString_ClearsExistingNote()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        await Put(db, fx, "x", "y");
        await Put(db, fx, "", "");
        var dept = await DeptJson(db, fx.TenantId);
        dept.GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);
        dept.GetProperty("levels")[0].GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task NoteChangeAlone_WithUnchangedHeadcount_IsSaved()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        await Put(db, fx, null, "only the cell note", heads: 2);
        (await db.DepartmentStaffingBudgets.AsNoTracking().SingleAsync(b => b.DepartmentId == fx.Dept.Id)).Note.Should().Be("only the cell note");
    }

    [Fact]
    public async Task DepartmentNoteOnly_NoRows_IsAccepted()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        var r = await EstablishmentApiTests.CreateController(db, fx.TenantId).PutBudgets(fx.Dept.Id,
            new DepartmentBudgetsRequest(new List<DepartmentBudgetRow>(), "note only", "dept only"), CancellationToken.None);
        r.Should().BeOfType<OkObjectResult>();
        (await db.Departments.AsNoTracking().SingleAsync(d => d.Id == fx.Dept.Id)).EstablishmentNote.Should().Be("dept only");
    }

    [Fact]
    public async Task OverFiveHundredChars_Rejected_Nothing_Saved()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        var tooLong = new string('a', 501);
        (await Put(db, fx, tooLong, null)).Should().BeOfType<BadRequestObjectResult>();
        (await Put(db, fx, null, tooLong, heads: 9)).Should().BeOfType<BadRequestObjectResult>();
        (await Put(db, fx, new string('b', 500), new string('c', 500))).Should().BeOfType<OkObjectResult>("exactly 500 is allowed");
        (await Put(db, fx, "  " + new string('d', 500) + "  ", null)).Should().BeOfType<OkObjectResult>("the limit applies after trimming");
        (await db.DepartmentStaffingBudgets.AsNoTracking().SingleAsync(b => b.DepartmentId == fx.Dept.Id)).BudgetedHeadcount
            .Should().Be(2, "a rejected request changes nothing");
    }

    [Fact]
    public async Task CellNote_OnALevelWithNoBudgetRow_IsRejected()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db, withBudget: false);
        (await Put(db, fx, null, "orphan note", heads: null)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task WithoutWritePermission_CannotWriteNotes()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        (await Put(db, fx, "sneaky", "sneaky", perms: new[] { "organization.read" })).Should().BeOfType<ForbidResult>();
        (await db.Departments.AsNoTracking().SingleAsync(d => d.Id == fx.Dept.Id)).EstablishmentNote.Should().BeNull();
    }

    [Fact]
    public async Task ReadOnlyRole_StillSeesNotes_NotWithheldLikeSalary()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        await Put(db, fx, "visible", "cell visible");
        var ok = (OkObjectResult)await EstablishmentApiTests.CreateController(db, fx.TenantId, "organization.read").Matrix(CancellationToken.None);
        var dept = JsonSerializer.SerializeToElement(ok.Value).GetProperty("departments")[0];
        dept.GetProperty("note").GetString().Should().Be("visible");
        dept.GetProperty("levels")[0].GetProperty("note").GetString().Should().Be("cell visible");
    }

    [Fact]
    public async Task TenantIsolation_NoteOnTenantA_NeverReadableOrWritableViaTenantB()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var a = await Seed(db);
        var b = await Seed(db);
        await Put(db, a, "secret of A", "cell secret of A");

        var json = JsonSerializer.Serialize(((OkObjectResult)await EstablishmentApiTests.CreateController(db, b.TenantId).Matrix(CancellationToken.None)).Value);
        json.Should().NotContain("secret of A");

        var cross = await EstablishmentApiTests.CreateController(db, b.TenantId).PutBudgets(a.Dept.Id,
            new DepartmentBudgetsRequest(new List<DepartmentBudgetRow> { new(a.Level.Id, 1, "hijack") }, "x", "hijack"), CancellationToken.None);
        cross.Should().BeOfType<NotFoundObjectResult>();
        (await db.Departments.AsNoTracking().SingleAsync(d => d.Id == a.Dept.Id)).EstablishmentNote.Should().Be("secret of A");
    }

    [Fact]
    public async Task Audit_RecordsThatNotesChanged_WithLengths_NotRequiredText()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        await Put(db, fx, "dept note!", "cell");
        var audit = await db.AuditLogs.SingleAsync(a => a.TenantId == fx.TenantId && a.Action == "establishment.budget_updated");
        var meta = JsonDocument.Parse(audit.Metadata).RootElement;
        var dn = meta.GetProperty("departmentNote");
        dn.GetProperty("beforeLength").GetInt32().Should().Be(0);
        dn.GetProperty("afterLength").GetInt32().Should().Be(10);
        var lvl = meta.GetProperty("levels")[0];
        lvl.GetProperty("noteBeforeLength").GetInt32().Should().Be(0);
        lvl.GetProperty("noteAfterLength").GetInt32().Should().Be(4);
    }

    [Fact]
    public async Task Audit_DepartmentNoteOnlyChange_IsStillAudited()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        await EstablishmentApiTests.CreateController(db, fx.TenantId).PutBudgets(fx.Dept.Id,
            new DepartmentBudgetsRequest(new List<DepartmentBudgetRow>(), "note only", "abc"), CancellationToken.None);
        (await db.AuditLogs.CountAsync(a => a.TenantId == fx.TenantId && a.Action == "establishment.budget_updated")).Should().Be(1);
    }

    [Fact]
    public async Task Audit_UnchangedNote_IsNotLoggedAsChange()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        await Put(db, fx, "same", "same");
        await Put(db, fx, "same", "same");
        (await db.AuditLogs.CountAsync(a => a.TenantId == fx.TenantId && a.Action == "establishment.budget_updated")).Should().Be(1);
    }

    [Fact]
    public async Task NotesArePlainText_MarkupIsStoredVerbatimAndEscapedInJson()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        await Put(db, fx, "<script>alert(1)</script>", "<b>x</b>");
        (await db.Departments.AsNoTracking().SingleAsync(d => d.Id == fx.Dept.Id)).EstablishmentNote.Should().Be("<script>alert(1)</script>");
        var json = JsonSerializer.Serialize(((OkObjectResult)await EstablishmentApiTests.CreateController(db, fx.TenantId).Matrix(CancellationToken.None)).Value);
        json.Should().NotContain("<script>", "System.Text.Json escapes angle brackets");
    }
}
