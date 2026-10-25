using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostAccountingKit;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-B slice 3 — <see cref="GlResolutionContext"/> <c>init</c> extensions and class-aware account choice.
/// T-039 (company+class &gt; company &gt; tenant+class &gt; tenant &gt; driver default), finding 4 (arity untouched),
/// "preview equals post" at the resolver layer.
/// </summary>
public class GlResolutionContextCostCentreTests
{
    private const string Driver = "EARN:BASIC";

    private sealed class Fx
    {
        public Guid Tid; public Company Co = null!;
        public GlAccount TenantPlain = null!, TenantSelling = null!, CoPlain = null!, CoSelling = null!;
    }

    private static async Task<Fx> Seed(Zayra.Api.Data.ZayraDbContext db, bool costingOn = false)
    {
        var f = new Fx { Tid = Guid.NewGuid() };
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, f.Tid, default);
        await db.SaveChangesAsync();
        f.Co = await AddCompany(db, f.Tid, costingOn);
        f.TenantPlain = await AddAccount(db, f.Tid, null, "5001", "Expense");
        f.TenantSelling = await AddAccount(db, f.Tid, null, "5011", "Expense");
        f.CoPlain = await AddAccount(db, f.Tid, f.Co.Id, "5021", "Expense");
        f.CoSelling = await AddAccount(db, f.Tid, f.Co.Id, "5031", "Expense");
        return f;
    }

    private static async Task Map(Zayra.Api.Data.ZayraDbContext db, Guid tid, Guid? companyId, GlAccount acct, string? cls = null, Guid? cc = null)
    {
        db.GlAccountMappings.Add(new GlAccountMapping { TenantId = tid, CompanyId = companyId, DriverKey = Driver, AccountId = acct.Id, FunctionalClass = cls, SegmentCostCenterId = cc });
        await db.SaveChangesAsync();
    }

    private static string Code(GlResolutionContext ctx, string? cls) => GlAccountResolver.AccountFor(Driver, cls, ctx).Code;

    [Fact]
    public async Task AccountFor_WithNoClassRows_EqualsToday_ForEveryClass()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        await Map(db, f.Tid, null, f.TenantPlain);
        var ctx = await GlAccountResolver.LoadAsync(db, f.Tid, f.Co.Id, default);

        foreach (var cls in new string?[] { null, "SELLING", "COGS", "G_AND_A" })
            GlAccountResolver.AccountFor(Driver, cls, ctx).Should().Be(GlAccountResolver.Resolve(Driver, ctx.Overrides, ctx.DriverDefaults));
        Code(ctx, null).Should().Be("5001");
    }

    [Fact]
    public async Task AccountFor_FallsToDriverDefault_WhenNothingIsMapped()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var ctx = await GlAccountResolver.LoadAsync(db, f.Tid, f.Co.Id, default);

        Code(ctx, "SELLING").Should().Be("5001");   // the seeded default for EARN:BASIC
        GlAccountResolver.AccountFor(Driver, "SELLING", ctx).Should().Be(PayrollGlCatalog.Defaults[Driver]);
    }

    [Fact]
    public async Task AccountFor_TenantPlusClass_BeatsTenant_ForThatClassOnly()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        await Map(db, f.Tid, null, f.TenantPlain);
        await Map(db, f.Tid, null, f.TenantSelling, "SELLING");
        var ctx = await GlAccountResolver.LoadAsync(db, f.Tid, f.Co.Id, default);

        Code(ctx, "SELLING").Should().Be("5011");
        Code(ctx, "COGS").Should().Be("5001");
        Code(ctx, null).Should().Be("5001");
    }

    [Fact]
    public async Task AccountFor_Company_BeatsTenantPlusClass()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        await Map(db, f.Tid, null, f.TenantPlain);
        await Map(db, f.Tid, null, f.TenantSelling, "SELLING");
        await Map(db, f.Tid, f.Co.Id, f.CoPlain);
        var ctx = await GlAccountResolver.LoadAsync(db, f.Tid, f.Co.Id, default);

        Code(ctx, "SELLING").Should().Be("5021", "a company mapping outranks a tenant class mapping");
        Code(ctx, null).Should().Be("5021");
    }

    [Fact]
    public async Task AccountFor_CompanyPlusClass_BeatsEverything()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        await Map(db, f.Tid, null, f.TenantPlain);
        await Map(db, f.Tid, null, f.TenantSelling, "SELLING");
        await Map(db, f.Tid, f.Co.Id, f.CoPlain);
        await Map(db, f.Tid, f.Co.Id, f.CoSelling, "SELLING");
        var ctx = await GlAccountResolver.LoadAsync(db, f.Tid, f.Co.Id, default);

        Code(ctx, "SELLING").Should().Be("5031");
        Code(ctx, "COGS").Should().Be("5021");
        Code(ctx, null).Should().Be("5021");
    }

    [Fact]
    public async Task ClassRows_NeverLeakIntoTheClasslessOverrides()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        await Map(db, f.Tid, f.Co.Id, f.CoSelling, "SELLING");
        var ctx = await GlAccountResolver.LoadAsync(db, f.Tid, f.Co.Id, default);

        ctx.Overrides.ContainsKey(Driver).Should().BeFalse("a class-only mapping must not become the driver's plain override");
        ctx.ClassOverrides.Should().ContainKey((Driver, "SELLING"));
        GlAccountResolver.AccountLabel(Driver, ctx).Should().StartWith("5001", "the classless label is unchanged");
    }

    [Fact]
    public async Task PreviewEqualsPost_BothReadTheSameContext()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        await Map(db, f.Tid, f.Co.Id, f.CoSelling, "SELLING");
        var preview = await GlAccountResolver.LoadAsync(db, f.Tid, f.Co.Id, default);
        var post = await GlAccountResolver.LoadAsync(db, f.Tid, f.Co.Id, default);

        foreach (var cls in new string?[] { null, "SELLING", "COGS" })
            GlAccountResolver.AccountFor(Driver, cls, preview).Should().Be(GlAccountResolver.AccountFor(Driver, cls, post));
    }

    // ── new context data ────────────────────────────────────────────────────

    [Fact]
    public async Task Context_CarriesAccountTypeAndRule_ByCode()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        await AddAccount(db, f.Tid, f.Co.Id, "6001", "Expense", "Required");
        await AddAccount(db, f.Tid, f.Co.Id, "2101", "Liability", "NotAllowed");
        var ctx = await GlAccountResolver.LoadAsync(db, f.Tid, f.Co.Id, default);

        ctx.AccountRules["6001"].Should().Be(("Expense", "Required"));
        ctx.AccountRules["2101"].Should().Be(("Liability", "NotAllowed"));
        ctx.AccountRules["5001"].Should().Be(("Expense", "Optional"));
    }

    [Fact]
    public async Task Context_AccountRules_CompanyAccountWinsOverTenantAccountWithTheSameCode()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        await AddAccount(db, f.Tid, null, "7001", "Expense", "Optional");
        await AddAccount(db, f.Tid, f.Co.Id, "7001", "Expense", "Required");
        var ctx = await GlAccountResolver.LoadAsync(db, f.Tid, f.Co.Id, default);

        ctx.AccountRules["7001"].Rule.Should().Be("Required");
    }

    [Fact]
    public async Task Context_FixedCostCentres_AreTheCompanyMappings_AndIgnoreClassRowsAndTenantDefaults()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var cc = await AddCostCentre(db, f.Tid, f.Co.Id, "SALES");
        var legacy = await AddCostCentre(db, f.Tid, f.Co.Id, "LEGACY");
        await Map(db, f.Tid, f.Co.Id, f.CoPlain, null, cc.Id);
        db.GlAccountMappings.Add(new GlAccountMapping { TenantId = f.Tid, CompanyId = null, DriverKey = "EARN:HOUSING", AccountId = f.TenantPlain.Id, SegmentCostCenterId = legacy.Id });
        await db.SaveChangesAsync();
        var ctx = await GlAccountResolver.LoadAsync(db, f.Tid, f.Co.Id, default);

        ctx.FixedCostCentres.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, Guid>(Driver, cc.Id));
    }

    [Fact]
    public async Task Context_CostCentres_AreThisCompanysLiveRows_WithTheirSnapshotFields()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db, costingOn: true);
        var parent = await AddCostCentre(db, f.Tid, f.Co.Id, "OPS", cls: "OPERATIONS");
        var child = await AddCostCentre(db, f.Tid, f.Co.Id, "SALES", cls: "SELLING");
        child.ParentId = parent.Id; child.GlSegment = "100200";
        var other = await AddCompany(db, f.Tid);
        var foreign = await AddCostCentre(db, f.Tid, other.Id, "FOREIGN");
        var group = await AddCostCentre(db, f.Tid, null, "GROUP");
        var gone = await AddCostCentre(db, f.Tid, f.Co.Id, "GONE", deleted: true);
        await db.SaveChangesAsync();
        var ctx = await GlAccountResolver.LoadAsync(db, f.Tid, f.Co.Id, default);

        ctx.CostCentres.Keys.Should().BeEquivalentTo(new[] { parent.Id, child.Id });
        var c = ctx.CostCentres[child.Id];
        (c.Code, c.Name, c.GlSegment, c.FunctionalClass, c.ParentId, c.IsActive).Should().Be(("SALES", "CC SALES", "100200", "SELLING", (Guid?)parent.Id, true));
    }

    [Fact]
    public async Task Context_Flags_FollowTheCompany_AndAreOffForGroupOrUnknown()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var cc = await AddCostCentre(db, f.Tid, f.Co.Id, "DEFAULT");
        f.Co.CostAccountingEnabled = true; f.Co.DefaultCostCenterId = cc.Id; f.Co.CostCentreSmallCellMin = 3;
        await db.SaveChangesAsync();

        var on = await GlAccountResolver.LoadAsync(db, f.Tid, f.Co.Id, default);
        (on.CostAccountingEnabled, on.CompanyDefaultCostCentreId, on.CostCentreSmallCellMin).Should().Be((true, (Guid?)cc.Id, (short)3));

        var group = await GlAccountResolver.LoadAsync(db, f.Tid, null, default);
        (group.CostAccountingEnabled, group.CompanyDefaultCostCentreId, group.CostCentres.Count).Should().Be((false, (Guid?)null, 0));
    }

    [Fact]
    public async Task Context_WhenCostingIsOff_LoadsNoCostCentres_SoNothingChangesForOldTenants()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db, costingOn: false);
        await AddCostCentre(db, f.Tid, f.Co.Id, "SALES");
        var ctx = await GlAccountResolver.LoadAsync(db, f.Tid, f.Co.Id, default);

        ctx.CostAccountingEnabled.Should().BeFalse();
        ctx.CostCentres.Should().BeEmpty();
    }

    [Fact]
    public void Empty_Context_HasSafeDefaults()
    {
        var e = GlResolutionContext.Empty;
        (e.CostAccountingEnabled, e.CompanyDefaultCostCentreId, e.CostCentreSmallCellMin).Should().Be((false, (Guid?)null, (short)0));
        e.AccountRules.Should().BeEmpty();
        e.ClassOverrides.Should().BeEmpty();
        e.FixedCostCentres.Should().BeEmpty();
        e.CostCentres.Should().BeEmpty();
        GlAccountResolver.AccountFor("EARN:BASIC", "SELLING", e).Should().Be(PayrollGlCatalog.Defaults["EARN:BASIC"]);
    }

    // ── finding 4: arity ────────────────────────────────────────────────────

    [Fact]
    public void NewContextData_IsInitOnly_SoBuilderAndContextArityAreUnchanged()
    {
        var ctor = typeof(GlResolutionContext).GetConstructors().Single(c => c.GetParameters().Length == 3);
        ctor.GetParameters().Select(p => p.Name).Should().Equal("Overrides", "DriverDefaults", "Drivers");

        foreach (var name in new[] { "AccountRules", "ClassOverrides", "FixedCostCentres", "CostCentres", "CostAccountingEnabled", "CompanyDefaultCostCentreId", "CostCentreSmallCellMin" })
        {
            var setter = typeof(GlResolutionContext).GetProperty(name)!.SetMethod!;
            setter.ReturnParameter.GetRequiredCustomModifiers().Select(t => t.Name).Should().Contain("IsExternalInit", $"{name} must be init-only");
        }
    }
}
