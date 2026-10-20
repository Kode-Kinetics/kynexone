using FluentAssertions;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public partial class OfferComponentOptionsTests
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _company = Guid.NewGuid();
    private readonly DateOnly _start = new(2027, 1, 15);
    private PayComponent Definition(string code, Guid? company = null) => new()
    {
        TenantId = _tenant, CompanyId = company, Code = code, NameEn = code,
        ComponentType = "Earning", CalcMethod = "Fixed", IsActive = true, Value = 999
    };

    [Fact]
    public void ApplicableCompanyDefinitionWinsAndFutureVersionDoesNotLeak()
    {
        var tenant = Definition("HOUSING");
        var company = Definition("HOUSING", _company); company.EffectiveFrom = new(2027, 1, 1);
        var future = Definition("HOUSING", _company); future.EffectiveFrom = new(2027, 2, 1);
        var foreign = Definition("OTHER", Guid.NewGuid());
        OfferComponentOptions.ResolveDefinitions([tenant, company, future, foreign], _tenant, _company, _start)
            .Should().ContainSingle().Which.Id.Should().Be(company.Id);
    }

    [Fact]
    public void EffectiveBoundsAreInclusiveAndNewestApplicableVersionWins()
    {
        var old = Definition("HOUSING"); old.EffectiveTo = _start;
        var current = Definition("HOUSING"); current.EffectiveFrom = _start;
        OfferComponentOptions.ResolveDefinitions([old, current], _tenant, _company, _start).Should().ContainSingle().Which.Id.Should().Be(current.Id);
        OfferComponentOptions.ResolveDefinitions([old], _tenant, _company, _start.AddDays(1)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Formula", "Earning", null, false)]
    [InlineData("PercentOfBasic", "Earning", null, false)]
    [InlineData("Integration", "Earning", "Bonus", false)]
    [InlineData("Fixed", "Earning", "Tax", false)]
    [InlineData("Statutory", "Earning", null, true)]
    [InlineData("Fixed", "Deduction", null, false)]
    [InlineData("Fixed", "EmployerContribution", null, false)]
    public void ProviderFormulaAndNonEarningDefinitionsAreUnavailable(string method, string type, string? provider, bool statutory)
    {
        var definition = Definition("EXTRA"); definition.CalcMethod = method; definition.ComponentType = type;
        definition.ProviderKey = provider; definition.IsStatutory = statutory;
        OfferComponentOptions.ResolveDefinitions([definition], _tenant, _company, _start).Should().BeEmpty();
    }

    [Fact]
    public void InactiveCompanyOverrideDoesNotReviveTenantDefault()
    {
        var tenant = Definition("EXTRA"); var company = Definition("EXTRA", _company); company.IsActive = false;
        OfferComponentOptions.ResolveDefinitions([tenant, company], _tenant, _company, _start).Should().BeEmpty();
    }

    [Fact]
    public void EmptyCatalogueDoesNotSynthesizePayrollSeeds() =>
        OfferComponentOptions.ResolveDefinitions([], _tenant, _company, _start).Should().BeEmpty();

    [Fact]
    public void CompanyBasicMappingOverridesTenantBasicEvenWhenItsDisplayCodeDiffers()
    {
        var tenant = Definition("BASIC"); tenant.CalcMethod = "StructureField"; tenant.StructureField = "BasicSalary";
        var company = Definition("BASE_PAY", _company); company.CalcMethod = "StructureField"; company.StructureField = "BasicSalary";
        OfferComponentOptions.ResolveDefinitions([tenant, company], _tenant, _company, _start)
            .Should().ContainSingle().Which.Id.Should().Be(company.Id);
    }
}
