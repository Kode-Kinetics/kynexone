using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Boot;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Every tenant is born with a leave policy per seeded leave type, and the policy carries the
/// DAY-COUNT BASIS its statute requires.
///
/// <para><b>The defect these close.</b> <c>LeaveService.CalculateWorkingDaysAsync</c> reads
/// "are weekends leave days?" and "are public holidays leave days?" from the resolved
/// <c>LeavePolicy</c>. Provisioning seeded ONE policy — annual leave — so a tenant had no sick-leave
/// policy at all and sick leave was counted by whatever the no-policy fallback happened to do. There
/// is no fallback that can be right for both types at once:</para>
/// <list type="bullet">
///   <item>Annual leave is counted in WORKING days. Charging the employee for the Friday in the
///     middle of a two-week holiday takes days the employer never owed.</item>
///   <item>KSA sick leave is counted in CALENDAR days — Art. 117 grants 120 days "during a single
///     year, whether such leaves are continuous or intermittent". Counting only working days bands a
///     120-day statutory entitlement as roughly 86 and cuts the paid bands with it.</item>
/// </list>
/// <para>So the policy row is the thing that distinguishes them, and the fix is to make sure the row
/// exists — on new tenants and on every tenant that already exists — as an ORDINARY editable row.</para>
///
/// <para>Runs against real Postgres (shared <see cref="PostgresFixture"/>) because the subject is
/// what provisioning and the boot backfill actually write, and because the backfill test walks every
/// tenant row in the database exactly as it does at start-up.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class DefaultLeavePolicyTests
{
    private readonly PostgresFixture _fx;
    public DefaultLeavePolicyTests(PostgresFixture fx) => _fx = fx;

    // A Thursday, and the Sunday three days later. On the KSA working week (Fri-Sat) the span holds
    // four calendar days of which two are rest days — so the two bases give visibly different
    // answers, which is the whole point.
    private static readonly DateOnly Thursday = new(2026, 1, 1);
    private static readonly DateOnly Sunday = new(2026, 1, 4);

    private static async Task<Tenant> NewTenantAsync(ZayraDbContext db, string prefix)
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"{prefix} Co",
            Slug = $"{prefix}-{Guid.NewGuid():N}",
            IsActive = true,
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    private static async Task<List<LeavePolicy>> PoliciesAsync(
        ZayraDbContext db, Guid tenantId, string leaveTypeCode)
    {
        var typeId = await db.LeaveTypes.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.Code == leaveTypeCode)
            .Select(t => t.Id)
            .SingleAsync();
        return await db.LeavePolicies.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.LeaveTypeId == typeId)
            .ToListAsync();
    }

    private static async Task<LeavePolicy> PolicyAsync(
        ZayraDbContext db, Guid tenantId, string leaveTypeCode, string countryCode) =>
        (await PoliciesAsync(db, tenantId, leaveTypeCode)).Single(p => p.CountryCode == countryCode);

    /// <summary>
    /// An employee of a KSA legal entity, with a sick-leave balance to spend. The company's country
    /// is passed in so a test can use the ISO-3 shape older and imported company rows carry.
    /// </summary>
    private static async Task<(Employee Employee, Guid SickTypeId)> SeedKsaEmployeeAsync(
        ZayraDbContext db, Guid tenantId, string companyCountryCode)
    {
        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Evostel Arabia LLC",
            CountryCode = companyCountryCode, Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"REG-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
        };
        db.Companies.Add(company);
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"EMP-{Guid.NewGuid():N}"[..12],
            FullName = "Aisha Rahman", UserAccountId = Guid.NewGuid(), Status = "Active",
            JoiningDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var sickTypeId = await db.LeaveTypes.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && t.Code == "SICK").Select(t => t.Id).SingleAsync();
        // Every year a test in this class can land a request in — including the previous one, for a
        // "yesterday" that falls on 31 December.
        foreach (var year in new[] { Thursday.Year, DateTime.UtcNow.Year, DateTime.UtcNow.Year - 1 }.Distinct())
        {
            if (await db.EmployeeLeaveBalances.IgnoreQueryFilters().AnyAsync(b =>
                    b.TenantId == tenantId && b.EmployeeId == employee.Id
                    && b.LeaveTypeId == sickTypeId && b.Year == year))
                continue;
            db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
            {
                TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
                LeaveTypeId = sickTypeId, Year = year, Entitled = 120,
            });
        }
        await db.SaveChangesAsync();
        return (employee, sickTypeId);
    }

    // ── 1. THE REGRESSION THIS EXISTS TO CLOSE ────────────────────────────────────────────────

    /// <summary>
    /// A newly provisioned tenant has a KSA sick-leave policy counted on the CALENDAR, and a KSA
    /// annual-leave policy counted on the WORKING WEEK — and the same Thursday-to-Sunday span
    /// therefore costs four days of sick leave and two days of annual leave.
    ///
    /// <para>Asserting the day counts rather than only the flags is deliberate: the flags are what
    /// was seeded, the counts are what the employee is charged, and only the second is the promise.</para>
    /// </summary>
    [Fact]
    public async Task NewTenant_CountsSickLeaveOnTheCalendar_AndAnnualLeaveOnTheWorkingWeek()
    {
        await using var db = _fx.CreateDb();
        var tenant = await NewTenantAsync(db, "ksa-basis");

        await TenantProvisioningBundle.ProvisionAsync(db, tenant.Id, CancellationToken.None);

        var sick = await PolicyAsync(db, tenant.Id, "SICK", "SA");
        sick.WeekendsIncluded.Should().BeTrue(
            "Art. 117 counts sick leave in calendar days — the Friday inside a sick spell is a sick day");
        sick.PublicHolidaysIncluded.Should().BeTrue();
        sick.Status.Should().Be("Active", "a policy that is not Active resolves for nobody");
        sick.AnnualEntitlementDays.Should().Be(120, "Art. 117 — 30 full pay + 60 at three quarters + 30 unpaid");
        sick.AppliesOnProbation.Should().BeTrue(
            "a seeded default must not be the reason a probationer is refused statutory sick leave");

        var annual = await PolicyAsync(db, tenant.Id, "ANNUAL", "SA");
        annual.WeekendsIncluded.Should().BeFalse("a rest day inside an annual-leave span is not a day of leave");
        annual.PublicHolidaysIncluded.Should().BeFalse();
        annual.AnnualEntitlementDays.Should().Be(21, "Art. 109(1) — 21 days, rising to 30 after five years' service");

        var service = new LeaveService(db, new ApprovalRouter(db));

        (await service.CalculateWorkingDaysAsync(tenant.Id, Thursday, Sunday, sick.Id))
            .Should().Be(4m, "Thursday to Sunday is four calendar days of sick leave under Art. 117");
        (await service.CalculateWorkingDaysAsync(tenant.Id, Thursday, Sunday, annual.Id))
            .Should().Be(2m, "Friday and Saturday are the KSA rest days, so the same span is two days of annual leave");
    }

    /// <summary>
    /// The same thing end to end, through the path a user actually takes: an employee of a KSA legal
    /// entity submits a sick-leave request and is charged four days.
    ///
    /// <para>The company's country is stored as <c>"SAU"</c> — ISO-3, the shape older and imported
    /// company rows carry. Before the country codes were normalised on comparison, the ISO-2 policy
    /// seeded for <c>"SA"</c> matched no such company, the request resolved NO policy at all, and the
    /// tenant was back in the fallback this whole change exists to remove.</para>
    /// </summary>
    [Fact]
    public async Task KsaEmployee_SubmittingSickLeave_ResolvesTheSeededCountryPolicy_AndIsChargedCalendarDays()
    {
        await using var db = _fx.CreateDb();
        var tenant = await NewTenantAsync(db, "ksa-submit");
        await TenantProvisioningBundle.ProvisionAsync(db, tenant.Id, CancellationToken.None);
        var (employee, sickTypeId) = await SeedKsaEmployeeAsync(db, tenant.Id, "SAU");

        var service = new LeaveService(db, new ApprovalRouter(db));
        var submitted = await service.SubmitRequestAsync(tenant.Id, new LeaveRequest
        {
            TenantId = tenant.Id, EmployeeId = employee.Id, LeaveTypeId = sickTypeId,
            StartDate = Thursday, EndDate = Sunday, DayType = "Full", Reason = "Influenza",
        }, employee.UserAccountId);

        var expected = await PolicyAsync(db, tenant.Id, "SICK", "SA");
        submitted.PolicyId.Should().Be(expected.Id,
            "the KSA country policy must outrank the country-neutral default for a KSA company, "
            + "including when the company's country is stored as ISO-3");
        submitted.TotalDays.Should().Be(4m, "Art. 117 counts the rest days inside a sick spell");
    }

    /// <summary>
    /// A policy that requires NO advance notice must not refuse a BACKDATED request — and one that
    /// requires notice must still refuse.
    ///
    /// <para>The notice guard read <c>NoticeRequiredDays &gt; noticeDays</c>, and for a request that
    /// started in the past <c>noticeDays</c> is negative, so <c>0 &gt; -1</c> held and the submission
    /// was refused with "This policy requires 0 day(s) advance notice." — a rule no tenant ever
    /// configured. It was invisible only while tenants had no policy for most leave types; seeding a
    /// sick-leave policy makes it fire on the one kind of leave that is reported after the fact.</para>
    /// </summary>
    [Fact]
    public async Task ABackdatedSickRequest_IsAccepted_WhenThePolicyRequiresNoNotice_AndRefusedWhenItDoes()
    {
        await using var db = _fx.CreateDb();
        var tenant = await NewTenantAsync(db, "notice");
        await TenantProvisioningBundle.ProvisionAsync(db, tenant.Id, CancellationToken.None);
        var (employee, sickTypeId) = await SeedKsaEmployeeAsync(db, tenant.Id, "SAU");

        var service = new LeaveService(db, new ApprovalRouter(db));
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        var submitted = await service.SubmitRequestAsync(tenant.Id, new LeaveRequest
        {
            TenantId = tenant.Id, EmployeeId = employee.Id, LeaveTypeId = sickTypeId,
            StartDate = yesterday, EndDate = yesterday, DayType = "Full", Reason = "Fever",
        }, employee.UserAccountId);
        submitted.Status.Should().NotBe("Draft");

        // The configured case is untouched: a policy that asks for notice still enforces it.
        var policyId = (await PolicyAsync(db, tenant.Id, "SICK", "SA")).Id;
        var policy = await db.LeavePolicies.IgnoreQueryFilters().SingleAsync(p => p.Id == policyId);
        policy.NoticeRequiredDays = 7;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var refused = async () => await new LeaveService(db, new ApprovalRouter(db))
            .SubmitRequestAsync(tenant.Id, new LeaveRequest
            {
                TenantId = tenant.Id, EmployeeId = employee.Id, LeaveTypeId = sickTypeId,
                StartDate = yesterday.AddDays(-10), EndDate = yesterday.AddDays(-10),
                DayType = "Full", Reason = "Fever",
            }, employee.UserAccountId);
        await refused.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*7 day(s) advance notice*");
    }

    // ── 2. Idempotency ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Provisioning runs again whenever a tenant is re-provisioned, and the backfill runs on every
    /// boot. Duplicated policies would not merely be untidy: two equally specific active policies for
    /// one leave type make the resolved policy — and so the employee's day count and accrual —
    /// depend on which row was touched last.
    /// </summary>
    [Fact]
    public async Task ProvisioningTwice_AddsNoDuplicateLeavePolicies()
    {
        await using var db = _fx.CreateDb();
        var tenant = await NewTenantAsync(db, "idem-leave");

        var first = await TenantProvisioningBundle.ProvisionAsync(db, tenant.Id, CancellationToken.None);
        first.LeavePolicies.Should().Be(
            (TenantProvisioningBundle.DefaultLeavePolicyCountries.Length + 1) * 2,
            "one country-neutral row plus one per GCC state, for each of the two seeded leave types");

        var second = await TenantProvisioningBundle.ProvisionAsync(db, tenant.Id, CancellationToken.None);
        second.LeavePolicies.Should().Be(0);
        second.LeaveTypes.Should().Be(0);

        foreach (var code in new[] { "ANNUAL", "SICK" })
        {
            var policies = await PoliciesAsync(db, tenant.Id, code);
            policies.Should().HaveCount(first.LeavePolicies / 2);
            policies.Select(p => p.CountryCode).Should().OnlyHaveUniqueItems(
                "the natural key is (leave type, tenant-wide scope, country)");
        }
    }

    // ── 3. The backfill: existing tenants get them too ────────────────────────────────────────

    /// <summary>
    /// The demo and pilot tenants already exist and were never provisioned through the bundle, so a
    /// fix that only runs for new tenants fixes nobody's data. The boot backfill installs the same
    /// rows and then does nothing for ever after.
    /// </summary>
    [Fact]
    public async Task Backfill_InstallsLeavePolicies_OnATenantThatHasNone_AndIsANoOpOnce()
    {
        await using var db = _fx.CreateDb();
        var tenant = await NewTenantAsync(db, "backfill-leave");

        (await db.LeavePolicies.IgnoreQueryFilters().CountAsync(p => p.TenantId == tenant.Id))
            .Should().Be(0, "this is the shape every pre-existing tenant is in");

        var first = await TenantDefaultsBackfill.RunAsync(db, NullLogger.Instance, CancellationToken.None);
        first.TenantsFailed.Should().Be(0);
        first.LeavePoliciesAdded.Should().BeGreaterThan(0);

        var sick = await PolicyAsync(db, tenant.Id, "SICK", "SA");
        sick.WeekendsIncluded.Should().BeTrue();
        sick.AnnualEntitlementDays.Should().Be(120);

        var installed = await db.LeavePolicies.IgnoreQueryFilters().CountAsync(p => p.TenantId == tenant.Id);
        installed.Should().Be((TenantProvisioningBundle.DefaultLeavePolicyCountries.Length + 1) * 2);

        // Boot again, and again.
        var second = await TenantDefaultsBackfill.RunAsync(db, NullLogger.Instance, CancellationToken.None);
        var third = await TenantDefaultsBackfill.RunAsync(db, NullLogger.Instance, CancellationToken.None);
        second.LeavePoliciesAdded.Should().Be(0, "nothing was left to install");
        second.LeaveTypesAdded.Should().Be(0);
        third.LeavePoliciesAdded.Should().Be(0);
        (await db.LeavePolicies.IgnoreQueryFilters().CountAsync(p => p.TenantId == tenant.Id))
            .Should().Be(installed);
    }

    // ── 4. THE ONE THAT PROTECTS A LIVE CLIENT ────────────────────────────────────────────────

    /// <summary>
    /// These are the client's rows, not the platform's. An employer that grants 25 days of annual
    /// leave, renames the policy, or archives one it does not want must find its decision intact
    /// after the next deploy — the failure mode being avoided is not "the defaults are missing" but
    /// "our leave policy reverted on Tuesday".
    /// </summary>
    [Fact]
    public async Task AnAdminEditToASeededPolicy_SurvivesReProvisioningAndTheBackfill()
    {
        await using var db = _fx.CreateDb();
        var tenant = await NewTenantAsync(db, "noclobber-leave");
        await TenantProvisioningBundle.ProvisionAsync(db, tenant.Id, CancellationToken.None);

        var annualId = (await PolicyAsync(db, tenant.Id, "ANNUAL", "SA")).Id;
        var annual = await db.LeavePolicies.IgnoreQueryFilters().SingleAsync(p => p.Id == annualId);
        annual.AnnualEntitlementDays = 25;
        annual.Name = "Annual Leave (Evostel — 25 days)";
        annual.NoticeRequiredDays = 14;

        // And one the administrator removed outright. DELETE archives rather than erasing, which is
        // what lets the removal stand: the row is still there for the gap check to find.
        var unwantedId = (await PolicyAsync(db, tenant.Id, "SICK", "OM")).Id;
        var unwanted = await db.LeavePolicies.IgnoreQueryFilters().SingleAsync(p => p.Id == unwantedId);
        unwanted.Status = "Archived";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reprovision = await TenantProvisioningBundle.ProvisionAsync(db, tenant.Id, CancellationToken.None);
        var backfill = await TenantDefaultsBackfill.RunAsync(db, NullLogger.Instance, CancellationToken.None);
        reprovision.LeavePolicies.Should().Be(0);
        backfill.LeavePoliciesAdded.Should().Be(0);

        db.ChangeTracker.Clear();
        var reread = await PolicyAsync(db, tenant.Id, "ANNUAL", "SA");
        reread.AnnualEntitlementDays.Should().Be(25, "a deploy must never restore the stock entitlement");
        reread.Name.Should().Be("Annual Leave (Evostel — 25 days)");
        reread.NoticeRequiredDays.Should().Be(14);

        (await PolicyAsync(db, tenant.Id, "SICK", "OM")).Status
            .Should().Be("Archived", "a removal is a decision, not a gap to be refilled");
        (await PoliciesAsync(db, tenant.Id, "ANNUAL")).Select(p => p.CountryCode).Should()
            .OnlyHaveUniqueItems("no duplicate was planted beside the edited row");
    }
}

