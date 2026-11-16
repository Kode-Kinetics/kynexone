using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A hire that arrives through an accepted offer is a draft whose nationality is free text copied from the candidate. Activating it must stamp the
/// same derived fields the Add-Employee form stamps: the resolved nationality code and its mapping state, the Saudi / NonSaudi class, the primary
/// ID type, the contract start (the start date) and the company's default notice period. Never guessed: text that does not resolve is flagged and
/// leaves the class blank.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeDraftHireDerivationPostgresTests(PostgresFixture fixture)
{
    private static readonly string NationalId = TestIds.ValidNationalId(43_000_001);
    private static readonly string Iqama = TestIds.ValidIqama(44_000_002);
    private static readonly DateTime Start = DateTime.UtcNow.Date;

    [Fact]
    public async Task OfferPath_Saudi_ActivatesWithTheSameDerivedFieldsAsTheWizard()
    {
        var e = await ActivateAsync("Saudi Arabia", d => d.IdNumber = NationalId);
        e.NationalityCode.Should().Be("SA");
        e.NationalityMappingState.Should().Be(NationalityMappingStates.Confirmed);
        e.SaudiOrNonSaudi.Should().Be("Saudi");
        e.IdType.Should().Be("NationalId");
        e.NoticePeriodDays.Should().Be(60);
        e.ContractStartDate.Should().Be(DateOnly.FromDateTime(Start));
    }

    [Fact]
    public async Task OfferPath_ExpatWithIqama_IsNonSaudi_WithAnIqamaIdType()
    {
        var e = await ActivateAsync("Pakistani", d => d.IqamaNumber = Iqama);
        e.NationalityCode.Should().Be("PK");
        e.NationalityMappingState.Should().Be(NationalityMappingStates.Confirmed);
        e.SaudiOrNonSaudi.Should().Be("NonSaudi");
        e.IdType.Should().Be("Iqama");
        e.NoticePeriodDays.Should().Be(60);
        e.ContractStartDate.Should().Be(DateOnly.FromDateTime(Start));
    }

    [Fact]
    public async Task OfferPath_UnresolvableNationalityText_IsFlagged_AndTheClassIsNeverGuessed()
    {
        var e = await ActivateAsync("Atlantean", d => d.IqamaNumber = Iqama);
        e.NationalityCode.Should().BeNull();
        e.NationalityMappingState.Should().Be(NationalityMappingStates.Unresolved);
        e.SaudiOrNonSaudi.Should().BeEmpty();
    }

    private async Task<Employee> ActivateAsync(string nationality, Action<EmployeeDraft> shape)
    {
        var tenantId = Guid.NewGuid();
        Guid draftId;
        var checker = Guid.NewGuid();
        await using (var db = fixture.CreateDb())
        {
            var company = new Company
            {
                TenantId = tenantId, LegalNameEn = "Riyadh Co", RegistrationNumber = $"RC-{Guid.NewGuid():N}",
                CountryCode = "SA", Jurisdiction = "SA", DefaultCurrency = "SAR",
            };
            var branch = new Branch { TenantId = tenantId, CompanyId = company.Id, Code = "RUH", NameEn = "Riyadh", CountryCode = "SA", IsActive = true };
            var draft = new EmployeeDraft
            {
                TenantId = tenantId, CreatedByUserId = Guid.NewGuid(), Status = "PendingHrApproval", CurrentStep = "HrApproval",
                EnglishName = "Accepted Hire", PersonalEmail = $"hire-{Guid.NewGuid():N}@example.test",
                Branch = branch.NameEn, JoiningDate = Start, SubmittedAtUtc = DateTime.UtcNow.AddHours(-1),
                Nationality = nationality, ContractType = "Permanent",
            };
            shape(draft);
            db.AddRange(
                new Tenant { Id = tenantId, Name = "Riyadh Tenant", Slug = $"riyadh-{tenantId:N}" },
                company, branch,
                new Role { TenantId = tenantId, Name = "Employee", NormalizedName = "EMPLOYEE", IsActive = true },
                new CountryPayrollRule { TenantId = tenantId, CountryCode = "SA", RuleKey = "notice_period_days", RuleValue = "60", DataType = "int", EffectiveFrom = DateTime.UtcNow.AddYears(-1) },
                draft);
            await db.SaveChangesAsync();
            draftId = draft.Id;
        }
        await using (var db = fixture.CreateDb())
            (await EmployeeDraftLifecyclePostgresTests.Employees(db, tenantId, checker).ApproveDraft(draftId, CancellationToken.None))
                .Result.Should().BeOfType<OkObjectResult>();
        await using var read = fixture.CreateDb();
        // IgnoreQueryFilters is intentional: the test reads the one employee the activation created in its own tenant.
        return await read.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.TenantId == tenantId);
    }
}
