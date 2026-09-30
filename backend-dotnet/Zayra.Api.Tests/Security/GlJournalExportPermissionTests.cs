using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The GL / ERP hand-off, judged by the REAL seeded role bundles: Payroll Manager produces the journal
/// export (maker), Finance Approver attests that the client's ERP imported it (checker).
///
/// <para>THE DEFECT. Export accepted <c>finance.gl.export || finance.gl.manage</c> and ERP confirmation
/// <c>finance.erp.confirm || finance.gl.manage</c>. Neither narrow key was in the permission catalog, so both
/// collapsed to <c>finance.gl.manage</c>: the Payroll Manager who produced a journal could also attest it
/// had been posted — the one self-attestation the separate confirm step exists to prevent — and the
/// Finance Approver, the role named for exactly that sign-off, could not do it at all.</para>
/// </summary>
public class GlJournalExportPermissionTests
{
    private const string Period = "2026-08";

    [Theory]
    [InlineData("Admin", 201)]
    [InlineData("Payroll Manager", 201)]
    [InlineData("Finance Approver", 403)]
    public async Task Export_IsTheMakersStep(string role, int expected)
    {
        var (db, tenantId) = await NewTenantAsync("gl-export");
        SeedLedger(db, tenantId);

        var result = await Controller(db, await CallerAsync(db, tenantId, role))
            .Create(Period, null, null, null, false, false, "generic-csv", CancellationToken.None);

        StatusOf(result).Should().Be(expected);
        (await db.GlJournalExports.CountAsync()).Should().Be(expected == 201 ? 1 : 0);
    }