/// <summary>
/// The legal numbers the seeded defaults carry come from ONE place each. These are pure guards
/// against a second source of truth appearing for a figure that has legal consequences — the way
/// two copies of a statutory number drift is that one of them is corrected and the other is not.
/// </summary>
public class DefaultLeaveEntitlementSourceTests
{
    /// <summary>
    /// KSA's seeded entitlements must equal the country pack's own Art. 109 / Art. 117 constants.
    /// The seeder reads <c>TenantProvisioningBundle.Packs</c> (which is also what the tenant's
    /// <c>CountryPayrollRule</c> leave rules are written from); <c>KsaLeaveHoursDefaults</c> is what
    /// the accrual tier and the sick-pay banding read. If those two ever disagree, a tenant is
    /// granted one number and paid against another.
    /// </summary>
    [Fact]
    public void SeededKsaEntitlements_MatchTheKsaCountryPackConstants()
    {
        // Art. 109(1) — 21 days, rising to 30 after five consecutive years. The row carries the
        // base; KsaAnnualLeaveScale applies the tier as a floor on top of it.
        TenantProvisioningBundle.DefaultLeaveEntitlementDays("ANNUAL", "SA")
            .Should().Be(KsaLeaveHoursDefaults.AnnualLeaveBaseDays);

        // Art. 117 — 30 days at full pay + 60 at three quarters + 30 unpaid = 120 in a single year.
        TenantProvisioningBundle.DefaultLeaveEntitlementDays("SICK", "SA")
            .Should().Be(KsaLeaveHoursDefaults.SickBand1Days
                       + KsaLeaveHoursDefaults.SickBand2Days
                       + KsaLeaveHoursDefaults.SickBand3Days);
    }

