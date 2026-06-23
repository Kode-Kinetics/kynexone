using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class HrmHierarchyTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (IHrmHierarchyService svc, ZayraDbContext db, Guid tenantId) Setup()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var svc = new HrmHierarchyService(db, new AuditService(db));
        return (svc, db, tenantId);
    }

    private static async Task<Employee> AddEmp(ZayraDbContext db, Guid tenantId, string code, int? managerId = null)
    {
        var emp = new Employee
        {
            TenantId = tenantId,
            EmployeeCode = code,
            FullName = $"Emp {code}",
            Status = "Active",
            JoiningDate = DateTime.UtcNow.AddDays(-30),
            ManagerEmployeeId = managerId,
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        return emp;
    }

    private static RequestContext Ctx(Guid tenantId) =>
        new("127.0.0.1", "Test", Guid.NewGuid(), tenantId);

    // ── Circular manager detection ─────────────────────────────────────────────

    [Fact]
    public async Task SetManager_ThrowsOnCircularChain()
    {
        var (svc, db, tid) = Setup();
        // A → B → C, then try C's manager = A creates a loop
        var a = await AddEmp(db, tid, "A");
        var b = await AddEmp(db, tid, "B", a.Id);
        var c = await AddEmp(db, tid, "C", b.Id);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SetManagerAsync(tid, a.Id, c.Id, Ctx(tid), default));

        Assert.Contains("circular", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetManager_ThrowsOnSelfReference()
    {
        var (svc, db, tid) = Setup();
        var emp = await AddEmp(db, tid, "SELF");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SetManagerAsync(tid, emp.Id, emp.Id, Ctx(tid), default));

        Assert.Contains("own manager", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetManager_SucceedsForValidHierarchy()
    {
        var (svc, db, tid) = Setup();
        var mgr = await AddEmp(db, tid, "MGR");
        var emp = await AddEmp(db, tid, "EMP");

        await svc.SetManagerAsync(tid, emp.Id, mgr.Id, Ctx(tid), default);

        var updated = await db.Employees.FindAsync(emp.Id);
        Assert.Equal(mgr.Id, updated!.ManagerEmployeeId);

        var line = await db.ReportingLines.FirstOrDefaultAsync(r =>
            r.EmployeeId == emp.Id && r.ManagerEmployeeId == mgr.Id && r.RelationshipType == "SolidLine");
        Assert.NotNull(line);
        Assert.True(line!.IsPrimary);
        Assert.True(line.IsActive);
    }

    [Fact]
    public async Task SetManager_DeactivatesOldReportingLineWhenManagerChanges()
    {
        var (svc, db, tid) = Setup();
        var mgr1 = await AddEmp(db, tid, "MGR1");
        var mgr2 = await AddEmp(db, tid, "MGR2");
        var emp  = await AddEmp(db, tid, "EMP");

        await svc.SetManagerAsync(tid, emp.Id, mgr1.Id, Ctx(tid), default);
        await svc.SetManagerAsync(tid, emp.Id, mgr2.Id, Ctx(tid), default);

        var lines = await db.ReportingLines
            .Where(r => r.EmployeeId == emp.Id && r.RelationshipType == "SolidLine")
            .ToListAsync();

        Assert.Equal(2, lines.Count);
        Assert.Single(lines, l => l.IsActive && l.ManagerEmployeeId == mgr2.Id);
        Assert.Single(lines, l => !l.IsActive && l.ManagerEmployeeId == mgr1.Id);
    }

    [Fact]
    public async Task SetManager_ClearsManagerWhenNull()
    {
        var (svc, db, tid) = Setup();
        var mgr = await AddEmp(db, tid, "MGR");
        var emp = await AddEmp(db, tid, "EMP", mgr.Id);

        await svc.SetManagerAsync(tid, emp.Id, null, Ctx(tid), default);

        var updated = await db.Employees.FindAsync(emp.Id);
        Assert.Null(updated!.ManagerEmployeeId);
    }

    // ── Tenant isolation ──────────────────────────────────────────────────────

    [Fact]
    public async Task SetManager_RefusesManagerFromDifferentTenant()
    {
        var (svc, db, tid) = Setup();
        var otherTenant = Guid.NewGuid();

        var emp = await AddEmp(db, tid, "EMP");
        db.Employees.Add(new Employee
        {
            TenantId = otherTenant, EmployeeCode = "XMGR", FullName = "Other Mgr",
            Status = "Active", JoiningDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var otherMgrId = (await db.Employees.FirstAsync(e => e.EmployeeCode == "XMGR")).Id;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SetManagerAsync(tid, emp.Id, otherMgrId, Ctx(tid), default));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetOrgChart_OnlyIncludesOwnTenantEmployees()
    {
        var (svc, db, tid) = Setup();
        var otherTenant = Guid.NewGuid();

        await AddEmp(db, tid, "MINE");
        db.Employees.Add(new Employee
        {
            TenantId = otherTenant, EmployeeCode = "OTHER", FullName = "Other",
            Status = "Active", JoiningDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var chart = await svc.GetOrgChartAsync(tid, null, 5, default);

        Assert.All(chart, node => Assert.NotEqual("OTHER", node.EmployeeCode));
        Assert.Contains(chart, node => node.EmployeeCode == "MINE");
    }

    // ── Reporting lines ───────────────────────────────────────────────────────

    [Fact]
    public async Task AddReportingLine_CreatesNonSolidLineWithoutChangingManager()
    {
        var (svc, db, tid) = Setup();
        var mgr = await AddEmp(db, tid, "MGR");
        var emp = await AddEmp(db, tid, "EMP");

        var line = await svc.AddReportingLineAsync(tid, emp.Id,
            new AddReportingLineRequest(mgr.Id, "DottedLine", null, null, false), Ctx(tid), default);

        Assert.Equal("DottedLine", line.RelationshipType);
        Assert.False(line.IsPrimary);

        var updated = await db.Employees.FindAsync(emp.Id);
        Assert.Null(updated!.ManagerEmployeeId);
    }

    [Fact]
    public async Task AddReportingLine_RejectsInvalidRelationshipType()
    {
        var (svc, db, tid) = Setup();
        var mgr = await AddEmp(db, tid, "MGR");
        var emp = await AddEmp(db, tid, "EMP");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AddReportingLineAsync(tid, emp.Id,
                new AddReportingLineRequest(mgr.Id, "BestFriends", null, null), Ctx(tid), default));

        Assert.Contains("RelationshipType", ex.Message);
    }

    [Fact]
    public async Task RemoveReportingLine_DeactivatesLine()
    {
        var (svc, db, tid) = Setup();
        var mgr = await AddEmp(db, tid, "MGR");
        var emp = await AddEmp(db, tid, "EMP");

        var added = await svc.AddReportingLineAsync(tid, emp.Id,
            new AddReportingLineRequest(mgr.Id, "Functional", null, null), Ctx(tid), default);

        var removed = await svc.RemoveReportingLineAsync(tid, added.Id, Ctx(tid), default);
        Assert.True(removed);

        var line = await db.ReportingLines.FindAsync(added.Id);
        Assert.False(line!.IsActive);
        Assert.NotNull(line.EffectiveTo);
    }

    [Fact]
    public async Task RemoveReportingLine_ReturnsFalseForWrongTenant()
    {
        var (svc, db, tid) = Setup();
        var otherTenant = Guid.NewGuid();
        var mgr = await AddEmp(db, tid, "MGR");
        var emp = await AddEmp(db, tid, "EMP");

        var added = await svc.AddReportingLineAsync(tid, emp.Id,
            new AddReportingLineRequest(mgr.Id, "Functional", null, null), Ctx(tid), default);

        var removed = await svc.RemoveReportingLineAsync(otherTenant, added.Id, Ctx(otherTenant), default);
        Assert.False(removed);
    }

    // ── Org chart tree structure ──────────────────────────────────────────────

    [Fact]
    public async Task GetOrgChart_BuildsCorrectTreeDepth()
    {
        var (svc, db, tid) = Setup();
        var root = await AddEmp(db, tid, "ROOT");
        var child1 = await AddEmp(db, tid, "CHILD1", root.Id);
        var child2 = await AddEmp(db, tid, "CHILD2", root.Id);
        var grandchild = await AddEmp(db, tid, "GRAND", child1.Id);

        var chart = await svc.GetOrgChartAsync(tid, null, 5, default);

        Assert.Single(chart);
        var rootNode = chart[0];
        Assert.Equal("ROOT", rootNode.EmployeeCode);
        Assert.Equal(2, rootNode.DirectReports.Count);

        var c1Node = rootNode.DirectReports.First(n => n.EmployeeCode == "CHILD1");
        Assert.Single(c1Node.DirectReports);
        Assert.Equal("GRAND", c1Node.DirectReports[0].EmployeeCode);
    }

    [Fact]
    public async Task GetOrgChart_RespectsMaxDepth()
    {
        var (svc, db, tid) = Setup();
        var root = await AddEmp(db, tid, "L0");
        var l1   = await AddEmp(db, tid, "L1", root.Id);
        var l2   = await AddEmp(db, tid, "L2", l1.Id);
        var l3   = await AddEmp(db, tid, "L3", l2.Id);

        // maxDepth=2: root (0), L1 (1) — L1's children must be empty
        var chart = await svc.GetOrgChartAsync(tid, null, 2, default);

        var rootNode = chart[0];
        var l1Node = rootNode.DirectReports.First();
        Assert.Empty(l1Node.DirectReports);
    }

    [Fact]
    public async Task GetOrgChart_RootedAtSpecificEmployee()
    {
        var (svc, db, tid) = Setup();
        var ceo = await AddEmp(db, tid, "CEO");
        var vp  = await AddEmp(db, tid, "VP", ceo.Id);
        var mgr = await AddEmp(db, tid, "MGR", vp.Id);

        var chart = await svc.GetOrgChartAsync(tid, vp.Id, 5, default);

        Assert.Single(chart);
        Assert.Equal("VP", chart[0].EmployeeCode);
        Assert.DoesNotContain(chart, n => n.EmployeeCode == "CEO");
    }

    // ── Two-pass import ───────────────────────────────────────────────────────

    [Fact]
    public async Task Import_TwoPass_ResolvesManagerCodesToIds()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(db, tenantId, "test-hier");

        var controller = BuildImportController(db, tenantId);
        var csv =
            "EmployeeCode,FullName,ArabicName,WorkEmail,Phone,Gender,Nationality,Department,DepartmentCode,Designation,JobTitle,EmploymentType,ContractType,Status,JoiningDate,ManagerEmployeeCode,SupervisorEmployeeCode\n" +
            "IMP-CEO,Jane CEO,,,,,,,,,,,Full-time,,2023-01-01,,\n" +
            "IMP-MGR,John Manager,,,,,,,,,,,Full-time,,2023-02-01,IMP-CEO,\n" +
            "IMP-EMP,Bob Employee,,,,,,,,,,,Full-time,,2023-03-01,IMP-MGR,IMP-CEO\n";

        var result = await controller.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var data = ok.Value!;
        var created = (int)data.GetType().GetProperty("created")!.GetValue(data)!;
        Assert.Equal(3, created);

        var employees = await db.Employees.Where(e => e.TenantId == tenantId).ToListAsync();
        var ceo = employees.First(e => e.EmployeeCode == "IMP-CEO");
        var mgr = employees.First(e => e.EmployeeCode == "IMP-MGR");
        var emp = employees.First(e => e.EmployeeCode == "IMP-EMP");

        Assert.Null(ceo.ManagerEmployeeId);
        Assert.Equal(ceo.Id, mgr.ManagerEmployeeId);
        Assert.Equal(mgr.Id, emp.ManagerEmployeeId);
        Assert.Equal(ceo.Id, emp.SupervisorEmployeeId);

        var solidLines = await db.ReportingLines
            .Where(r => r.TenantId == tenantId && r.RelationshipType == "SolidLine")
            .ToListAsync();
        Assert.Equal(2, solidLines.Count);

        var dottedLines = await db.ReportingLines
            .Where(r => r.TenantId == tenantId && r.RelationshipType == "DottedLine")
            .ToListAsync();
        Assert.Single(dottedLines);
    }

    [Fact]
    public async Task Import_TwoPass_SkipsCircularManagerWithError()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(db, tenantId, "test-circular");

        var controller = BuildImportController(db, tenantId);
        var csv =
            "EmployeeCode,FullName,ArabicName,WorkEmail,Phone,Gender,Nationality,Department,DepartmentCode,Designation,JobTitle,EmploymentType,ContractType,Status,JoiningDate,ManagerEmployeeCode,SupervisorEmployeeCode\n" +
            "EMP-A,Alice,,,,,,,,,,,Full-time,,2023-01-01,EMP-A,\n";

        var result = await controller.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var data = ok.Value!;
        var errors = (System.Collections.IEnumerable)data.GetType().GetProperty("errors")!.GetValue(data)!;
        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task Import_TwoPass_ResolvesDesignationFromTitle()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(db, tenantId, "test-desig");
        db.Designations.Add(new Designation { TenantId = tenantId, Code = "SE", TitleEn = "Software Engineer" });
        await db.SaveChangesAsync();

        var controller = BuildImportController(db, tenantId);
        var csv =
            "EmployeeCode,FullName,ArabicName,WorkEmail,Phone,Gender,Nationality,Department,DepartmentCode,Designation,JobTitle,EmploymentType,ContractType,Status,JoiningDate,ManagerEmployeeCode,SupervisorEmployeeCode\n" +
            "EMP-SE,Alice,,,,,,Engineering,,Software Engineer,,Full-time,,Active,2023-01-01,,\n";

        var result = await controller.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        var emp = await db.Employees.FirstAsync(e => e.EmployeeCode == "EMP-SE");
        Assert.NotNull(emp.DesignationId);
    }

    // ── ValidateNoCircularManagerAsync ────────────────────────────────────────

    [Fact]
    public async Task ValidateNoCircular_ReturnsSuccessWhenChainIsClean()
    {
        var (svc, db, tid) = Setup();
        var a = await AddEmp(db, tid, "A");
        var b = await AddEmp(db, tid, "B", a.Id);
        var c = await AddEmp(db, tid, "C", b.Id);
        var d = await AddEmp(db, tid, "D");

        // D has no manager — setting A as D's manager won't loop
        var depth = await svc.ValidateNoCircularManagerAsync(tid, d.Id, a.Id, default);
        Assert.True(depth >= 0);
    }

    [Fact]
    public async Task ValidateNoCircular_ThrowsWhenChainLoops()
    {
        var (svc, db, tid) = Setup();
        var a = await AddEmp(db, tid, "A");
        var b = await AddEmp(db, tid, "B", a.Id);
        var c = await AddEmp(db, tid, "C", b.Id);

        // A → B → C — setting C as A's manager would create A → B → C → A
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ValidateNoCircularManagerAsync(tid, a.Id, c.Id, default));
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task SeedTenantAsync(ZayraDbContext db, Guid tenantId, string slug)
    {
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Test", Slug = slug });
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId = tenantId, MaxEmployees = 100, Plan = "Enterprise", Status = "Active"
        });
        await db.SaveChangesAsync();
    }

    // Internal so ApprovalPolicyTests can reuse the controller builder
    internal static EmployeesController BuildImportControllerInternal(ZayraDbContext db, Guid tenantId)
        => BuildImportController(db, tenantId);

    private static EmployeesController BuildImportController(ZayraDbContext db, Guid tenantId)
    {
        var controller = new EmployeesController(
            db,
            new Pbkdf2PasswordHasher(),
            new AuditService(db),
            new FakeDocStorage(),
            new NotificationService(db, new FakeEmailSvc(), NullLogger<NotificationService>.Instance),
            new FakeHijriSvc(),
            new Zayra.Api.Infrastructure.Common.DataScopeService(db),
            new FakeLetterSvc());

        var userId = Guid.NewGuid();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                }, "Test"))
            }
        };
        return controller;
    }
}

// ── Fakes (file-scoped to avoid symbol conflict with EmployeeModuleTests) ─────

file sealed class FakeDocStorage : IDocumentStorage
{
    public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
        Task.FromResult(new StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public string ResolvePath(string storageUrl) => "/tmp/test";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());
}

file sealed class FakeEmailSvc : IEmailService
{
    public Task SendAsync(string to, string name, string subject, string html,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(false);
}

file sealed class FakeHijriSvc : IHijriDateService
{
    public DateConversionDto FromGregorian(DateOnly date) =>
        new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}

file sealed class FakeLetterSvc : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());
}
