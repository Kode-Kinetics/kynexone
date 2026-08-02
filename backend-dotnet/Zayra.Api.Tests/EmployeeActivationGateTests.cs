using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// HARD ACTIVATION GATE — the 4-path guard-parity, structured 422, reinstatement carve-out, and import
/// leniency. Invariants (3) all four Active paths gated on becoming-Active-from-non-occupying, (4) split
/// primitive + 422 caught before InvalidOperationException, (6) import name-only lenient + dry-run.
/// A KSA expat missing GOSI/Iqama is blocked by the CODE FLOOR alone (no profile seeded).
/// </summary>
public class EmployeeActivationGateTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record Fx(Guid TenantId, Department Dept, Designation Desig);

    private static async Task<Fx> SeedTenant(ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Slug = $"t-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        var dept = new Department { TenantId = tenant.Id, Code = "OPS", NameEn = "Operations", IsActive = true };
        var desig = new Designation { TenantId = tenant.Id, Code = "OPS-OFF", TitleEn = "Operations Officer", IsActive = true };
        db.AddRange(dept, desig);
        await db.SaveChangesAsync();
        return new Fx(tenant.Id, dept, desig);
    }

    // A KSA expat: complete=true adds GOSI + Iqama (the code-floor blockers). Status defaults Draft.
    private static Employee KsaExpat(Fx fx, bool complete, string status = "Draft")
    {
        var e = new Employee
        {
            TenantId = fx.TenantId, EmployeeCode = $"E-{Guid.NewGuid():N}"[..12], FullName = "Test Expat",
            EnglishName = "Test Expat", CountryCode = "SA", Nationality = "Indian", Status = status,
            JoiningDate = DateTime.UtcNow, DepartmentId = fx.Dept.Id, DesignationId = fx.Desig.Id,
        };
        if (complete) { e.GosiReference = "GOSI-1"; e.IqamaNumber = "2000000001"; }
        return e;
    }

    private static EmployeeManagementService Service(ZayraDbContext db) =>
        new(db, new AuditService(db), new FakeDocs(), new FakeNotifications(), new EstablishmentGuardService(db));

    private static EmployeesController Controller(ZayraDbContext db, Guid tenantId)
    {
        var audit = new AuditService(db);
        var controller = new EmployeesController(db, new Pbkdf2PasswordHasher(), audit, new FakeDocs(),
            new FakeNotifications(), new FakeHijri(), new DataScopeService(db), new FakeLetters(),
            new ApprovalWorkflowService(db, audit), NullLogger<EmployeesController>.Instance, new EstablishmentGuardService(db));
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("permission", "organization.establishment.write"),
            new Claim("is_group_scope", "true"),
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    private static RequestContext Ctx(Guid tenantId) => new(null, "test", Guid.NewGuid(), tenantId);
    private static EmployeeStatusChangeRequest Activate(string reason = "Activate") =>
        new("Active", DateOnly.FromDateTime(DateTime.UtcNow.Date), reason);

    // ── ChangeStatusAsync gate ───────────────────────────────────────────────

    [Fact]
    public async Task ChangeStatusAsync_Blocked_Throws_And_LeavesDraft()
    {
        await using var db = CreateDb();
        var fx = await SeedTenant(db);
        var emp = KsaExpat(fx, complete: false);
        db.Employees.Add(emp); await db.SaveChangesAsync();

        var act = () => Service(db).ChangeStatusAsync(fx.TenantId, emp.Id, Activate(), Ctx(fx.TenantId), CancellationToken.None);
        (await act.Should().ThrowAsync<EmployeeActivationBlockedException>()).Which.Readiness.Blocking
            .Should().Contain(i => i.Key == "GosiReference");
        db.ChangeTracker.Clear();
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == emp.Id)).Status.Should().Be("Draft", "a blocked activation leaves the record untouched");
    }

    [Fact]
    public async Task ChangeStatusAsync_Ready_Activates_And_StampsActivatedAtUtc()
    {
        await using var db = CreateDb();
        var fx = await SeedTenant(db);
        var emp = KsaExpat(fx, complete: true);
        db.Employees.Add(emp); await db.SaveChangesAsync();

        var dto = await Service(db).ChangeStatusAsync(fx.TenantId, emp.Id, Activate(), Ctx(fx.TenantId), CancellationToken.None);
        dto!.Status.Should().Be("Active");
        var reloaded = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == emp.Id);
        reloaded.ActivatedAtUtc.Should().NotBeNull();
        reloaded.ActivationBlockersCount.Should().Be(0, "no activate-blockers remain once activated");
        reloaded.ReadinessState.Should().NotBe("Blocked");
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Offboarded")]
    public async Task Reinstatement_FromOccupyingStatus_IsNeverGated(string occupyingStatus)
    {
        await using var db = CreateDb();
        var fx = await SeedTenant(db);
        var emp = KsaExpat(fx, complete: false, status: occupyingStatus); // incomplete on purpose
        db.Employees.Add(emp); await db.SaveChangesAsync();

        var dto = await Service(db).ChangeStatusAsync(fx.TenantId, emp.Id, Activate("Reinstate"), Ctx(fx.TenantId), CancellationToken.None);
        dto!.Status.Should().Be("Active", "a mandated reinstatement from an occupying status is never refused, even if incomplete");
    }

    [Fact]
    public async Task Gate_IgnoresStaleSnapshot_RecomputesLive()
    {
        await using var db = CreateDb();
        var fx = await SeedTenant(db);
        var emp = KsaExpat(fx, complete: false);
        emp.ReadinessState = "Ready"; emp.ActivationBlockersCount = 0; // stale/forged snapshot
        db.Employees.Add(emp); await db.SaveChangesAsync();

        var act = () => Service(db).ChangeStatusAsync(fx.TenantId, emp.Id, Activate(), Ctx(fx.TenantId), CancellationToken.None);
        await act.Should().ThrowAsync<EmployeeActivationBlockedException>("the gate recomputes live and never trusts the stored snapshot");
    }

    // ── Controller catch ordering: structured 422, not generic 400 ───────────

    [Fact]
    public async Task ChangeStatusEndpoint_Blocked_Returns422_employee_not_activatable()
    {
        await using var db = CreateDb();
        var fx = await SeedTenant(db);
        var emp = KsaExpat(fx, complete: false);
        db.Employees.Add(emp); await db.SaveChangesAsync();

        var result = await Controller(db, fx.TenantId).ChangeStatus(emp.Id, Activate(), Service(db), CancellationToken.None);
        var obj = result.Result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        JsonSerializer.Serialize(obj.Value).Should().Contain("employee_not_activatable").And.Contain("GosiReference");
    }

    // ── ApproveDraft gate (3rd path) ─────────────────────────────────────────

    [Fact]
    public async Task ApproveDraft_Blocked_Returns422_DraftUntouched()
    {
        await using var db = CreateDb();
        var fx = await SeedTenant(db);
        var draft = new EmployeeDraft
        {
            TenantId = fx.TenantId, Status = "PendingHrApproval", CurrentStep = "HrApproval",
            EnglishName = "Draft Expat", CountryCode = "SA", Nationality = "Indian",
            Department = "Operations", Designation = "Operations Officer", JoiningDate = DateTime.UtcNow.Date,
        };
        db.EmployeeDrafts.Add(draft); await db.SaveChangesAsync();

        var result = await Controller(db, fx.TenantId).ApproveDraft(draft.Id, CancellationToken.None);
        var obj = result.Result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        JsonSerializer.Serialize(obj.Value).Should().Contain("employee_not_activatable");
        (await db.Employees.CountAsync(e => e.TenantId == fx.TenantId)).Should().Be(0, "the blocked draft creates no employee");
        (await db.EmployeeDrafts.AsNoTracking().SingleAsync(d => d.Id == draft.Id)).Status.Should().Be("PendingHrApproval");
    }

    // ── Import leniency (§7) ─────────────────────────────────────────────────

    private static async Task<JsonElement> ImportJson(EmployeesController c, string csv)
    {
        var res = (OkObjectResult)await c.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);
        return JsonSerializer.SerializeToElement(res.Value);
    }

    [Fact]
    public async Task Import_BlankStatus_LandsDraft_NeverActive()
    {
        await using var db = CreateDb();
        var fx = await SeedTenant(db);
        const string csv = "FullName,CountryCode,Nationality\nJane Expat,SA,Indian\n";
        await ImportJson(Controller(db, fx.TenantId), csv);
        (await db.Employees.AsNoTracking().SingleAsync(e => e.TenantId == fx.TenantId)).Status.Should().Be("Draft");
    }

    [Fact]
    public async Task Import_NameOnly_CreatedAsDraft_NeverSkipped()
    {
        await using var db = CreateDb();
        var fx = await SeedTenant(db);
        var json = await ImportJson(Controller(db, fx.TenantId), "FullName\nJust A Name\n");
        json.GetProperty("created").GetInt32().Should().Be(1);
        json.GetProperty("skipped").GetInt32().Should().Be(0);
        (await db.Employees.AsNoTracking().SingleAsync(e => e.TenantId == fx.TenantId)).Status.Should().Be("Draft");
    }

    [Fact]
    public async Task Import_ActiveWithBlocker_DowngradesToDraft_WithWarning_AndCreatedIncomplete()
    {
        await using var db = CreateDb();
        var fx = await SeedTenant(db);
        // Explicit Active KSA expat with NO GOSI/Iqama → downgraded to Draft + warning + createdIncomplete.
        const string csv = "FullName,CountryCode,Nationality,Status\nBlocked Expat,SA,Indian,Active\n";
        var json = await ImportJson(Controller(db, fx.TenantId), csv);
        (await db.Employees.AsNoTracking().SingleAsync(e => e.TenantId == fx.TenantId)).Status.Should().Be("Draft");
        json.GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().Contain(w => w!.Contains("imported as Draft") && w.Contains("GOSI"));
        json.GetProperty("createdIncomplete").GetArrayLength().Should().Be(1);
        json.TryGetProperty("importBatchId", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Import_ActiveComplete_StaysActive()
    {
        await using var db = CreateDb();
        var fx = await SeedTenant(db);
        const string csv = "FullName,CountryCode,Nationality,Status,GosiReference,IqamaNumber\nReady Expat,SA,Indian,Active,GOSI-9,2000000009\n";
        await ImportJson(Controller(db, fx.TenantId), csv);
        (await db.Employees.AsNoTracking().SingleAsync(e => e.TenantId == fx.TenantId)).Status.Should().Be("Active");
    }

    [Fact]
    public async Task ImportPreview_ProjectsLandingStates_WithoutPersisting()
    {
        await using var db = CreateDb();
        var fx = await SeedTenant(db);
        const string csv = "FullName,CountryCode,Nationality,Status\nBlocked Expat,SA,Indian,Active\nReady Expat,SA,Saudi,\n";
        var res = (OkObjectResult)await Controller(db, fx.TenantId).ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(res.Value);
        json.GetProperty("wouldCreateDraft").GetInt32().Should().BeGreaterThan(0);
        (await db.Employees.CountAsync(e => e.TenantId == fx.TenantId)).Should().Be(0, "dry-run persists nothing");
    }

    // ── Field-parity: registry is the single source of truth ─────────────────

    [Fact]
    public void FieldRegistry_CatalogAndReadinessSpecs_HaveNoDrift()
    {
        // The §3.1 enforcement: every readiness key has exactly one ActivationRelevant catalog descriptor
        // (and vice-versa) and CSV headers are unique. Throws on drift — this test is the CI guard that a
        // future edit desyncing the three surfaces fails fast instead of silently.
        var act = () => EmployeeFieldRegistry.AssertCatalogIntegrity();
        act.Should().NotThrow();
    }

    // ── Two-axis (country × nationality) catalog↔floor parity ────────────────

    [Theory]
    [InlineData("SA", "SA")]  [InlineData("SA", "Indian")]  [InlineData("SA", "AE")]
    [InlineData("AE", "AE")]  [InlineData("AE", "Indian")]  [InlineData("AE", "SA")]
    [InlineData("QA", "QA")]  [InlineData("QA", "Indian")]  [InlineData("QA", "SA")]
    [InlineData("KW", "KW")]  [InlineData("KW", "Indian")]
    [InlineData("OM", "OM")]  [InlineData("OM", "Indian")]
    [InlineData("BH", "BH")]  [InlineData("BH", "Indian")]
    public void FieldResolver_EveryHardFloorRequirement_HasAVisibleField(string country, string nationality)
    {
        // The mechanical anti-trap guarantee: no (country, nationality) can require a field the modal
        // cannot show — i.e. no employee is un-activatable (the SA-national / UAE-expat trap class).
        var normNat = GccReadinessFloor.NormalizeNationality(nationality);
        var visible = EmployeeFieldRegistry.CatalogFor(country, nationality)
            .Where(d => d.ActivationRelevant).Select(d => d.Key)
            .ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        foreach (var req in GccReadinessFloor.Resolve(country))
        {
            if (!req.FailClosed || req.Key.StartsWith("doc:", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (!GccReadinessFloor.NationalityApplies(req.AppliesWhen, normNat)) continue;
            visible.Should().Contain(req.Key, $"{country}/{nationality} floor requires '{req.Key}' — it must be enterable in the modal");
        }
    }

    [Fact]
    public void FieldResolver_SaudiNational_NeverSeesIqama_And_GccNational_NeverSeesWorkPermit()
    {
        // Owner's explicit acceptance criteria.
        var saNational = EmployeeFieldRegistry.CatalogFor("SA", "Saudi").Select(d => d.Key).ToList();
        saNational.Should().NotContain("IqamaNumber", "a Saudi national must NEVER see an Iqama field");
        saNational.Should().NotContain("IqamaExpiry");
        saNational.Should().Contain("IdNumber", "a Saudi national uses the National ID (Hawiyya)");

        // A UAE national never needs a work permit / residence visa (GCC reciprocity).
        var aeNational = EmployeeFieldRegistry.CatalogFor("AE", "Emirati").Select(d => d.Key).ToList();
        aeNational.Should().NotContain("WorkPermitNumber");
        aeNational.Should().Contain("EmiratesId");

        // A GCC national working in another GCC state is work-permit / visa exempt (Saudi in UAE).
        var gccExpat = EmployeeFieldRegistry.CatalogFor("AE", "Saudi").Select(d => d.Key).ToList();
        gccExpat.Should().NotContain("WorkPermitNumber", "a Saudi in the UAE is work-permit exempt");
        gccExpat.Should().Contain("EmiratesId", "but still holds an Emirates ID");

        // A non-GCC expat in the UAE DOES get the work permit the AE floor gates (the A1 trap fix).
        var expat = EmployeeFieldRegistry.CatalogFor("AE", "Indian").Select(d => d.Key).ToList();
        expat.Should().Contain("WorkPermitNumber");
    }

    [Fact]
    public void FieldResolver_CivilId_CarriesCountrySpecificLocalLabel()
    {
        string Label(string c) => EmployeeFieldRegistry.LabelFor(
            EmployeeFieldRegistry.CatalogFor(c, "Indian").Single(d => d.Key == "CivilId"), c);
        Label("KW").Should().Contain("Kuwait");
        Label("OM").Should().Contain("Resident Card");
        Label("BH").Should().Contain("CPR");
    }

    [Fact]
    public void ImportTemplate_Headers_AreDrivenBy_RegistryCatalog()
    {
        using var db = CreateDb();
        var fx = Guid.NewGuid();
        var csv = System.Text.Encoding.UTF8.GetString(
            ((FileContentResult)Controller(db, fx).ImportTemplate()).FileContents);
        var headerLine = csv.Split('\n')[0].Split(',').Select(h => h.Trim('"')).ToList();

        // The template column set IS the registry CSV header list — no hand-maintained array to drift.
        headerLine.Should().BeEquivalentTo(EmployeeFieldRegistry.CsvHeaders, o => o.WithStrictOrdering());
        // …and it now carries the parity columns that were previously missing (Δ11/Δ12/Δ16/Δ20/Δ23).
        headerLine.Should().Contain(new[]
        {
            "IqamaExpiry", "EmiratesIdExpiry", "QidExpiry", "CivilIdExpiry",
            "QiwaContractNumber", "SocialInsuranceReference",
            "EmergencyContactName", "EmergencyContactPhone", "ContractStartDate", "ContractEndDate",
        });
    }

    [Fact]
    public async Task Import_GccIdExpiryColumns_RoundTripTo_EmployeeScalars()
    {
        await using var db = CreateDb();
        var fx = await SeedTenant(db);
        // Registry-driven CSV column → first-class Employee scalar the readiness pay-gate reads (Δ20).
        const string csv =
            "FullName,CountryCode,Nationality,IqamaNumber,IqamaExpiry\n" +
            "Iqama Holder,SA,Indian,2000000123,2030-06-30\n";
        await ImportJson(Controller(db, fx.TenantId), csv);
        var emp = await db.Employees.AsNoTracking().SingleAsync(e => e.TenantId == fx.TenantId);
        emp.IqamaNumber.Should().Be("2000000123");
        emp.IqamaExpiryDate.Should().Be(new DateOnly(2030, 6, 30));
    }

    [Fact]
    public async Task ImportPreview_AggregatesFieldGaps_AcrossRows()
    {
        await using var db = CreateDb();
        var fx = await SeedTenant(db);
        // Two KSA expats missing the GOSI/Iqama floor → a per-field summary the modal can render.
        const string csv =
            "FullName,CountryCode,Nationality,Status\n" +
            "Expat One,SA,Indian,Active\n" +
            "Expat Two,SA,Indian,Active\n";
        var res = (OkObjectResult)await Controller(db, fx.TenantId)
            .ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(res.Value);
        var gaps = json.GetProperty("fieldGaps");
        gaps.GetArrayLength().Should().BeGreaterThan(0);
        gaps.EnumerateArray().Should().Contain(g =>
            g.GetProperty("field").GetString() == "GosiReference" && g.GetProperty("rowCount").GetInt32() == 2);
    }

    // ── Stubs ────────────────────────────────────────────────────────────────

    private sealed class FakeDocs : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new StoredDocument(file.FileName, file.ContentType ?? "application/octet-stream", "storage/test", "/tmp/test"));
        public string ResolvePath(string storageUrl) => "/tmp/test";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }

    private sealed class FakeNotifications : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeHijri : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class FakeLetters : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
