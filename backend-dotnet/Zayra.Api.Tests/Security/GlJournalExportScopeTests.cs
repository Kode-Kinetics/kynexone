using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// D2 — <c>includeUnattributed</c> is a GROUP-ONLY privilege.
///
/// <para>THE DEFECT. The flag makes <c>JournalExportBuilder</c> add every NULL-company
/// <see cref="FinanceGlEntry"/> in the tenant to the result. Those rows are tenant-wide by definition —
/// every pre-POD-B1b ledger line is unattributed — so a company-scoped caller who passed the flag
/// received amounts, account codes, narratives and source references belonging to sibling legal
/// entities. The response body even advertised it: <i>"re-run with includeUnattributed=true to include
/// them."</i></para>
///
/// <para>WHY <c>ScopeError</c> DID NOT CATCH IT. That guard validates only <c>companyId</c>. A caller
/// passing their OWN authorised company passed it, and the flag then widened the result behind that
/// valid check. Authorising the company is not the same as authorising the tenant-wide residue, which
/// is why D2 adds a separate guard rather than modifying the existing one.</para>
///
/// <para>These tests pin the boundary on EVERY entry point that accepts the flag — preview, artifact
/// creation and reconciliation — and prove a denied request leaves no export artifact behind.</para>
/// </summary>
public class GlJournalExportScopeTests
{
    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <param name="scopedCompany">null ⇒ group-level token; otherwise a companies-mode token.</param>
    private static GlJournalExportsController MakeCtrl(
        ZayraDbContext db, Guid tenantId, Guid? scopedCompany, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Finance User"),
        };
        foreach (var p in permissions) claims.Add(new Claim("permission", p));

        if (scopedCompany is Guid c)
            claims.Add(new Claim(EntityScopeContext.V2ClaimType,
                JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { c.ToString() } })));
        else
            claims.Add(new Claim("is_group_scope", "true"));

        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        var exports = new JournalExportService(db, [new GenericCsvJournalFormatter()]);
        var ctrl = new GlJournalExportsController(db, exports, new PeriodHandoffReconciler(db, exports))
        {
            ControllerContext = new ControllerContext { HttpContext = httpCtx },
        };
        return ctrl;
    }

    // ── Negative: a company-scoped caller may not widen to tenant-wide rows ────

    [Fact]
    public async Task Preview_WithIncludeUnattributed_IsDeniedToACompanyScopedCaller()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var ctrl = MakeCtrl(db, tenantId, scopedCompany: mine, "finance.gl.read");

        // NOTE: companyId is the caller's OWN authorised company, so ScopeError passes. The refusal
        // must come from the unattributed guard, which is the whole point of D2.
        var result = await ctrl.Preview("2026-07", null, mine, null, includeUnattributed: true);

        result.Should().BeOfType<ForbidResult>(
            "unattributed rows are tenant-wide, so widening to them is a group-level privilege even when " +
            "the companyId supplied is one the caller legitimately owns");
    }

    [Fact]
    public async Task Create_WithIncludeUnattributed_IsDeniedAndPersistsNoArtifact()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var ctrl = MakeCtrl(db, tenantId, scopedCompany: mine, "finance.gl.manage");

        var result = await ctrl.Create("2026-07", null, mine, null, includeUnattributed: true);

        result.Should().BeOfType<ForbidResult>();
        db.GlJournalExports.Should().BeEmpty(
            "authorization runs BEFORE building, persisting or stamping — a denied request must leave no " +
            "artifact and mutate no ERP status");
        db.GlJournalExportLines.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconciliation_WithIncludeUnattributed_IsDeniedToACompanyScopedCaller()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var ctrl = MakeCtrl(db, tenantId, scopedCompany: mine, "finance.gl.read");

        var result = await ctrl.Reconciliation("2026-07", null, mine, includeUnattributed: true);

        result.Should().BeOfType<ForbidResult>(
            "the same rule must hold on every entry point that accepts the flag, not just the exporting ones");
    }

    [Fact]
    public async Task CompanyScopedCaller_MayStillWorkNormally_WithoutTheFlag()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var ctrl = MakeCtrl(db, tenantId, scopedCompany: mine, "finance.gl.read");

        var result = await ctrl.Preview("2026-07", null, mine, null, includeUnattributed: false);

        result.Should().NotBeOfType<ForbidResult>(
            "D2 must restrict only the tenant-wide widening — ordinary company-scoped export must keep working");
    }

    // ── Positive: a group caller retains the legacy-row capability ─────────────

    [Fact]
    public async Task GroupCaller_MayDeliberatelyIncludeUnattributedRows()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var ctrl = MakeCtrl(db, tenantId, scopedCompany: null, "finance.gl.read");

        var result = await ctrl.Preview("2026-07", null, null, null, includeUnattributed: true);

        result.Should().NotBeOfType<ForbidResult>(
            "including legacy NULL-company rows is a legitimate group-level operation — every pre-POD-B1b " +
            "ledger line is unattributed, so a group reconciliation must be able to see them");
    }

    // ── The permission check still comes first ────────────────────────────────

    [Fact]
    public async Task MissingPermission_IsRefusedBeforeScopeIsEvenConsidered()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var ctrl = MakeCtrl(db, tenantId, scopedCompany: null /* group */);

        var result = await ctrl.Preview("2026-07", null, null, null, includeUnattributed: true);

        result.Should().BeOfType<ForbidResult>(
            "company scope is an ADDITIONAL control; it never replaces the finance.gl.* permission gate");
    }
}
