using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// L3.2 — a NEW KSA tenant is born with the full KSA leave-type catalogue (13 types, each with an editable default policy),
/// not just ANNUAL and SICK. Existing tenants, other countries and the boot backfill are never touched.
/// </summary>
public sealed class LeaveKsaCatalogueSeedTests
{
    private static readonly string[] Codes =
    [
        "ANNUAL", "SICK", "UNPAID", "MATERNITY", "MATERNITY_EXT", "PATERNITY", "MARRIAGE", "BEREAVEMENT",
        "BEREAVEMENT_SIBLING", "HAJJ", "EXAM", "IDDAH", "IDDAH_NON_MUSLIM",
    ];

    private static ZayraDbContext NewDb()
        => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(List<LeaveType> Types, List<LeavePolicy> Policies)> InstallAsync(string home, bool newTenant, Guid? companyOfCountry = null)
    {
        var db = NewDb();
        var tenantId = Guid.NewGuid();
        await TenantProvisioningBundle.InstallDefaultLeaveAsync(db, tenantId, default, home, newTenantKsaDefaults: newTenant);
        await db.SaveChangesAsync();
        return (await db.LeaveTypes.IgnoreQueryFilters().ToListAsync(), await db.LeavePolicies.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task ANewSaudiTenant_GetsAllThirteenTypes_EachWithADefaultPolicy()
    {
        var (types, policies) = await InstallAsync("SA", newTenant: true);

        types.Select(t => t.Code).Should().BeEquivalentTo(Codes);
        foreach (var type in types)
            policies.Should().Contain(p => p.LeaveTypeId == type.Id && p.Status == "Active", $"{type.Code} needs a default policy");
        types.Select(t => t.NameAr).Should().OnlyContain(n => !string.IsNullOrWhiteSpace(n));
    }

    [Fact]
    public async Task EveryStatutoryTypeIsRecognisedByTheEngine_AndItsPolicyPassesTheStatutoryGuard()
    {
        var (types, policies) = await InstallAsync("SA", newTenant: true);
        var expected = new Dictionary<string, KsaStatutoryLeaveKind?>
        {
            ["ANNUAL"] = null, ["SICK"] = null, ["UNPAID"] = null, ["MATERNITY_EXT"] = null, ["EXAM"] = null,
            ["MATERNITY"] = KsaStatutoryLeaveKind.Maternity, ["PATERNITY"] = KsaStatutoryLeaveKind.Paternity,
            ["MARRIAGE"] = KsaStatutoryLeaveKind.Marriage, ["BEREAVEMENT"] = KsaStatutoryLeaveKind.Bereavement,
            ["BEREAVEMENT_SIBLING"] = KsaStatutoryLeaveKind.BereavementSibling, ["HAJJ"] = KsaStatutoryLeaveKind.Hajj,
            ["IDDAH"] = KsaStatutoryLeaveKind.IddahMuslim, ["IDDAH_NON_MUSLIM"] = KsaStatutoryLeaveKind.IddahNonMuslim,
        };
        foreach (var type in types)
        {
            var kind = KsaStatutorySpecialLeave.Classify(type.Code, type.NameEn, type.Category);
            kind.Should().Be(expected[type.Code], type.Code);
            if (kind is not { } k) continue;
            foreach (var policy in policies.Where(p => p.LeaveTypeId == type.Id))
            {
                var floor = KsaSpecialLeaveDefaults.FloorDays(k, new DateOnly(2026, 1, 1))!.Value;
                KsaStatutorySpecialLeave.PolicyViolations(k, floor, policy.AnnualEntitlementDays, policy.MaximumDaysPerRequest,
                        policy.PayrollImpact, policy.WeekendsIncluded && policy.PublicHolidaysIncluded)
                    .Should().BeEmpty($"{type.Code} must be lawful as seeded");
            }
        }
    }

    [Fact]
    public async Task AttachmentAndMaxRules_FollowTheSpec()
    {
        var (types, _) = await InstallAsync("SA", newTenant: true);
        LeaveType T(string code) => types.Single(t => t.Code == code);

        T("SICK").RequiresAttachment.Should().BeTrue("sick leave is proven by a certificate from day one");
        T("UNPAID").RequiresAttachment.Should().BeFalse();
        T("UNPAID").IsPaid.Should().BeFalse();
        T("MATERNITY_EXT").IsPaid.Should().BeFalse();
        T("ANNUAL").MaxConsecutiveDays.Should().Be(0);
        T("SICK").MaxConsecutiveDays.Should().Be(0);
        (T("MATERNITY").MaxConsecutiveDays, T("PATERNITY").MaxConsecutiveDays, T("MARRIAGE").MaxConsecutiveDays,
                T("BEREAVEMENT").MaxConsecutiveDays, T("BEREAVEMENT_SIBLING").MaxConsecutiveDays, T("HAJJ").MaxConsecutiveDays,
                T("IDDAH").MaxConsecutiveDays, T("IDDAH_NON_MUSLIM").MaxConsecutiveDays)
            .Should().Be((84, 3, 5, 5, 3, 15, 130, 15));
        T("MATERNITY_EXT").MaxConsecutiveDays.Should().Be(30);
    }

    [Fact]
    public async Task NoReligionIsCollected_AndGenderGatesTheFemaleAndMaleLeaves()
    {
        var (types, policies) = await InstallAsync("SA", newTenant: true);
        string GenderOf(string code) => policies.First(p => p.LeaveTypeId == types.Single(t => t.Code == code).Id).Gender;

        GenderOf("MATERNITY").Should().Be("Female");
        GenderOf("IDDAH").Should().Be("Female");
        GenderOf("IDDAH_NON_MUSLIM").Should().Be("Female");
        GenderOf("PATERNITY").Should().Be("Male");
        GenderOf("MARRIAGE").Should().BeEmpty();
    }

    [Fact]
    public async Task ReRunningTheInstaller_AddsNothing()
    {
        var db = NewDb();
        var tenantId = Guid.NewGuid();
        await TenantProvisioningBundle.InstallDefaultLeaveAsync(db, tenantId, default, "SA", newTenantKsaDefaults: true);
        await db.SaveChangesAsync();

        var second = await TenantProvisioningBundle.InstallDefaultLeaveAsync(db, tenantId, default, "SA", newTenantKsaDefaults: true);

        second.Should().Be((0, 0));
    }

    [Theory]
    [InlineData("SA", false)]
    [InlineData("AE", true)]
    public async Task OtherCountries_AndTheBootBackfill_KeepOnlyAnnualAndSick(string home, bool newTenant)
    {
        var (types, _) = await InstallAsync(home, newTenant);

        types.Select(t => t.Code).Should().BeEquivalentTo("ANNUAL", "SICK");
        types.Single(t => t.Code == "SICK").RequiresAttachment.Should().BeFalse("an existing tenant's sick type is never rewritten");
    }

    [Fact]
    public async Task ANewSaudiTenants_AnnualPolicy_ProratesAJoinersFirstMonth_ButExistingDefaultsDoNot()
    {
        var (_, saudi) = await InstallAsync("SA", newTenant: true);
        var (_, backfill) = await InstallAsync("SA", newTenant: false);
        var (_, emirati) = await InstallAsync("AE", newTenant: true);

        saudi.Where(p => p.AccrualMethod == "Monthly" && p.CountryCode == "SA").Should().OnlyContain(p => p.ProratePartialMonths);
        backfill.Should().OnlyContain(p => !p.ProratePartialMonths);
        emirati.Should().OnlyContain(p => !p.ProratePartialMonths);
    }

    [Fact]
    public async Task ASaudiCompany_GetsItsOwnCatalogue_NotAShared()
    {
        var db = NewDb();
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "KSA Co", CountryCode = "SA" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        await TenantProvisioningBundle.InstallDefaultLeaveAsync(db, tenantId, default, "SA", newTenantKsaDefaults: true);
        await db.SaveChangesAsync();

        var types = await db.LeaveTypes.IgnoreQueryFilters().ToListAsync();
        types.Select(t => t.Code).Should().BeEquivalentTo(Codes);
        types.Should().OnlyContain(t => t.CompanyId == company.Id);
    }
}
