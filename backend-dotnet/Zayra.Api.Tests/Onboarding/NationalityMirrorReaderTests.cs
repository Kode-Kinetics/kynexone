using FluentAssertions;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.Contracts;
using Zayra.Api.Infrastructure.CountryPack.Qatar;
using Zayra.Api.Infrastructure.CountryPack.Uae;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>
/// The hire wizard now stores the catalog's English COUNTRY NAME in the legacy <c>nationality</c> text ("Pakistan"),
/// where staff used to type a demonym ("Pakistani"). Every reader that classifies a person off that text must give the
/// same answer for the name as for the person's class in the catalog, for the whole catalog, or the mirror silently
/// changes GOSI, GPSSA/GRSIA, Saudisation, wage-file and leave classification.
/// </summary>
public class NationalityMirrorReaderTests
{
    private static readonly string[] Gcc = ["AE", "BH", "KW", "OM", "QA"];

    public static IEnumerable<object[]> Entries() => NationalityCatalog.Entries.Select(e => new object[] { e.Iso2 });

    [Fact]
    public void Catalog_ClassesAreTheKnownOnes() =>
        NationalityCatalog.Entries.Where(e => e.Class == NationalityClass.Saudi).Select(e => e.Iso2).Should().Equal("SA");

    [Theory]
    [MemberData(nameof(Entries))]
    public void EveryReader_ClassifiesTheMirroredCountryName_AsTheCatalogClass(string iso2)
    {
        var e = NationalityCatalog.FindByIso2(iso2)!;
        var name = e.NameEn;
        var isSaudi = e.Class == NationalityClass.Saudi;
        var isGcc = e.Class == NationalityClass.Gcc;

        SaudiNationality.IsSaudi(name).Should().Be(isSaudi, $"IsSaudi({name})");
        GosiCalculationService.DeriveClassification(name).Should().Be(
            isSaudi ? GosiClassifications.Saudi : isGcc ? GosiClassifications.GCC : GosiClassifications.NonSaudi, $"GOSI class of {name}");
        GosiCalculationService.DeriveGccHomeState(name).Should().Be(isGcc ? iso2 : null, $"GCC home state of {name}");

        var floor = GccReadinessFloor.NormalizeNationality(name);
        if (isSaudi) floor.Should().Be("SA");
        else if (isGcc) floor.Should().Be(iso2);
        else Gcc.Concat(["SA"]).Should().NotContain(floor, $"{name} must not read as a GCC or Saudi national in the readiness floor");

        // Qiwa / Release A worker class: Saudi stays Saudi, GCC is never classified, an expat is NonSaudi or "HR confirms", never Saudi.
        var worker = WorkerNationality.ClassOf(null, name);
        if (isSaudi) worker.Should().Be(WorkerNationalityClasses.Saudi);
        else if (isGcc) worker.Should().BeNull();
        else worker.Should().NotBe(WorkerNationalityClasses.Saudi);
        if (!isSaudi && WorkerNationality.IsoOf(name) is { } iso) iso.Should().Be(iso2);

        // Leave policy nationality scope.
        var leaveIso = LeavePolicyScope.NationalityIso2(name);
        if (isSaudi || isGcc) leaveIso.Should().Be(iso2);
        else new[] { "SA", "AE", "BH", "KW", "OM", "QA" }.Should().NotContain(leaveIso);

        // Host-state social insurance: only the host's own nationals contribute, so the name must be recognised as national.
        UaeDeductionCalculator.IsUaeNational(name).Should().Be(iso2 == "AE", $"UAE national check for {name}");
        QatarDeductionCalculator.IsQatariNational(name).Should().Be(iso2 == "QA", $"Qatari national check for {name}");
    }

    [Theory]
    [InlineData("Emirati"), InlineData("AE"), InlineData("ARE")]
    public void UaeNational_KeepsEveryPreviouslyAcceptedSpelling(string spelling) => UaeDeductionCalculator.IsUaeNational(spelling).Should().BeTrue();

    [Theory]
    [InlineData("Qatari"), InlineData("QA"), InlineData("QAT")]
    public void QatariNational_KeepsEveryPreviouslyAcceptedSpelling(string spelling) => QatarDeductionCalculator.IsQatariNational(spelling).Should().BeTrue();

    [Theory]
    [InlineData("Pakistani", "Pakistan")]
    [InlineData("Indian", "India")]
    [InlineData("Emirati", "United Arab Emirates")]
    [InlineData("Saudi", "Saudi Arabia")]
    [InlineData("سعودي", "Saudi Arabia")]
    public void DuplicateDetector_ComparesNationalityAsTheSameCountry(string legacy, string mirrored)   // CD24
        => NationalityKey.Same(legacy, mirrored).Should().BeTrue();

    [Fact]
    public void DuplicateDetector_DifferentCountriesAndBlanksAreNotTheSameSignal()
    {
        NationalityKey.Same("Pakistani", "India").Should().BeFalse();
        NationalityKey.Same("", "Pakistan").Should().BeFalse();
        NationalityKey.Same(null, null).Should().BeFalse();
        NationalityKey.Same("Atlantean", "atlantean").Should().BeTrue("unrecognised text still compares case-insensitively");
    }
}
