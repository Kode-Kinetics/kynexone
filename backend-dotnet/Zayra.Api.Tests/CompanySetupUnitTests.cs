using System.Reflection;
using FluentAssertions;
using Zayra.Api.Application.Setup;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.CompanySetup;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Infrastructure.WorkWeek;
using Zayra.Api.Infrastructure.Setup;

namespace Zayra.Api.Tests;

/// <summary>Database-free rules of the create-company wizard (docs/design/company-setup-wizard.md).</summary>
public class CompanySetupUnitTests
{
    // ── Decision 1: no model call is reachable from any company-setup endpoint ──────────────────────────

    /// <summary>
    /// The controller is constructed from exactly these types; each concrete type's constructor graph is walked
    /// (an interface is followed to the implementation production registers). None may depend on the model client,
    /// the setup assistant (whose instance path calls a model) or anything in an AI namespace. A reviewer who adds
    /// such a dependency to any of them makes this fail.
    /// </summary>
    [Fact]
    public void No_model_client_is_reachable_from_the_company_setup_controller()
    {
        var implementations = new Dictionary<Type, Type>
        {
            [typeof(Zayra.Api.Application.Organization.IOrganizationSetupService)] = typeof(OrganizationSetupService),
            [typeof(Zayra.Api.Application.Auth.IAccessManagementService)] = typeof(AccessManagementService),
            [typeof(Zayra.Api.Application.WorkWeek.IWorkWeekService)] = typeof(WorkWeekService),
            [typeof(IEmailService)] = typeof(SmtpEmailService),
            [typeof(Zayra.Api.Application.Auth.IAuditService)] = typeof(AuditService),
        };
        var seen = new HashSet<Type>();
        var offenders = new List<string>();
        void Walk(Type type, string path)
        {
            if (!seen.Add(type)) return;
            if (IsModelType(type)) { offenders.Add($"{path} -> {type.FullName}"); return; }
            if (implementations.TryGetValue(type, out var impl)) { Walk(impl, $"{path} -> {type.Name}"); return; }
            if (type.IsInterface || type.IsAbstract || type.Namespace?.StartsWith("Zayra.Api", StringComparison.Ordinal) != true) return;
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                foreach (var p in ctor.GetParameters())
                    Walk(p.ParameterType, $"{path} -> {type.Name}({p.Name})");
            foreach (var f in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                Walk(f.FieldType, $"{path} -> {type.Name}.{f.Name}");
        }
        Walk(typeof(CompanySetupController), "CompanySetupController");
        Walk(typeof(CompanySetupService), "CompanySetupService");
        Walk(typeof(CompanySetupPlanner), "CompanySetupPlanner");
        offenders.Should().BeEmpty("the create-company wizard is deterministic only (owner rule: policy intake off, manual setup)");
    }

    private static bool IsModelType(Type type) =>
        type == typeof(Zayra.Api.Application.AI.ILlmClient)
        || type == typeof(ISetupAssistantService)
        || type == typeof(SetupAssistantService)
        || (type.Namespace ?? string.Empty).StartsWith("Zayra.Api.Infrastructure.AI", StringComparison.Ordinal)
        || (type.Namespace ?? string.Empty).StartsWith("Zayra.Api.Application.AI", StringComparison.Ordinal);

    /// <summary>The source of the wizard names no model API either (a static call would not show in a ctor graph).</summary>
    [Fact]
    public void The_company_setup_sources_never_name_a_model_api()
    {
        var root = FindRepoRoot();
        var files = Directory.GetFiles(Path.Combine(root, "backend-dotnet", "Zayra.Api", "Infrastructure", "CompanySetup"), "*.cs")
            .Append(Path.Combine(root, "backend-dotnet", "Zayra.Api", "Controllers", "CompanySetupController.cs"))
            .Append(Path.Combine(root, "backend-dotnet", "Zayra.Api", "Application", "CompanySetup", "CompanySetupContracts.cs"));
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in new[] { "ILlmClient", "IAiCallRecorder", "GenerateAsync(", "TryLlmAsync", "Infrastructure.AI", "Application.AI", "AiGateway" })
                // Doc comments may name what is excluded; code may not use it.
                text.Split('\n').Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal) && !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    .Should().NotContain(l => l.Contains(forbidden), $"{Path.GetFileName(file)} must not reach a model ({forbidden})");
        }
    }

    [Fact]
    public void The_deterministic_defaults_are_the_same_every_time()
    {
        var a = SetupAssistantService.DeterministicCompanyDefaults("SA", "SAR", "Acme");
        var b = SetupAssistantService.DeterministicCompanyDefaults("SA", "SAR", "Acme");
        System.Text.Json.JsonSerializer.Serialize(a).Should().Be(System.Text.Json.JsonSerializer.Serialize(b));
        a.HolidayCalendar.Should().NotBeNull();
        a.Grades.Should().NotBeEmpty().And.OnlyContain(g => g.Currency == "SAR");
    }

    // ── Decision 10: one MOL establishment number ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null, null, true, null, false)]                  // nothing recorded
    [InlineData(null, "7-1", "7-1", true, "7-1", false)]               // today's rule: bank settings, agreeing
    [InlineData(null, "7-1", "7-2", true, null, true)]                 // today's rule: bank vs agent conflict
    [InlineData(null, null, "7-2", false, "7-2", false)]               // non-KSA: the GCC agent id
    [InlineData("7-9", null, null, true, "7-9", false)]                // the company value alone
    [InlineData("7-9", "7-9", "7-9", true, "7-9", false)]              // all agree
    [InlineData("7-9", "7-1", null, true, null, true)]                 // company vs bank settings
    [InlineData("7-9", null, "7-2", true, null, true)]                 // company vs GCC agent
    [InlineData("7-9", null, "7-2", false, null, true)]                // company vs GCC agent outside KSA too
    [InlineData("7-9", "7-1", null, false, "7-9", false)]              // bank settings are KSA-only: not read elsewhere
    public void The_company_mol_number_is_the_authority(string? company, string? bank, string? agent, bool ksa, string? expected, bool conflict)
    {
        var (id, issue) = WpsEstablishmentAuthority.Resolve(company, bank, agent, ksa);
        id.Should().Be(expected);
        (issue is not null).Should().Be(conflict);
        if (conflict) issue!.Code.Should().Be(WpsEstablishmentAuthority.ConflictCode);
    }

    [Theory]
    [InlineData("7-1234567", "71234567", null, true, "7-1234567")]     // hyphen only differs: the company's spelling is filed
    [InlineData("71234567", "7-1234567", null, true, "71234567")]
    [InlineData("7-1234567", null, "7 1234567", true, "7-1234567")]    // a space is not a different establishment
    [InlineData("7-1234567", "7 - 1234567", "71234567", true, "7-1234567")]
    [InlineData("7-1234567", null, "71234567", false, "7-1234567")]    // outside KSA too
    [InlineData(null, "7-1234567", "71234567", true, "7-1234567")]     // no company value: today's rule, the bank value is filed
    public void Establishment_ids_that_differ_only_by_spaces_or_hyphens_agree(string? company, string? bank, string? agent, bool ksa, string expected)
    {
        var (id, issue) = WpsEstablishmentAuthority.Resolve(company, bank, agent, ksa);
        issue.Should().BeNull();
        id.Should().Be(expected, "the file carries the recorded value unchanged, not the comparison key");
    }

    [Theory]
    [InlineData("7-1234567", "7-1234568", null)]   // a different digit
    [InlineData("7-1234567", "07-1234567", null)]  // a leading zero is a character, not formatting
    [InlineData("7-1234567", "7_1234567", null)]   // only spaces and hyphens are ignored
    [InlineData("7-1234567", "7.1234567", null)]
    [InlineData("7-1234567", null, "7/1234567")]
    public void Any_other_difference_still_refuses_the_wage_file(string company, string? bank, string? agent)
    {
        var (id, issue) = WpsEstablishmentAuthority.Resolve(company, bank, agent, isKsa: true);
        id.Should().BeNull();
        issue!.Code.Should().Be(WpsEstablishmentAuthority.ConflictCode);
    }

    [Fact]
    public void The_comparison_key_strips_spaces_and_hyphens_and_nothing_else()
    {
        WpsEstablishmentAuthority.ComparisonKey(" 7 - 12-34 567 ").Should().Be("71234567");
        WpsEstablishmentAuthority.ComparisonKey("7_1.2/3").Should().Be("7_1.2/3");
    }

    [Fact]
    public void A_saudi_company_must_carry_a_valid_mol_number_at_create()
    {
        var request = new Zayra.Api.Application.Organization.CompanyRequest("Co", null, null, "SA", null, "1010000001", null, "0000", null, null, "SAR");
        OrganizationSetupService.SaudiIdentifierProblem(request, creating: true).Should().NotBeNull();
        OrganizationSetupService.SaudiIdentifierProblem(request with { WpsEmployerId = "7-1234567" }, creating: true).Should().BeNull();
        OrganizationSetupService.SaudiIdentifierProblem(request with { WpsEmployerId = "" }, creating: true).Should().BeNull("empty keeps today's behaviour");
        OrganizationSetupService.SaudiIdentifierProblem(request with { WpsEmployerId = "7-1234567", UnifiedNationalNumber = "123" }, creating: true)
            .Should().NotBeNull("a Unified National Number is 10 digits");
        OrganizationSetupService.SaudiIdentifierProblem(request with { CountryCode = "AE" }, creating: true).Should().BeNull();
    }

    // ── pack helpers ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Fri,Sat", "Fri,Sat", "Sun-Thu")]
    [InlineData("Sat-Sun", "Sun,Sat", "Mon-Fri")]
    [InlineData("Fri", "Fri", "Sat-Thu")]
    public void Weekend_and_working_week_are_written_in_the_stored_forms(string input, string weekend, string workWeek)
    {
        var days = Zayra.Api.Application.WorkWeek.WorkWeekParser.ParseWeekendDays(input)!;
        CompanySetupPack.FormatWeekend(days).Should().Be(weekend);
        CompanySetupPack.WorkWeekFor(days).Should().Be(workWeek);
    }

    [Fact]
    public void The_saudi_tenant_default_country_row_uses_the_days_per_year_scale()
    {
        var row = CompanySetupPack.TenantDefaultGccRow(Guid.NewGuid(), "SA");
        row.CompanyId.Should().BeNull();
        row.EosbEnabled.Should().BeTrue();
        row.EosbYears1To5Rate.Should().Be(15m);
        row.EosbYearsAbove5Rate.Should().Be(30m);
        row.EosbMinYears.Should().Be(0);
        row.WeekendDays.Should().Be("Fri,Sat");
        // Equal to the Art. 84 floor, so the calculator applies it without a below-floor refusal notice.
        var notices = new List<string>();
        var (t1, t2) = Zayra.Api.Infrastructure.CountryPack.Ksa.KsaEndOfServiceCalculator.ApplyPolicyRates(
            new Zayra.Api.Application.CountryPack.EosbPolicyOverride(row.EosbYears1To5Rate, row.EosbYearsAbove5Rate, row.EosbMinYears), 15m, 30m, notices);
        (t1, t2).Should().Be((15m, 30m));
        notices.Should().BeEmpty();
    }

    [Theory]
    [InlineData("2026-11", true, 2026, 11)]
    [InlineData("2026-13", false, 0, 0)]
    [InlineData("11-2026", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    public void The_first_payroll_month_parses_to_its_first_day(string value, bool ok, int year, int month)
    {
        CompanySetupPlanner.TryParseMonth(value, out var day).Should().Be(ok);
        if (ok) day.Should().Be(new DateOnly(year, month, 1));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend-dotnet", "Zayra.Api"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
