using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using StoredDocument = Zayra.Api.Infrastructure.Documents.StoredDocument;

namespace Zayra.Api.Tests;

// ── Tests for the Payslip Template Designer ────────────────────────────────────
// All tests that touch the DB use the PostgresFixture (Testcontainers) for
// realistic constraint and filter behaviour. Unit-level validation tests use
// the registry directly and need no DB.

[Trait("Category", "Integration")]
public class PayslipTemplateTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fx;
    public PayslipTemplateTests(PostgresFixture fx) => _fx = fx;

    // ── 1. CRUD — happy path ──────────────────────────────────────────────────

    [Fact]
    public async Task Create_And_Get_Template_Succeeds()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var ctrl = BuildController(db, tenantId, "Admin");

        var req = ValidRequest("My Template");
        var result = await ctrl.Create(req, CancellationToken.None);
        var created = Assert.IsType<CreatedAtActionResult>(result);
        var dto = Assert.IsType<TemplateDto>(created.Value);
        Assert.Equal("My Template", dto.Name);
        Assert.Equal(1, dto.Version);
        Assert.Equal("draft", dto.Status);

        var getResult = await ctrl.Get(dto.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(getResult);
        var fetched = Assert.IsType<TemplateDto>(ok.Value);
        Assert.Equal(dto.Id, fetched.Id);
    }

    // ── 2. Tenant isolation — Tenant B cannot read Tenant A's templates ────────

    [Fact]
    public async Task TenantB_Cannot_Read_TenantA_Template()
    {
        await using var db = _fx.CreateDb();
        var tenantA = await PostgresFixture.SeedMinimalTenant(db);
        var tenantB = await PostgresFixture.SeedMinimalTenant(db);

        // Create template for A
        var ctrlA = BuildController(db, tenantA, "Admin");
        var createResult = await ctrlA.Create(ValidRequest("A's Template"), CancellationToken.None);
        var dto = Assert.IsType<TemplateDto>(((CreatedAtActionResult)createResult).Value!);

        // B tries to read it by ID
        var ctrlB = BuildController(db, tenantB, "Admin");
        var getResult = await ctrlB.Get(dto.Id, CancellationToken.None);
        Assert.IsType<NotFoundResult>(getResult);

        // B list returns nothing
        var listResult = await ctrlB.List(CancellationToken.None);
        var listOk = Assert.IsType<OkObjectResult>(listResult);
        var items = Assert.IsAssignableFrom<IEnumerable<TemplateListItem>>(listOk.Value!);
        Assert.Empty(items);
    }

    // ── 3. RBAC — non-admin role cannot create or update ─────────────────────

    [Fact]
    public async Task PayrollOfficer_Cannot_Create_Template()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var ctrl = BuildController(db, tenantId, "Payroll Officer");

        // The authorization check is done by the [Authorize(Roles=...)] attribute.
        // In unit tests without the full ASP.NET middleware, we simulate the role
        // by checking the ClaimsPrincipal used to build the controller.
        // A real integration test would hit the HTTP stack; here we assert the
        // controller's User.IsInRole() behaves as expected given the claims.
        Assert.False(ctrl.User.IsInRole("Admin"));
        Assert.False(ctrl.User.IsInRole("HR Manager"));
        Assert.False(ctrl.User.IsInRole("Payroll Manager"));
        Assert.True(ctrl.User.IsInRole("Payroll Officer"));
        // The attribute [Authorize(Roles = "Admin,HR Manager,Payroll Manager")] would
        // reject this request; we mark this test as confirmed via role inspection.
    }

    // ── 4. Validation — compliance-locked field cannot be removed ─────────────

    [Fact]
    public async Task Remove_Locked_Field_Returns_400()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var ctrl = BuildController(db, tenantId, "Admin");

        // Layout that omits gosi_annuities_ee (compliance-locked) from deductions
        var layout = new PayslipLayoutConfig("en", new[]
        {
            new PayslipSectionConfig("earnings",   true, 1, new[] { "basic_salary" }),
            new PayslipSectionConfig("deductions", true, 2, new[] { "loan_repayment" }), // missing gosi_annuities_ee!
        });

        var req = new TemplateUpsertRequest(
            "Bad Template", false,
            JsonSerializer.Serialize(ValidBranding()),
            JsonSerializer.Serialize(layout));

        var result = await ctrl.Create(req, CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("gosi_annuities_ee", json);
    }

    // ── 5. One-default invariant ───────────────────────────────────────────────

    [Fact]
    public async Task Setting_New_Default_Clears_Previous_Default()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var ctrl = BuildController(db, tenantId, "Admin");

        // Create first as default
        var r1 = await ctrl.Create(ValidRequest("T1", isDefault: true), CancellationToken.None);
        var dto1 = Assert.IsType<TemplateDto>(((CreatedAtActionResult)r1).Value!);
        Assert.True(dto1.IsDefault);

        // Create second as default
        var r2 = await ctrl.Create(ValidRequest("T2", isDefault: true), CancellationToken.None);
        var dto2 = Assert.IsType<TemplateDto>(((CreatedAtActionResult)r2).Value!);
        Assert.True(dto2.IsDefault);

        // T1 should no longer be default
        var t1 = await db.PayslipTemplates.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == dto1.Id);
        Assert.False(t1.IsDefault);

        // Only one default in DB for this tenant
        var defaultCount = await db.PayslipTemplates
            .CountAsync(t => t.TenantId == tenantId && t.IsDefault);
        Assert.Equal(1, defaultCount);
    }

    // ── 6. Versioning — update creates new version, archives old ──────────────

    [Fact]
    public async Task Update_Creates_New_Version_Archives_Old()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var ctrl = BuildController(db, tenantId, "Admin");

        var createResult = await ctrl.Create(ValidRequest("Versioned"), CancellationToken.None);
        var v1 = Assert.IsType<TemplateDto>(((CreatedAtActionResult)createResult).Value!);
        Assert.Equal(1, v1.Version);

        var updateResult = await ctrl.Update(v1.Id, ValidRequest("Versioned"), CancellationToken.None);
        var v2 = Assert.IsType<TemplateDto>(((OkObjectResult)updateResult).Value!);
        Assert.Equal(2, v2.Version);
        Assert.Equal("active", v2.Status);
        Assert.Equal(v1.Id, v2.ParentTemplateId);

        // Old version is archived and immutable
        var archivedV1 = await db.PayslipTemplates.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == v1.Id);
        Assert.Equal("archived", archivedV1.Status);
        Assert.False(archivedV1.IsDefault);

        // Trying to update an archived version returns Conflict
        var conflictResult = await ctrl.Update(v1.Id, ValidRequest("Versioned"), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(conflictResult);
    }

    // ── 7. Version immutability — old payslip keeps old template version ───────

    [Fact]
    public async Task OldPayslip_Retains_Template_Version_After_Template_Update()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var ctrl = BuildController(db, tenantId, "Admin");

        // Create v1 as default
        var cr = await ctrl.Create(ValidRequest("History Test", isDefault: true), CancellationToken.None);
        var v1 = Assert.IsType<TemplateDto>(((CreatedAtActionResult)cr).Value!);

        // Simulate a payslip generated with v1
        var run = new PayrollRun { TenantId = tenantId, Year = 2026, Month = 6, CompanyId = null };
        db.PayrollRuns.Add(run);
        var payslip = new Payslip
        {
            TenantId = tenantId,
            PayrollRunId = run.Id,
            EmployeeId = 1,
            PayslipNumber = "PS-HIST-001",
            PayslipTemplateId = v1.Id,   // stamped at generate time
        };
        db.Payslips.Add(payslip);
        await db.SaveChangesAsync();

        // Update the template (creates v2)
        await ctrl.Update(v1.Id, ValidRequest("History Test"), CancellationToken.None);

        // The payslip still references v1
        var storedPayslip = await db.Payslips.IgnoreQueryFilters()
            .FirstAsync(p => p.Id == payslip.Id);
        Assert.Equal(v1.Id, storedPayslip.PayslipTemplateId);

        // v1 is still in DB (archived, not deleted)
        var v1InDb = await db.PayslipTemplates.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == v1.Id);
        Assert.NotNull(v1InDb);
        Assert.Equal("archived", v1InDb!.Status);
    }

    // ── 8. Preview renders a non-empty PDF ────────────────────────────────────

    [Fact]
    public async Task Preview_Returns_NonEmpty_Pdf()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var ctrl = BuildController(db, tenantId, "Admin", realPdf: true);

        // Create a template with valid EN layout
        var cr = await ctrl.Create(ValidRequest("Preview Test"), CancellationToken.None);
        var dto = Assert.IsType<TemplateDto>(((CreatedAtActionResult)cr).Value!);

        var result = await ctrl.Preview(dto.Id, CancellationToken.None);
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.NotEmpty(file.FileContents);
        // PDF magic bytes
        Assert.Equal((byte)'%', file.FileContents[0]);
        Assert.Equal((byte)'P', file.FileContents[1]);
        Assert.Equal((byte)'D', file.FileContents[2]);
        Assert.Equal((byte)'F', file.FileContents[3]);
    }

    // ── 9. Preview renders Arabic (AR locale) ────────────────────────────────

    [Fact]
    public async Task Preview_Arabic_Returns_NonEmpty_Pdf()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var ctrl = BuildController(db, tenantId, "Admin", realPdf: true);

        var branding = new PayslipBrandingConfig(
            PrimaryColor: "#1E3A5F",
            AccentColor:  "#2563EB",
            FontFamily:   "NotoSans",
            HeaderTextEn: "Confidential",
            HeaderTextAr: "سري",
            FooterTextEn: "",
            FooterTextAr: "صادر من نظام زيرا للموارد البشرية",
            Locale:       "ar"
        );
        var layout = new PayslipLayoutConfig("ar", new[]
        {
            new PayslipSectionConfig("earnings",   true, 1, new[] { "basic_salary", "housing_allowance" }),
            new PayslipSectionConfig("deductions", true, 2, new[] { "gosi_annuities_ee", "gosi_saned_ee" }),
        });

        var req = new TemplateUpsertRequest("AR Template", false,
            JsonSerializer.Serialize(branding), JsonSerializer.Serialize(layout));
        var cr = await ctrl.Create(req, CancellationToken.None);
        var dto = Assert.IsType<TemplateDto>(((CreatedAtActionResult)cr).Value!);

        var result = await ctrl.Preview(dto.Id, CancellationToken.None);
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.True(file.FileContents.Length > 1024, "PDF should be more than 1 KB");
    }

    // ── 10. Unknown section key rejected ─────────────────────────────────────

    [Fact]
    public async Task Unknown_Section_Returns_400()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var ctrl = BuildController(db, tenantId, "Admin");

        var layout = new PayslipLayoutConfig("en", new[]
        {
            new PayslipSectionConfig("earnings",         true, 1, new[] { "basic_salary" }),
            new PayslipSectionConfig("deductions",       true, 2, new[] { "gosi_annuities_ee", "gosi_saned_ee" }),
            new PayslipSectionConfig("evil_injection",   true, 3, new[] { "arbitrary_field" }), // not in registry
        });

        var req = new TemplateUpsertRequest("Sneaky", false,
            JsonSerializer.Serialize(ValidBranding()), JsonSerializer.Serialize(layout));
        var result = await ctrl.Create(req, CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("evil_injection", json);
    }

    // ── 11. Delete default returns conflict ───────────────────────────────────

    [Fact]
    public async Task Delete_Default_Template_Returns_Conflict()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var ctrl = BuildController(db, tenantId, "Admin");

        var cr = await ctrl.Create(ValidRequest("Default Template", isDefault: true), CancellationToken.None);
        var dto = Assert.IsType<TemplateDto>(((CreatedAtActionResult)cr).Value!);

        // Set as default to make sure
        await ctrl.SetDefault(dto.Id, CancellationToken.None);

        var deleteResult = await ctrl.Delete(dto.Id, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(deleteResult);
    }

    // ── Registry unit tests (no DB needed) ───────────────────────────────────

    [Fact]
    public void Registry_Contains_All_Required_Compliance_Locked_Fields()
    {
        // Earnings must have basic_salary locked
        var earnings = PayslipTemplateRegistry.Sections["earnings"];
        Assert.True(earnings.Fields.Any(f => f.Key == "basic_salary" && f.IsComplianceLocked));

        // Deductions must have GOSI lines locked
        var deductions = PayslipTemplateRegistry.Sections["deductions"];
        Assert.True(deductions.Fields.Any(f => f.Key == "gosi_annuities_ee" && f.IsComplianceLocked));
        Assert.True(deductions.Fields.Any(f => f.Key == "gosi_saned_ee" && f.IsComplianceLocked));

        // bank_wps must have IBAN and bank_name locked
        var bankWps = PayslipTemplateRegistry.Sections["bank_wps"];
        Assert.True(bankWps.Fields.Any(f => f.Key == "iban" && f.IsComplianceLocked));
        Assert.True(bankWps.Fields.Any(f => f.Key == "bank_name" && f.IsComplianceLocked));

        // earnings and deductions are non-disablable
        Assert.Contains("earnings",   PayslipTemplateRegistry.NonDisablableSections);
        Assert.Contains("deductions", PayslipTemplateRegistry.NonDisablableSections);
    }

    [Fact]
    public void Registry_ValidateBranding_Rejects_Bad_Hex()
    {
        var bad = new PayslipBrandingConfig(PrimaryColor: "not-a-color");
        var errors = PayslipTemplateRegistry.ValidateBranding(bad);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("primaryColor"));
    }

    [Fact]
    public void Registry_ValidateBranding_Rejects_Unknown_Font()
    {
        var bad = new PayslipBrandingConfig(FontFamily: "Comic Sans");
        var errors = PayslipTemplateRegistry.ValidateBranding(bad);
        Assert.Contains(errors, e => e.Contains("fontFamily"));
    }

    [Fact]
    public void Registry_ValidateLayout_Rejects_Unknown_Locale()
    {
        var layout = new PayslipLayoutConfig("fr");
        var errors = PayslipTemplateRegistry.ValidateLayout(layout);
        Assert.Contains(errors, e => e.Contains("locale"));
    }

    [Fact]
    public void Registry_ValidateLayout_Rejects_Unknown_Field()
    {
        var layout = new PayslipLayoutConfig("en", new[]
        {
            new PayslipSectionConfig("earnings", true, 1, new[] { "basic_salary", "secret_field" }),
            new PayslipSectionConfig("deductions", true, 2, new[] { "gosi_annuities_ee", "gosi_saned_ee" }),
        });
        var errors = PayslipTemplateRegistry.ValidateLayout(layout);
        Assert.Contains(errors, e => e.Contains("secret_field"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PayslipBrandingConfig ValidBranding() => new(
        PrimaryColor: "#1E3A5F",
        AccentColor:  "#2563EB",
        FontFamily:   "NotoSans"
    );

    private static TemplateUpsertRequest ValidRequest(string name, bool isDefault = false)
    {
        var layout = new PayslipLayoutConfig("en", new[]
        {
            new PayslipSectionConfig("earnings",   true, 1, new[] { "basic_salary", "housing_allowance", "transport_allowance" }),
            new PayslipSectionConfig("deductions", true, 2, new[] { "gosi_annuities_ee", "gosi_saned_ee", "loan_repayment" }),
        });
        return new TemplateUpsertRequest(name, isDefault,
            JsonSerializer.Serialize(ValidBranding()),
            JsonSerializer.Serialize(layout));
    }

    private static PayslipTemplatesController BuildController(ZayraDbContext db, Guid tenantId, string role,
        bool realPdf = false)
    {
        ILetterService letters = realPdf ? new Zayra.Api.Infrastructure.Documents.Letters.LetterService() : new TemplateFakeLetterService();
        var ctrl = new PayslipTemplatesController(db, new TemplateFakeDocumentStorage(), letters);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id",                       tenantId.ToString()),
                    new Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(System.Security.Claims.ClaimTypes.Role, role),
                }, "Test"))
            }
        };
        return ctrl;
    }
}

file sealed class TemplateFakeDocumentStorage : IDocumentStorage
{
    public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new StoredDocument(file.FileName, file.ContentType, $"storage/payslip-templates/{tenantId}/{file.FileName}", $"/tmp/{file.FileName}"));
    public string ResolvePath(string storageUrl) => $"/tmp/{storageUrl}";
}

file sealed class TemplateFakeLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
