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

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

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