    /// <summary>
    /// UAE Federal Decree-Law 33/2021: Art. 29 — 30 days annual leave for a worker who has completed
    /// one year; Art. 31 — 90 days sick leave in the year, 15 at full pay, 30 at half, 45 unpaid.
    /// </summary>
    [Fact]
    public void SeededUaeEntitlements_AreTheUaeStatutoryFigures()
    {
        TenantProvisioningBundle.DefaultLeaveEntitlementDays("ANNUAL", "AE").Should().Be(30m);
        TenantProvisioningBundle.DefaultLeaveEntitlementDays("SICK", "AE").Should().Be(90m);
    }

    /// <summary>
    /// The country-neutral default — the one an employee whose company has no country code resolves
    /// — carries the home jurisdiction's figures rather than nothing at all.
    /// </summary>
    [Fact]
    public void TheCountryNeutralDefault_CarriesTheHomeJurisdictionFigures()
    {
        TenantProvisioningBundle.DefaultLeaveEntitlementDays("SICK", string.Empty)
            .Should().Be(TenantProvisioningBundle.DefaultLeaveEntitlementDays("SICK", "SA"));
        TenantProvisioningBundle.DefaultLeaveEntitlementDays("ANNUAL", string.Empty)
            .Should().Be(TenantProvisioningBundle.DefaultLeaveEntitlementDays("ANNUAL", "SA"));
    }

    /// <summary>
    /// A policy's country restriction is compared after normalising both sides to canonical ISO-2.
    /// <c>Company.CountryCode</c> carries ISO-3 on older and imported rows, and a raw string
    /// comparison answered "not eligible" for every one of them.
    /// </summary>
    [Theory]
    [InlineData("SA", "SA", true)]
    [InlineData("SA", "SAU", true)]
    [InlineData("SA", "sau", true)]
    [InlineData("SA", "AE", false)]
    [InlineData("SA", "", false)]
    [InlineData("", "AE", true)]          // an unset restriction admits everyone
    [InlineData("", "", true)]
    [InlineData("ZZ", "ZZ", true)]        // unrecognised: falls back to a raw comparison
    public void CountryMatches_NormalisesBothSidesToIso2(string policyCountry, string employeeCountry, bool expected)
        => LeaveService.CountryMatches(policyCountry, employeeCountry).Should().Be(expected);
}
