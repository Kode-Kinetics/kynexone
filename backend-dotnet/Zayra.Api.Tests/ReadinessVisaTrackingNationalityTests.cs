using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// DEFECT (hire rehearsal): with visa tracking switched on for a Saudi company, the Overview checklist listed "Visa expiry" as
/// recommended for a Saudi NATIONAL, who has no visa. The visa-tracking toggle added the requirement with no nationality condition,
/// while the code floor already limits the residence visa to non-GCC expatriates.
/// </summary>
public class ReadinessVisaTrackingNationalityTests
{
    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<ResolvedReadinessPolicy> ResolveAsync(string nationality)
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        db.GCCComplianceSettings.Add(new GCCComplianceSetting
        {
            TenantId = tenantId, CompanyId = null, CountryCode = "SA", IqamaRequired = true, WpsEnabled = true, VisaTrackingEnabled = true,
        });
        await db.SaveChangesAsync();
        return await new EmployeeReadinessPolicyResolver(db).ResolveAsync(tenantId, companyId: null, "SA", nationality);
    }

    [Theory]
    [InlineData("Saudi")]
    [InlineData("SA")]
    [InlineData("Saudi Arabia")]
    public async Task SaudiNational_IsNotAskedForVisaExpiry(string nationality)
    {
        var policy = await ResolveAsync(nationality);

        policy.Items.Should().NotContain(i => i.Key == "VisaExpiryDate", "a Saudi national holds no visa");
    }

    [Fact]
    public async Task GccNational_IsNotAskedForVisaExpiry()
    {
        var policy = await ResolveAsync("Emirati");

        policy.Items.Should().NotContain(i => i.Key == "VisaExpiryDate", "GCC nationals are exempt from the host residence visa");
    }

    [Fact]
    public async Task NonGccExpat_IsStillAskedForVisaExpiry_WhenVisaTrackingIsOn()
    {
        var policy = await ResolveAsync("Indian");

        policy.Items.Should().Contain(i => i.Key == "VisaExpiryDate", "the narrowing must not drop the requirement for the people who do hold a visa");
    }
}
