using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Shared harness of the benefit-definition tests (feature <c>benefit_definition_v1</c>): one tenant with three companies and two grades, the
/// entitlements controller as HR (<c>entitlements.manage</c>) or Finance (<c>payroll.approve</c> too), and helpers to read the JSON the API returns.
/// </summary>
internal sealed class Bd : IAsyncDisposable
{
    public static readonly DateOnly Today = new(2026, 10, 6);
    public static readonly DateOnly Nov1 = new(2026, 11, 1);
    public static readonly DateOnly Oct31Next = new(2027, 10, 31);
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    public const string CarDriver = "EARN:CAR";

    public ZayraDbContext Db { get; } = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    public Guid Tid { get; } = Guid.NewGuid();
    public Company Alpha { get; } = null!;
    public Company Beta { get; } = null!;
    public Company Gamma { get; } = null!;
    public Grade[] G { get; } = new Grade[3];
    public EntitlementMatrixService Service { get; private set; } = null!;
    public readonly Guid Hr = Guid.NewGuid();
    public readonly Guid HrTwo = Guid.NewGuid();
    public readonly Guid FinanceA = Guid.NewGuid();
    public readonly Guid FinanceB = Guid.NewGuid();

    private Bd()
    {
        Alpha = new Company { TenantId = Tid, LegalNameEn = "Masar Facility Services Co.", DefaultCurrency = "SAR" };
        Beta = new Company { TenantId = Tid, LegalNameEn = "Masar Logistics Co.", DefaultCurrency = "SAR" };
        Gamma = new Company { TenantId = Tid, LegalNameEn = "Masar Catering Co.", DefaultCurrency = "SAR" };
    }

