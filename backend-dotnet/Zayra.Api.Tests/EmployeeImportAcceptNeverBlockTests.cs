using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// The accept-never-block law: a bulk-import row is dropped ONLY for (a) no name or (b) duplicate
/// EmployeeCode. Every other failure imports the person as Draft + a typed gap. Also covers preview↔commit
/// count parity, manager email-fallback + auto-code linking, advisory-gap activatability, gap self-heal, and
/// the server-side readiness / gap deep-link filter.
/// </summary>
public class EmployeeImportAcceptNeverBlockTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedTenant(ZayraDbContext db)
    {
        var id = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = id, Name = "Zayra", Slug = $"z-{id:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = id, MaxEmployees = 1000, Plan = "Enterprise", Status = "Active" });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Company> SeedCompany(ZayraDbContext db, Guid tenantId, string name)
    {
        var c = new Company
        {
            TenantId = tenantId, LegalNameEn = name, CountryCode = "SA", Jurisdiction = "test",
            RegistrationNumber = $"RC-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow,
        };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    private static async Task<Department> SeedDepartment(ZayraDbContext db, Guid tenantId, string code, string name)
    {
        var d = new Department { TenantId = tenantId, Code = code, NameEn = name, IsActive = true };
        db.Departments.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    private static EmployeesController ImportController(ZayraDbContext db, Guid tenantId) =>
        HrmHierarchyTests.BuildImportControllerInternal(db, tenantId);

    private static string S(object v) => JsonSerializer.Serialize(v);
    private static object Payload(IActionResult r) => Assert.IsType<OkObjectResult>(r).Value!;

    // ── THE LAW: only no-name + dup-code drop; everything else imports + gap ─────────────────────
    [Fact]
    public async Task Import_DropsOnlyNoNameAndDupCode_EverythingElseImportsWithGaps()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var acme = await SeedCompany(db, tenantId, "Acme");
        var ctrl = ImportController(db, tenantId);

        // E1 fires 4 org leaks (unknown company/dept/grade/position) — all become gaps, row imports.
        // E2 has no name → dropped. E3 imports. E3 (dup) → dropped.
        var csv =
            "EmployeeCode,FullName,CompanyLegalName,Department,Grade,PositionCode,JoiningDate\n" +
            "E1,Alice,Ghost Co,Ghost Dept,Ghost Grade,GHOST-POS,2024-01-01\n" +
            "E2,,,,,,2024-01-01\n" +
            "E3,Bob,,,,,2024-01-01\n" +
            "E3,Bob Dup,,,,,2024-01-01\n";

        var payload = S(Payload(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None)));

        Assert.Contains("\"created\":2", payload);
        Assert.Contains("\"skipped\":2", payload);
        Assert.Contains("\"skippedNoName\":1", payload);
        Assert.Contains("\"skippedDupCode\":1", payload);

        // The invariant: dropped rows == the two lawful reasons and NOTHING else.
        const int received = 4, created = 2, skipped = 2;
        Assert.Equal(received, created + skipped);
        Assert.Equal(2, skipped); // exactly the two lawful reasons (1 no-name + 1 dup) — no other drop

        var e1 = await db.Employees.SingleAsync(e => e.TenantId == tenantId && e.EmployeeCode == "E1");
        Assert.Equal(acme.Id, e1.CompanyId); // unknown company defaulted, not dropped
        var types = await db.EmployeeImportGaps.Where(g => g.TenantId == tenantId && g.EmployeeId == e1.Id).Select(g => g.GapType).ToListAsync();
        Assert.Contains("org:company", types);
        Assert.Contains("org:department", types);
        Assert.Contains("org:grade", types);
        Assert.Contains("org:position", types);
    }

    // ── Preview↔commit parity: dry-run counts == commit counts ─────────────────────────────────
    [Fact]
    public async Task ImportPreview_CountsMatchCommit()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        await SeedCompany(db, tenantId, "Acme");
        var ctrl = ImportController(db, tenantId);

        var csv =
            "EmployeeCode,FullName,CompanyLegalName,Department,Grade,PositionCode,JoiningDate\n" +
            "E1,Alice,Ghost Co,Ghost Dept,Ghost Grade,GHOST-POS,2024-01-01\n" +
            "E2,,,,,,2024-01-01\n" +
            "E3,Bob,,,,,2024-01-01\n" +
            "E3,Bob Dup,,,,,2024-01-01\n";

        var preview = Payload(await ctrl.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        int wouldCreate = (int)preview.GetType().GetProperty("wouldCreate")!.GetValue(preview)!;
        int wouldSkip = (int)preview.GetType().GetProperty("wouldSkip")!.GetValue(preview)!;

        var commit = S(Payload(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None)));

        Assert.Contains($"\"created\":{wouldCreate}", commit);
        Assert.Contains($"\"skipped\":{wouldSkip}", commit);
        Assert.Equal(2, wouldCreate);
        Assert.Equal(2, wouldSkip);
    }

    // ── Dup-code is case-insensitive in BOTH preview and commit (Issue 3 parity) ─────────────────
    [Fact]
    public async Task Import_DuplicateEmployeeCode_IsCaseInsensitive_AndPreviewAgrees()
    {
        // An existing "ABC" makes an incoming "abc" a duplicate. Preview already treats it as a dup;
        // commit previously used a case-sensitive DB check and would have CREATED the case-variant,
        // diverging from preview. Both paths must now drop it (aligned to the in-file dedup folding).
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        db.Employees.Add(new Employee
        {
            TenantId = tenantId, EmployeeCode = "ABC", FullName = "Existing Person",
            Status = "Active", JoiningDate = DateTime.UtcNow, Designation = string.Empty,
        });
        await db.SaveChangesAsync();
        var ctrl = ImportController(db, tenantId);

        var csv =
            "EmployeeCode,FullName,JoiningDate\n" +
            "abc,Lowercase Clash,2024-01-01\n";

        var preview = S(Payload(await ctrl.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None)));
        Assert.Contains("\"wouldSkip\":1", preview);
        Assert.Contains("\"wouldCreate\":0", preview);

        var commit = S(Payload(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None)));
        Assert.Contains("\"created\":0", commit);
        Assert.Contains("\"skipped\":1", commit);
        Assert.Contains("already exists", commit);
        // Only the original survives — the case-variant was NOT created.
        Assert.Equal(1, await db.Employees.CountAsync(e => e.TenantId == tenantId));
    }

    // ── Manager linking: EMAIL fallback + AUTO-CODE carry ──────────────────────────────────────
    [Fact]
    public async Task Import_ManagerLinks_ByEmailFallback_AndAutoCodedManager()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var ctrl = ImportController(db, tenantId);

        // Manager row has a BLANK EmployeeCode (auto-generated) + a WorkEmail. The report row references the
        // manager by ManagerEmail only. Code-first, email-fallback + auto-code carry must still link.
        var csv =
            "EmployeeCode,FullName,WorkEmail,ManagerEmail,JoiningDate\n" +
            ",Boss Lady,boss@acme.test,,2024-01-01\n" +
            "R1,Report Guy,report@acme.test,boss@acme.test,2024-01-01\n";

        var payload = S(Payload(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None)));
        Assert.Contains("\"created\":2", payload);
        Assert.Contains("\"managersUnresolved\":0", payload);
        Assert.Contains("\"hierarchyLinked\":1", payload);

        var boss = await db.Employees.SingleAsync(e => e.TenantId == tenantId && e.WorkEmail == "boss@acme.test");
        var report = await db.Employees.SingleAsync(e => e.TenantId == tenantId && e.EmployeeCode == "R1");
        Assert.Equal(boss.Id, report.ManagerEmployeeId);
    }

    [Fact]
    public async Task Import_ManagerNotFound_IsWarningAndGap_NotError()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var ctrl = ImportController(db, tenantId);

        var csv =
            "EmployeeCode,FullName,ManagerEmployeeCode,JoiningDate\n" +
            "R1,Report Guy,NOBODY,2024-01-01\n";

        var payload = S(Payload(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None)));
        Assert.Contains("\"created\":1", payload);
        Assert.Contains("\"managersUnresolved\":1", payload);
        Assert.DoesNotContain("\"errors\":[\"", payload); // no row errors

        var r1 = await db.Employees.SingleAsync(e => e.TenantId == tenantId && e.EmployeeCode == "R1");
        Assert.Null(r1.ManagerEmployeeId);
        Assert.True(await db.EmployeeImportGaps.AnyAsync(g => g.EmployeeId == r1.Id && g.GapType == "link:manager"));
    }

    // ── Advisory gaps NEVER block activation ───────────────────────────────────────────────────
    [Fact]
    public async Task Import_AdvisoryOnlyGap_LandsNeedsAttention_ButStaysActivatable()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var ctrl = ImportController(db, tenantId);

        // No country/policy tenant: the only imperfection is an unknown department (advisory org gap).
        var csv =
            "EmployeeCode,FullName,Department,JoiningDate\n" +
            "E1,Alice,Ghost Dept,2024-01-01\n";
        await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var e1 = await db.Employees.SingleAsync(e => e.TenantId == tenantId && e.EmployeeCode == "E1");
        Assert.Equal("NeedsAttention", e1.ReadinessState);
        Assert.Equal(0, e1.ActivationBlockersCount); // advisory gap does NOT count as a blocker

        var guard = new EmployeeActivationGuard(db);
        var eval = await guard.EvaluateEmployeeAsync(tenantId, e1.Id, CancellationToken.None);
        Assert.NotNull(eval);
        Assert.False(eval!.Value.Readiness.IsBlocked);

        // EnsureActivatableAsync must not throw for an advisory-only-gap employee.
        var snap = await guard.BuildSnapshotAsync(tenantId, e1.Id, CancellationToken.None);
        var ctx = new RequestContext(null, null, TenantId: tenantId);
        var readiness = await guard.EnsureActivatableAsync(tenantId, e1.CompanyId, snap!, ctx, CancellationToken.None);
        Assert.False(readiness.IsBlocked);
    }

    // ── Gap self-heal: completing the org unit clears the gap on the next stamp ─────────────────
    [Fact]
    public async Task Gap_SelfHeals_WhenDepartmentIsLinkedLater()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var ctrl = ImportController(db, tenantId);
        var csv =
            "EmployeeCode,FullName,Department,JoiningDate\n" +
            "E1,Alice,Ghost Dept,2024-01-01\n";
        await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var e1 = await db.Employees.SingleAsync(e => e.TenantId == tenantId && e.EmployeeCode == "E1");
        Assert.Equal("NeedsAttention", e1.ReadinessState);

        // Operator later links a real department; the stamp path must heal the org:department gap.
        var dept = await SeedDepartment(db, tenantId, "OPS", "Operations");
        var tracked = await db.Employees.SingleAsync(e => e.Id == e1.Id);
        tracked.DepartmentId = dept.Id;
        await new EmployeeActivationGuard(db).StampReadinessAsync(tracked, CancellationToken.None);
        await db.SaveChangesAsync();

        var gap = await db.EmployeeImportGaps.SingleAsync(g => g.EmployeeId == e1.Id && g.GapType == "org:department");
        Assert.NotNull(gap.ResolvedAtUtc);
        Assert.Equal("Ready", (await db.Employees.SingleAsync(e => e.Id == e1.Id)).ReadinessState);
    }

    // ── Server-side readiness / gap filter pages the WHOLE dataset (past page 1) ────────────────
    [Fact]
    public async Task SearchAsync_ReadinessFilter_IsServerSide_AndPagesPastPageOne()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var ctrl = ImportController(db, tenantId);

        // 30 people with an unknown-department gap → all NeedsAttention; 2 clean → Ready.
        var sb = new System.Text.StringBuilder("EmployeeCode,FullName,Department,JoiningDate\n");
        for (int i = 1; i <= 30; i++) sb.Append($"G{i},Person {i},Ghost Dept,2024-01-01\n");
        sb.Append("C1,Clean One,,2024-01-01\n");
        sb.Append("C2,Clean Two,,2024-01-01\n");
        var import = Payload(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(sb.ToString()), CancellationToken.None));
        var batchId = (Guid)import.GetType().GetProperty("importBatchId")!.GetValue(import)!;

        var svc = new EmployeeManagementService(db, new AuditService(db), new NoopDocs(), new NoopNotifications());

        // Page 2 of the NeedsAttention set must return the tail — proving the filter is server-side, not
        // page-local (the old client-side filter returned nothing past page 1).
        var page2 = await svc.SearchAsync(tenantId, null, null, null, "NeedsAttention", null, null, page: 2, pageSize: 25, CancellationToken.None);
        Assert.Equal(30, page2.Total);
        Assert.Equal(5, page2.Items.Count);

        var ready = await svc.SearchAsync(tenantId, null, null, null, "Ready", null, null, 1, 25, CancellationToken.None);
        Assert.Equal(2, ready.Total);

        // Gap deep-link by type + batch.
        var byGap = await svc.SearchAsync(tenantId, null, null, null, null, batchId, "org:department", 1, 25, CancellationToken.None);
        Assert.Equal(30, byGap.Total);
    }
}

file sealed class NoopDocs : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
        Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument("f", "t", "u", "p"));
    public string ResolvePath(string storageUrl) => "/tmp";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class NoopNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}
