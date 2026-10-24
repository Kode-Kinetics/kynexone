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

    // ── Addendum A: the wire shape of existing-company mode ─────────────────────────────────────────────

    [Fact]
    public void Existing_mode_fields_are_camel_case_and_a_create_mode_body_does_not_carry_them()
    {
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var company = new Zayra.Api.Application.CompanySetup.CompanySetupCompany("Co", null, null, "SA", "SAR", "1010000001", null, null, null, null, null, null, null, null, "2026-11");
        var rules = new Zayra.Api.Application.CompanySetup.CompanySetupRules(new("Fri,Sat", false), new(true), new(true), new("EMP", true, false, true, true, 4, 1));
        var admin = new Zayra.Api.Application.CompanySetup.CompanySetupCompanyAdmin("groupOnly", null, null, null, null);
        var create = new Zayra.Api.Application.CompanySetup.CompanySetupDraftBody("defaults", null, company,
            [new("HQ", "HQ", null, "Riyadh", "Asia/Riyadh", true, [new("L1", "Site", null, null, null, null)])], [new("CC", "Cost", null)], [], [], [], rules, admin);
        var createJson = System.Text.Json.JsonSerializer.Serialize(create, web);
        createJson.Should().NotContain("existingCompanyId").And.NotContain("existingId", "a new-company body is unchanged on the wire");

        var id = Guid.NewGuid();
        var existing = create with
        {
            ExistingCompanyId = id,
            Branches = [new("HQ", "HQ", null, "Riyadh", "Asia/Riyadh", true, [new("L1", "Site", null, null, null, null, id)], id)],
            CostCentres = [new("CC", "Cost", null, id)],
            Departments = [new("HR", "HR", null, null, null, null, id)],
            Designations = [new("D", "D", null, null, null, id)],
            Grades = [new("G", "G", null, 1, 1, 2, 3, id)],
        };
        var json = System.Text.Json.JsonSerializer.Serialize(existing, web);
        json.Should().Contain($"\"existingCompanyId\":\"{id}\"");
        System.Text.RegularExpressions.Regex.Matches(json, "\"existingId\":").Count.Should().Be(6, "branch, location, cost centre, department, designation, grade");
        System.Text.Json.JsonSerializer.Deserialize<Zayra.Api.Application.CompanySetup.CompanySetupDraftBody>(json, web)
            .Should().BeEquivalentTo(existing, "a draft round-trips through its JSON body");

        Zayra.Api.Application.CompanySetup.CompanySetupVocabulary.ActionUpdate.Should().Be("update");
        Zayra.Api.Application.CompanySetup.CompanySetupVocabulary.ActionUnchanged.Should().Be("unchanged");
        var context = new Zayra.Api.Application.CompanySetup.CompanySetupContext(false, null, null, "", "", 0, null, true, [], new([], [], []), null)
        {
            ManageableCompanies = [new(id, "Co", null, "SA", 3, 9, null)],
        };
        System.Text.Json.JsonSerializer.Serialize(context, web).Should()
            .Contain("\"manageableCompanies\":[{\"id\"").And.Contain("\"legalNameAr\":null").And.Contain("\"readinessDone\":3")
            .And.Contain("\"readinessTotal\":9").And.Contain("\"openDraftId\":null");
    }

    // ── Addendum A9: the three-way comparison ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("A", "A", "A", CompanySetupBaseline.Outcome.TakeDatabase)]   // nobody changed it
    [InlineData("A", "A", "B", CompanySetupBaseline.Outcome.TakeDatabase)]   // changed elsewhere only: the newer value stands
    [InlineData("B", "A", "A", CompanySetupBaseline.Outcome.TakeDraft)]      // changed by the user only: update
    [InlineData("B", "A", "B", CompanySetupBaseline.Outcome.TakeDatabase)]   // both, the same way: nothing to write
    [InlineData("B", "A", "C", CompanySetupBaseline.Outcome.Conflict)]       // both, differently: the user decides
    [InlineData(null, "A", "C", CompanySetupBaseline.Outcome.Conflict)]
    [InlineData("B", null, null, CompanySetupBaseline.Outcome.TakeDraft)]    // nothing existed at prefill (no cutover, no rule)
    public void Each_field_is_judged_on_draft_baseline_and_database(string? draft, string? baseline, string? database, CompanySetupBaseline.Outcome expected) =>
        CompanySetupBaseline.Decide(draft, baseline, database).Should().Be(expected);

    [Fact]
    public void The_projection_of_an_untouched_prefill_equals_its_baseline_and_ignores_numeric_scale()
    {
        var row = new Zayra.Api.Models.Location { Code = "L1", NameEn = " Site ", Latitude = 21.500000m, Longitude = 39.2m, GeofenceRadiusMeters = 150.00m };
        var fromRow = CompanySetupBaseline.Location(row);
        CompanySetupBaseline.Location(new Zayra.Api.Application.CompanySetup.CompanySetupLocation("L1", "Site", null, 21.5m, 39.20m, 150m))
            .Should().BeEquivalentTo(fromRow);
        fromRow["latitude"].Should().Be("21.5");
        CompanySetupBaseline.WorkWeek("Sat,Fri", true).Should().BeEquivalentTo(CompanySetupBaseline.WorkWeek("Fri,Sat", true));
    }

    [Fact]
    public void A_baseline_round_trips_through_the_draft_json_unchanged()
    {
        var baseline = new Dictionary<string, string?> { ["nameEn"] = "Dammam", ["nameAr"] = null, ["isHeadOffice"] = "false" };
        var branch = new Zayra.Api.Application.CompanySetup.CompanySetupBranch("DMM", "Dammam", null, "Dammam", "Asia/Riyadh", false, [], Guid.NewGuid(), baseline);
        var json = System.Text.Json.JsonSerializer.Serialize(branch, CompanySetupService.BodyJson);
        json.Should().Contain("\"baseline\":{\"nameEn\":\"Dammam\",\"nameAr\":null,\"isHeadOffice\":\"false\"}");
        System.Text.Json.JsonSerializer.Deserialize<Zayra.Api.Application.CompanySetup.CompanySetupBranch>(json, CompanySetupService.BodyJson)!
            .Baseline.Should().BeEquivalentTo(baseline);
    }

    // ── Addendum B: per-company organisation rows (baselines, wire shapes) ──────────────────────────────

    [Fact]
    public void Department_designation_and_grade_baselines_project_the_row_and_the_draft_the_same_way()
    {
        var branchId = Guid.NewGuid(); var ccId = Guid.NewGuid(); var parentId = Guid.NewGuid(); var deptId = Guid.NewGuid(); var gradeId = Guid.NewGuid();
        var codes = new Dictionary<Guid, string> { [parentId] = "ADMIN", [deptId] = "OPS" };
        var branches = new Dictionary<Guid, string> { [branchId] = "HQ" };
        var ccs = new Dictionary<Guid, string> { [ccId] = "CC_OPS" };
        var grades = new Dictionary<Guid, string> { [gradeId] = "G1" };
        var dept = new Zayra.Api.Models.Department { Id = deptId, Code = "OPS", NameEn = " Operations ", NameAr = "", ParentDepartmentId = parentId, BranchId = branchId, CostCenterId = ccId };
        CompanySetupBaseline.Department(dept, codes, branches, ccs).Should().BeEquivalentTo(
            CompanySetupBaseline.Department(new Zayra.Api.Application.CompanySetup.CompanySetupDepartment("ops", "Operations", null, "admin", "hq", "cc_ops")));
        var desig = new Zayra.Api.Models.Designation { Code = "OFF", TitleEn = "Officer", TitleAr = "", DepartmentId = deptId, GradeId = gradeId };
        CompanySetupBaseline.Designation(desig, codes, grades).Should().BeEquivalentTo(
            CompanySetupBaseline.Designation(new Zayra.Api.Application.CompanySetup.CompanySetupDesignation("OFF", "Officer", null, "OPS", "G1")));
        var grade = new Zayra.Api.Models.Grade { Code = "G1", Name = "Grade 1", Level = 1, MinSalary = 3000.00m, MidSalary = 4500m, MaxSalary = 6000.0m };
        var fromRow = CompanySetupBaseline.Grade(grade);
        fromRow.Should().BeEquivalentTo(CompanySetupBaseline.Grade(new Zayra.Api.Application.CompanySetup.CompanySetupGrade("G1", "Grade 1", null, 1, 3000m, 4500m, 6000m)));
        fromRow["minSalary"].Should().Be("3000", "scale-free");
        CompanySetupBaseline.CostCentre(new Zayra.Api.Models.CostCenter { Code = "CC", Name = "Ops", NameAr = "العمليات" })
            .Should().ContainKey("nameAr").WhoseValue.Should().Be("العمليات");
        CompanySetupBaseline.AbsentMeansEmpty("costCentre", "nameAr").Should().BeTrue("a cost-centre baseline saved before Addendum B has no Arabic name");
        CompanySetupBaseline.AbsentMeansEmpty("department", "nameAr").Should().BeFalse();
    }

    [Fact]
    public void Section_apply_and_copy_rows_have_the_typescript_mirror_shapes()
    {
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var id = Guid.NewGuid();
        var copy = new Zayra.Api.Application.CompanySetup.CopyRows("grades", id, Grades: [new("G1", "Grade 1", null, 1, 1, 2, 3)]);
        var json = System.Text.Json.JsonSerializer.Serialize(copy, web);
        json.Should().StartWith($"{{\"section\":\"grades\",\"fromCompanyId\":\"{id}\",\"grades\":[")
            .And.NotContain("costCentres").And.NotContain("departments").And.NotContain("existingId").And.NotContain("baseline");
        Zayra.Api.Application.CompanySetup.CompanySetupVocabulary.Sections.All.Should().Equal("costCentres", "departments", "grades", "designations");
        Zayra.Api.Application.CompanySetup.CompanySetupVocabulary.Sections.IsValid("branches").Should().BeFalse();
        var context = new Zayra.Api.Application.CompanySetup.CompanySetupContext(true, null, null, "", "", 0, null, true, [], null, null);
        System.Text.Json.JsonSerializer.Serialize(context, web).Should().Contain("\"sharedCatalog\":null").And.Contain("\"gradesCurrency\":null");
        System.Text.Json.JsonSerializer.Serialize(new Zayra.Api.Application.CompanySetup.CompanySetupSectionApplyRequest(4), web).Should().Be("{\"version\":4}");
    }

    // ── Data-entry audit: how registration numbers, VAT and coordinates are read ──────────────────────

    [Theory]
    [InlineData("١٠١٠٠٠٠٠٠١", "1010000001")]            // Arabic-Indic digits
    [InlineData("۱۰۱۰۰۰۰۰۰۱", "1010000001")]            // Persian digits
    [InlineData(" 1010 000-001 ", "1010000001")]         // spaces and dashes in a digits-only number
    [InlineData("1010–000 001", "1010000001")] // en dash, no-break space
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Cr_700_and_vat_numbers_are_read_as_ascii_digits_without_spaces_or_dashes(string? typed, string expected) =>
        CompanySetupDataEntry.DigitsOnlyNumber(typed).Should().Be(expected);

    [Theory]
    [InlineData("٧-١٢٣٤٥٦٧", "7-1234567")]
    [InlineData(" 7 – 1234567 ", "7-1234567")]  // a typed dash is the one hyphen the MOL format allows
    [InlineData("۷۱۲۳۴۵۶۷", "71234567")]
    public void The_mol_establishment_id_keeps_its_one_hyphen_and_loses_spaces(string typed, string expected)
    {
        CompanySetupDataEntry.EstablishmentId(typed).Should().Be(expected);
        var issues = new List<SaudiBankExportIssueDto>();
        KsaWageFileRules.ValidateEstablishmentId(CompanySetupDataEntry.EstablishmentId(typed), issues);
        issues.Should().BeEmpty("the normalised value is a valid establishment id");
    }

    [Theory]
    [InlineData("300000000000003", true)]
    [InlineData("312345678901233", true)]
    [InlineData("300000000000004", false)]   // must end with 3
    [InlineData("200000000000003", false)]   // must start with 3
    [InlineData("30000000000003", false)]    // 14 digits
    [InlineData("3000000000000033", false)]  // 16 digits
    public void A_saudi_vat_number_is_15_digits_starting_and_ending_with_3(string vat, bool valid) =>
        CompanySetupDataEntry.IsSaudiVatNumber(vat).Should().Be(valid);

    [Fact]
    public void The_vat_country_mol_and_700_problems_carry_arabic_text()
    {
        CompanySetupDataEntry.VatInvalidMessageAr.Should().NotBeNullOrWhiteSpace();
        CompanySetupDataEntry.CountryInvalidMessage.Should().Be("Choose the company's country from the list.");
        CompanySetupDataEntry.CountryInvalidMessageAr.Should().NotBeNullOrWhiteSpace();
        foreach (var code in new[]
                 {
                     KsaWageFileRules.Codes.EstablishmentMissing, KsaWageFileRules.Codes.EstablishmentInvalid, KsaWageFileRules.Codes.EstablishmentPlaceholder,
                     KsaWageFileRules.Codes.NationalUnifiedNoMissing, KsaWageFileRules.Codes.NationalUnifiedNoInvalid,
                 })
            CompanySetupDataEntry.ArabicFor(code).Should().NotBeNullOrWhiteSpace(code);
        CompanySetupDataEntry.ArabicFor("something_else").Should().BeNull();
    }

    [Theory]
    [InlineData(0.0, 0.0, true)]
    [InlineData(0.0, 39.2, false)]
    [InlineData(21.5, 0.0, false)]
    [InlineData(null, null, false)]
    public void Coordinates_of_exactly_zero_zero_are_missing_not_a_geofence(double? lat, double? lng, bool missing)
    {
        decimal? la = lat is null ? null : (decimal)lat.Value, lo = lng is null ? null : (decimal)lng.Value;
        CompanySetupDataEntry.IsZeroPoint(la, lo).Should().Be(missing);
        var kept = CompanySetupDataEntry.Coordinates(la, lo);
        if (missing) kept.Should().Be(((decimal?)null, (decimal?)null));
        else kept.Should().Be((la, lo));
    }

    // ── Arabic reasons (PlanItem.ReasonAr) ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Changes: name (English), city.", "التغييرات: الاسم (بالإنجليزية)، المدينة.")]
    [InlineData("Changes: from 2026-11-01.", "التغييرات: كانت 2026-11-01.")]
    [InlineData("Changes: weekend, from Fri,Sat.", "التغييرات: العطلة الأسبوعية، كانت Fri,Sat.")]
    [InlineData("Changes: parent department, branch, cost centre, title (Arabic), salary band, next number.", null)]
    [InlineData(CompanySetupReasons.Kept, null)]
    [InlineData("This company already has its 2026 holiday calendar; it is kept as it is.", null)]
    [InlineData("Statutory default for SA, created for this company.", null)]
    [InlineData("This company already has its SA default; it is kept as it is.", null)]
    [InlineData("Overrides the workspace weekend (Fri,Sat) for this company only.", null)]
    [InlineData("Already configured for this workspace.", null)]
    [InlineData("No settings exist for this country yet; the country pack defaults are written once and never overwritten.", null)]
    [InlineData("This company's own weekend, kept as it is.", null)]
    [InlineData("Same as the workspace setting; no override is written.", null)]
    [InlineData("The workspace working week applies.", null)]
    [InlineData("Invited as this company's super admin (Admin, this company only).", null)]
    [InlineData("Already a group super admin, so they manage this company already.", null)]
    [InlineData("Already a super admin of this company.", null)]
    [InlineData("Gets access to this company with their Admin role; their other access is unchanged.", null)]
    public void Every_reason_the_planner_writes_has_arabic(string reason, string? exact)
    {
        var ar = CompanySetupReasons.Arabic(reason);
        ar.Should().NotBeNullOrWhiteSpace(reason);
        ar!.Should().MatchRegex("[؀-ۿ]");
        if (exact is not null) ar.Should().Be(exact);
    }

    [Fact]
    public void An_unknown_reason_has_no_invented_arabic_and_the_wire_carries_reason_ar()
    {
        CompanySetupReasons.Arabic("Changes: something new.").Should().BeNull("an unknown field word is never guessed");
        CompanySetupReasons.Arabic(null).Should().BeNull();
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var json = System.Text.Json.JsonSerializer.Serialize(new Zayra.Api.Application.CompanySetup.PlanItem("branch", "HQ", "HQ", "update", "Changes: city.") { ReasonAr = "التغييرات: المدينة." }, web);
        System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("reasonAr").GetString().Should().Be("التغييرات: المدينة.");
    }
}