    public static async Task<Bd> Create(bool definitionFlag = true)
    {
        var h = new Bd();
        h.Db.AddRange(h.Alpha, h.Beta, h.Gamma);
        for (var i = 1; i <= 2; i++) { h.G[i] = new Grade { TenantId = h.Tid, Code = $"G{i}", Name = $"Grade {i}", Level = i * 10 }; h.Db.Add(h.G[i]); }
        h.Db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = h.Tid, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        if (definitionFlag) h.Db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = h.Tid, FeatureKey = FeatureKeys.BenefitDefinitionV1, IsEnabled = true });
        h.Db.GlDrivers.AddRange(Driver(h.Tid, "EARN:CAR", "5210"), Driver(h.Tid, "EARN:CAR2", "5211"));
        await h.Db.SaveChangesAsync();
        h.Service = new EntitlementMatrixService(h.Db, new FixedTenantClock(Today));
        return h;
    }

    public static GlDriver Driver(Guid tid, string key, string account) => new()
    {
        TenantId = tid, CompanyId = null, Key = key, Label = key, Category = GlDriverCategories.Earning, PostingSide = "DR",
        DefaultCode = account, DefaultName = $"Account {account}", MatchMode = "Exact", MatchComponentCode = key[(key.IndexOf(':') + 1)..], SortOrder = 70,
    };

    public async Task SetFlag(bool on)
    {
        var row = await Db.TenantFeatureFlags.SingleOrDefaultAsync(f => f.TenantId == Tid && f.FeatureKey == FeatureKeys.BenefitDefinitionV1);
        if (row is null) Db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = Tid, FeatureKey = FeatureKeys.BenefitDefinitionV1, IsEnabled = on });
        else row.IsEnabled = on;
        await Db.SaveChangesAsync();
    }

    /// <summary>The entitlements controller as a signed-in user of <paramref name="tenant"/> (default: this tenant).</summary>
    public EntitlementMatrixController As(Guid user, bool finance = false, Guid[]? companyScope = null, Guid? tenant = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", (tenant ?? Tid).ToString()), new(ClaimTypes.NameIdentifier, user.ToString()), new(ClaimTypes.Role, "HR Director"),
        };
        if (finance) claims.Add(new Claim("permission", "payroll.approve"));
        if (companyScope is not null)
            claims.Add(new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = companyScope })));
        return new EntitlementMatrixController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
        };
    }

    public Task<IActionResult> Add(CreateBenefitRequest req, Guid? user = null, bool finance = false) =>
        As(user ?? Hr, finance).AddBenefit(Service, req, default);

    public Task<IActionResult> Put(string code, UpdateBenefitRequest req, Guid? user = null, bool finance = false) =>
        As(user ?? Hr, finance).UpdateBenefit(Service, code, req, default);

    public Task<IActionResult> PublishGroup(DateOnly from, params MatrixCellInput[] cells) =>
        As(Hr).Publish(Service, new PublishMatrixRequest(null, from, cells.ToList()), false, default);

    public static MatrixCellInput Amount(Grade g, string code, decimal amount) =>
        new() { GradeId = g.Id, ComponentCode = code, Eligible = true, ValueType = "Amount", Amount = amount };

    public async Task<EntitlementMatrixDto> Read(DateOnly? asOf = null, Guid? companyId = null) =>
        (EntitlementMatrixDto)((OkObjectResult)await As(Hr).Matrix(Service, companyId, asOf ?? Today, default)).Value!;

    public async Task<PayComponent[]> Rows(string code, bool tenantWideOnly = false) =>
        (await Db.PayComponents.AsNoTracking().Where(x => x.TenantId == Tid && x.Code == code && (!tenantWideOnly || x.CompanyId == null)).ToListAsync())
            .OrderBy(x => x.EffectiveFrom ?? DateOnly.MinValue).ToArray();

    public Task<int> AuditCount(string action) => Db.AuditLogs.CountAsync(a => a.TenantId == Tid && a.Action == action);

    public ValueTask DisposeAsync() => Db.DisposeAsync();

    // ── reading what the API returned ────────────────────────────────────────────────────────────

    public static int? Status(IActionResult r) => (r as ObjectResult)?.StatusCode;
    public static string Describe(IActionResult r) => r is ObjectResult o ? $"{o.StatusCode}: {JsonSerializer.Serialize(o.Value, Web)}" : r.GetType().Name;
    public static string? Error(IActionResult r) =>
        JsonSerializer.SerializeToElement(((ObjectResult)r).Value, Web).TryGetProperty("error", out var e) ? e.GetString() : null;

    public static MatrixComponentDto Component(IActionResult r)
    {
        Status(r).Should200(Describe(r));
        var value = ((ObjectResult)r).Value!;
        return value as MatrixComponentDto ?? (MatrixComponentDto)value.GetType().GetProperty("component")!.GetValue(value)!;
    }

    public static CreateBenefitRequest Contract(string nameEn, Action<CreateBenefitRequestBuilder>? configure = null)
    {
        var b = new CreateBenefitRequestBuilder(nameEn);
        configure?.Invoke(b);
        return b.Build();
    }
}

internal static class BdAssertions
{
    public static void Should200(this int? status, string because)
    {
        if (status != 200) throw new Xunit.Sdk.XunitException($"expected 200 but got {status}: {because}");
    }
}

/// <summary>Builds a <see cref="CreateBenefitRequest"/> field by field, so a test names only what it sets.</summary>
internal sealed class CreateBenefitRequestBuilder(string nameEn)
{
    public string? NameAr;
    public string? Kind, Code, Delivery, Frequency, Category, PolicyClass, GlDriverKey, EosbExclusionReason, EosbExclusionCategory;
    public bool EosbIncluded = true;
    public DateOnly? EffectiveFrom, EffectiveTo;
    public bool? IsActive, RequiresEnrolment;
    public BenefitScopeInput? Scope;
    public Guid? RequestId;

    public CreateBenefitRequest Build() => new(nameEn, NameAr, Kind, GlDriverKey, EosbIncluded, EosbExclusionReason, EosbExclusionCategory)
    {
        Code = Code, Delivery = Delivery, Frequency = Frequency, Category = Category, PolicyClass = PolicyClass, EffectiveFrom = EffectiveFrom,
        EffectiveTo = EffectiveTo, IsActive = IsActive, RequiresEnrolment = RequiresEnrolment, Scope = Scope, RequestId = RequestId,
    };
}