    [Theory]
    [InlineData("Finance Approver", 200)]
    [InlineData("Admin", 200)]
    [InlineData("Payroll Manager", 403)]
    [InlineData("Payroll Officer", 403)]
    public async Task ConfirmingTheErpImport_IsTheCheckersStep(string role, int expected)
    {
        var (db, tenantId) = await NewTenantAsync("gl-confirm");
        var exportId = await ExportAsPayrollManagerAsync(db, tenantId);

        var result = await Controller(db, await CallerAsync(db, tenantId, role))
            .Confirm(exportId, new ErpImportConfirmationRequest("ERP-DOC-1001"), CancellationToken.None);

        StatusOf(result).Should().Be(expected);
        var export = await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == exportId);
        export.Status.Should().Be(expected == 200 ? GlJournalExportStatuses.Confirmed : GlJournalExportStatuses.Exported);
        (await db.FinanceGlEntries.AsNoTracking().AllAsync(e =>
                e.ErpPostingStatus == (expected == 200 ? ErpPostingStatuses.Posted : ErpPostingStatuses.Exported)))
            .Should().BeTrue("a refused confirmation must not stamp the ledger as posted");
    }

    [Theory]
    [InlineData("Finance Approver", 200)]
    [InlineData("Payroll Manager", 403)]
    public async Task RecordingAnErpRejection_IsTheCheckersStep(string role, int expected)
    {
        var (db, tenantId) = await NewTenantAsync("gl-reject");
        var exportId = await ExportAsPayrollManagerAsync(db, tenantId);

        var result = await Controller(db, await CallerAsync(db, tenantId, role))
            .Reject(exportId, new ErpImportRejectionRequest("ERP refused: unknown cost centre"), CancellationToken.None);

        StatusOf(result).Should().Be(expected);
        (await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == exportId)).Status
            .Should().Be(expected == 200 ? GlJournalExportStatuses.Rejected : GlJournalExportStatuses.Exported);
    }

    [Fact]
    public async Task NoSeededRoleButAdmin_CanBothExportAndAttestTheErpImport()
    {
        var (db, tenantId) = await NewTenantAsync("gl-sod");
        var roles = await db.Roles.AsNoTracking().Where(r => r.TenantId == tenantId).Select(r => r.Name).ToListAsync();
        roles.Should().Contain(new[] { "Payroll Manager", "Finance Approver" });

        foreach (var role in roles.Where(r => r != "Admin"))
        {
            var held = await PermissionsOfAsync(db, tenantId, role);
            (held.Contains("finance.gl.manage") && held.Contains("finance.erp.confirm"))
                .Should().BeFalse($"{role} would be maker and checker of the same journal");
        }
    }

    // ── Maker-checker by identity, not only by key ─────────────────────────────────────────────
    //
    // The keys keep the seeded roles apart, but Admin holds both, and a tenant can give one role both. The
    // person who exported a journal must never be the one who attests the ERP took it (or refused it), so
    // the check is on WHO exported, whatever keys the caller holds. Same rule, code and wording style as the
    // settlement approve/pay checks (#124, #136).

    [Theory]
    [InlineData("confirm")]
    [InlineData("reject")]
    public async Task TheAdminWhoExported_CannotDecideTheErpOutcome_OfTheirOwnExport(string step)
    {
        var (db, tenantId) = await NewTenantAsync("gl-sod-admin");
        SeedLedger(db, tenantId);
        var admin = await CallerAsync(db, tenantId, "Admin");
        StatusOf(await Controller(db, admin).Create(Period, null, null, null, false, false, "generic-csv", CancellationToken.None))
            .Should().Be(201);
        db.ChangeTracker.Clear();
        var exportId = (await db.GlJournalExports.AsNoTracking().SingleAsync()).Id;

        var result = await Decide(db, admin, exportId, step);

        AssertSegregationRefusal(result, step);
        await AssertNothingDecidedAsync(db, exportId);
        await AssertRefusalAuditedAsync(db, exportId, step, admin);
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("reject")]
    public async Task ARoleGrantedBothKeys_StillNeedsASecondPerson(string step)
    {
        // A tenant admin gives Payroll Manager the checker key as well. The key check now passes for every
        // Payroll Manager, so only the identity check stands between the exporter and their own attestation.
        var (db, tenantId) = await NewTenantAsync("gl-sod-granted");
        await GrantAsync(db, tenantId, "Payroll Manager", GlJournalExportsController.ErpConfirmPermission);
        SeedLedger(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        StatusOf(await Controller(db, maker).Create(Period, null, null, null, false, false, "generic-csv", CancellationToken.None))
            .Should().Be(201);
        db.ChangeTracker.Clear();
        var exportId = (await db.GlJournalExports.AsNoTracking().SingleAsync()).Id;

        AssertSegregationRefusal(await Decide(db, maker, exportId, step), step);
        await AssertNothingDecidedAsync(db, exportId);

        var colleague = await CallerAsync(db, tenantId, "Payroll Manager");
        StatusOf(await Decide(db, colleague, exportId, step)).Should().Be(200,
            "a different person holding the checker key decides the outcome");
        (await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == exportId)).Status.Should().Be(
            step == "confirm" ? GlJournalExportStatuses.Confirmed : GlJournalExportStatuses.Rejected);
    }

    [Fact]
    public async Task ACheckerWithNoUserId_IsRefused_BecauseTheyCannotBeCheckedAgainstTheMaker()
    {
        var (db, tenantId) = await NewTenantAsync("gl-sod-anon");
        var exportId = await ExportAsPayrollManagerAsync(db, tenantId);
        var approver = await CallerAsync(db, tenantId, "Finance Approver");
        var anonymous = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            approver.Claims.Where(c => c.Type != System.Security.Claims.ClaimTypes.NameIdentifier), "Test"));

        StatusOf(await Decide(db, anonymous, exportId, "confirm")).Should().Be(403);
        await AssertNothingDecidedAsync(db, exportId);
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    private static Task<IActionResult> Decide(ZayraDbContext db, System.Security.Claims.ClaimsPrincipal caller, Guid exportId, string step) =>
        step == "confirm"
            ? Controller(db, caller).Confirm(exportId, new ErpImportConfirmationRequest("ERP-DOC-2001"), CancellationToken.None)
            : Controller(db, caller).Reject(exportId, new ErpImportRejectionRequest("ERP refused: period closed"), CancellationToken.None);

    private static void AssertSegregationRefusal(IActionResult result, string step)
    {
        StatusOf(result).Should().Be(409, $"the exporter must not {step} their own journal");
        var body = System.Text.Json.JsonSerializer.SerializeToElement(((ObjectResult)result).Value);
        body.GetProperty("error").GetString().Should().Be("segregation_of_duties");
        body.GetProperty("message").GetString().Should().StartWith("You exported this journal, so you cannot also");
    }

    private static async Task AssertNothingDecidedAsync(ZayraDbContext db, Guid exportId)
    {
        db.ChangeTracker.Clear();
        var export = await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == exportId);
        export.Status.Should().Be(GlJournalExportStatuses.Exported);
        export.ConfirmedByUserId.Should().BeNull();
        export.RejectionReason.Should().BeNull();
        (await db.FinanceGlEntries.AsNoTracking().AllAsync(e => e.ErpPostingStatus == ErpPostingStatuses.Exported))
            .Should().BeTrue("a refused decision must not stamp the ledger");
    }

    private static async Task AssertRefusalAuditedAsync(
        ZayraDbContext db, Guid exportId, string step, System.Security.Claims.ClaimsPrincipal caller)
    {
        var callerId = Guid.Parse(caller.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var audit = await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.EntityId == exportId.ToString() && a.Action == $"finance.gl.journal_export.erp_{step}_refused");
        audit.UserId.Should().Be(callerId, "the refusal names who tried");
        audit.Metadata.Should().Contain("segregation_of_duties");
    }

    private static GlJournalExportsController Controller(ZayraDbContext db, System.Security.Claims.ClaimsPrincipal caller)
    {
        var exports = new JournalExportService(db, [new GenericCsvJournalFormatter()]);
        return Bind(new GlJournalExportsController(db, exports, new PeriodHandoffReconciler(db, exports)), caller);
    }

    private static async Task<Guid> ExportAsPayrollManagerAsync(ZayraDbContext db, Guid tenantId)
    {
        SeedLedger(db, tenantId);
        var created = await Controller(db, await CallerAsync(db, tenantId, "Payroll Manager"))
            .Create(Period, null, null, null, false, false, "generic-csv", CancellationToken.None);
        StatusOf(created).Should().Be(201, "the maker's export is the artifact the checker confirms");
        db.ChangeTracker.Clear();
        return (await db.GlJournalExports.AsNoTracking().SingleAsync()).Id;
    }

    private static void SeedLedger(ZayraDbContext db, Guid tenantId)
    {
        db.FinanceGlEntries.AddRange(
            new FinanceGlEntry
            {
                TenantId = tenantId, SourceModule = "Payroll", SourceEntityId = Guid.NewGuid(), SourceEntityRef = "PR-AUG",
                EventType = "Accrual", DebitAccount = "5000 - Salaries", CreditAccount = "2100 - Payable",
                Amount = 12_500m, Currency = "SAR", EntryDate = new DateOnly(2026, 8, 31), Period = Period,
                Description = "August payroll accrual",
            },
            new FinanceGlEntry
            {
                TenantId = tenantId, SourceModule = "Payroll", SourceEntityId = Guid.NewGuid(), SourceEntityRef = "PR-AUG-GOSI",
                EventType = "Accrual", DebitAccount = "5100 - GOSI Employer", CreditAccount = "2200 - GOSI Payable",
                Amount = 1_462.50m, Currency = "SAR", EntryDate = new DateOnly(2026, 8, 31), Period = Period,
                Description = "August GOSI employer share",
            });
        db.SaveChanges();
    }
}
