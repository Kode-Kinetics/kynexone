using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Entitlements.Usage;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Benefit utilisation on EF InMemory (the race proof is in <see cref="BenefitUsagePostgresTests"/>). Mohammed (PackageSeed) works at
/// Masar Facility Services; his frozen rows are written here directly so each test decides what was frozen. Users: an HR user, a
/// Finance approver, a second Finance approver, and Mohammed's own login.
/// </summary>
public sealed class BenefitUsageKit : IAsyncDisposable
{
    public static readonly DateOnly Today = new(2026, 10, 6);

    public ZayraDbContext Db { get; }
    public PackageSeed Seed { get; }
    public FixedTenantClock Clock { get; } = new(Today);
    public BenefitUsageService Service { get; }

    public Guid HrUser { get; } = Guid.NewGuid();
    public Guid FinanceUser { get; } = Guid.NewGuid();
    public Guid SecondFinanceUser { get; } = Guid.NewGuid();

    private BenefitUsageKit(ZayraDbContext db, PackageSeed seed)
    {
        Db = db;
        Seed = seed;
        Service = new BenefitUsageService(db, Clock);
    }

    public static async Task<BenefitUsageKit> CreateAsync(Action<PackageSeed>? tweak = null)
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"benefit-usage-{Guid.NewGuid():N}").Options);
        var seed = new PackageSeed();
        tweak?.Invoke(seed);
        await seed.SaveAsync(db);
        return new BenefitUsageKit(db, seed);
    }

    public BenefitUsageActor Hr(Guid? user = null, Guid? onlyCompany = null) =>
        new(Seed.TenantId, user ?? HrUser, ["entitlements.read", "entitlements.manage"], c => onlyCompany is null || c == onlyCompany);

    public BenefitUsageActor Finance(Guid? user = null, Guid? onlyCompany = null) =>
        new(Seed.TenantId, user ?? FinanceUser, ["payroll.approve"], c => onlyCompany is null || c == onlyCompany);

    /// <summary>Mohammed himself, holding every key: the subject never decides their own request, whatever their role.</summary>
    public BenefitUsageActor Subject() =>
        new(Seed.TenantId, Seed.UserId, ["entitlements.read", "entitlements.manage", "payroll.approve"], _ => true);

    /// <summary>A frozen row for Mohammed's term, written as the writer would (Verified unless told otherwise).</summary>
    public async Task<EmployeeEntitlement> FreezeAsync(string code, string valueType, short? quantity = null, decimal? amount = null,
        string? tier = null, string scope = DependantScopes.None, short? maxDependants = null, string period = EntitlementLimitPeriods.Annual,
        string verification = EntitlementVerificationStates.Verified)
    {
        var row = new EmployeeEntitlement
        {
            TenantId = Seed.TenantId, CompanyId = Seed.Company.Id, EmployeeId = Seed.Mohammed.PublicId, ContractId = Seed.Term.Id,
            PayComponentCode = code, EntitlementClass = PayEntitlementClasses.Contractual, ValueType = valueType, Quantity = quantity, Amount = amount,
            CoverageTier = tier, DependantScope = scope, MaxDependants = maxDependants, LimitPeriod = period,
            Source = verification == EntitlementVerificationStates.Verified ? EntitlementSources.GradeDefault : EntitlementSources.Migrated,
            VerificationState = verification, GradeEntitlementId = Seed.Cells.TryGetValue(code, out var cell) ? cell.Id : null,
            EffectiveFrom = Seed.Term.StartDate, EffectiveTo = Seed.Term.EndDate,
        };
        Db.EmployeeEntitlements.Add(row);
        await Db.SaveChangesAsync();
        return row;
    }

    public BenefitUsageRequestInput Ticket(DateOnly? on = null, short units = 1, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), Seed.Mohammed.Id, "AIR_TICKET", on ?? new DateOnly(2026, 8, 10), units, null, "BKG-1001");

    public BenefitUsageRequestInput Money(string code, decimal amount, DateOnly? on = null, Guid? dependantId = null) =>
        new(Guid.NewGuid(), Seed.Mohammed.Id, code, on ?? new DateOnly(2026, 9, 1), null, amount, "INV-77", DependantId: dependantId, Currency: "SAR");

    public Task<BenefitUsageRequestDto> ApproveAsync(Guid id, Guid? by = null) => Service.DecideAsync(Finance(by), id, "Approved", null, default);

    public async Task<BenefitUtilisationDto> RowAsync(string code, DateOnly? asOf = null) =>
        (await Service.UtilisationAsync(Hr(), Seed.Mohammed.Id, asOf, default)).Single(r => r.BenefitCode == code);

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
