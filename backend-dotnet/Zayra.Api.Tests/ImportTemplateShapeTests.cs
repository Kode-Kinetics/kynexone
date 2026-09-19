using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Employees;

namespace Zayra.Api.Tests;

/// <summary>
/// THE guard that was missing while the import templates rotted.
///
/// A CSV template is a header line plus a worked example row, and CSV rows are POSITIONAL. Add a
/// column to the header and forget the example row and every value from that point on lands in the
/// wrong column — the file still opens, the importer still parses it, and nothing anywhere says so.
/// That is how PR #10's example rows drifted to 8 cells against a 12-column header, 4 against 20,
/// and 11 against 13, and why it could not simply be rebased: three of those five files merged
/// *cleanly* straight into a corrupted template.
///
/// This test makes that class of drift impossible to ship. It does NOT read a hand-written list of
/// templates — a hand-written list is the same maintenance burden that produced the bug. It
/// DISCOVERS every template endpoint by reflecting over the API assembly's controllers, so the next
/// template somebody adds is covered the moment its route exists, without anyone remembering to
/// come back here.
///
/// Three properties are asserted for every discovered template:
///   1. it carries at least one example row per section (an empty template passes a cell-count
///      check vacuously, so "delete the row" must not be a way to make this test green);
///   2. every example row has exactly as many cells as the header it sits under; and
///   3. it names no seeded demo tenant — the live bug this replaces was a pilot customer
///      downloading a template with another tenant's company name printed inside it.
/// </summary>
public sealed class ImportTemplateShapeTests
{
    private static readonly Assembly ApiAssembly = typeof(Csv).Assembly;

    /// <summary>
    /// Company names belonging to the demo/seed tenants (<c>DemoDataSeeder</c>,
    /// <c>IntelliFlowDemoSeeder</c>, <c>KsaDemoTenantSeeder</c>, <c>EnterpriseGroupSeeder</c>).
    /// None of these may ever appear in a file a customer downloads.
    /// </summary>
    private static readonly string[] SeededTenantNames =
        { "IntelliFlow", "Evostel", "Al-Nakheel", "Almarai" };

    // ── Discovery ────────────────────────────────────────────────────────────────────────────

    private sealed record TemplateEndpoint(string Name, string Route, Type Controller, MethodInfo Action);

    /// <summary>
    /// A template route is one whose LAST segment is <c>template</c> or ends in <c>-template</c>:
    /// <c>import-template</c>, <c>structures/import-template</c>, <c>template</c>. That deliberately
    /// excludes the unrelated <c>templates</c> collections (contract templates, assessment templates,
    /// notification templates) and the parameterised <c>template-tasks</c> route, none of which serve
    /// a CSV data format. Every template action is parameterless, which is the second half of the
    /// filter and also what makes them callable here.
    /// </summary>
    private static bool IsTemplateRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route)) return false;
        var last = route.TrimEnd('/').Split('/')[^1];
        return last.Equals("template", StringComparison.OrdinalIgnoreCase)
               || last.EndsWith("-template", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<TemplateEndpoint> DiscoverTemplateEndpoints() =>
        ApiAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition
                        && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetParameters().Length == 0)
                .SelectMany(m => m.GetCustomAttributes<HttpGetAttribute>()
                    .Select(a => a.Template)
                    .Where(IsTemplateRoute)
                    .Select(route => new TemplateEndpoint($"{t.Name}.{m.Name}", route!, t, m))))
            .DistinctBy(e => e.Name)          // route-aliased actions (Payroll) carry two [HttpGet]s
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Calls the action and returns the CSV document(s) it serves — one for a plain CSV template,
    /// several for the migration package, which hands back a dictionary of per-entity CSVs.
    ///
    /// The controller is created WITHOUT its constructor. Every template action is a pure function
    /// of a static header array plus a static example row: none of them touches the database, the
    /// user, or any injected service. Building the real DI graph would mean booting the application
    /// just to read a string constant, and would couple a shape check to every unrelated dependency
    /// those controllers happen to take. If a future template action does start reading injected
    /// state, this throws — loudly, in the test that owns templates — which is the right signal.
    /// </summary>
    private static IReadOnlyList<(string Name, string Csv)> Download(TemplateEndpoint endpoint)
    {
        var controller = (ControllerBase)RuntimeHelpers.GetUninitializedObject(endpoint.Controller);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        object? result;
        try
        {
            result = endpoint.Action.Invoke(controller, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Surface what the endpoint actually threw — Csv.Template refuses a mismatched example
            // row at source, and that message names the two counts. A raw reflection wrapper here
            // would bury the one sentence a reader needs.
            throw new InvalidOperationException(
                $"{endpoint.Name} (GET {endpoint.Route}) threw while building its template: "
                + ex.InnerException.Message, ex.InnerException);
        }

        return result switch
        {
            FileContentResult file =>
                new[] { (endpoint.Name, Encoding.UTF8.GetString(file.FileContents)) },
            ContentResult content =>
                new[] { (endpoint.Name, content.Content ?? string.Empty) },
            ObjectResult obj when obj.Value is IDictionary<string, string> package =>
                package.Select(kv => ($"{endpoint.Name}[{kv.Key}]", kv.Value)).ToArray(),
            _ => throw new InvalidOperationException(
                $"{endpoint.Name} (GET {endpoint.Route}) returned {result?.GetType().Name ?? "null"}, "
                + "which this guard does not know how to read as a CSV template. Teach it that shape "
                + "rather than removing the endpoint from the check."),
        };
    }

    // ── Block parsing ────────────────────────────────────────────────────────────────────────

    /// <summary>One header line and the example rows beneath it. Sectioned templates (the approval
    /// policy file, the organisation-structure package) contain several.</summary>
    private sealed record Block(string HeaderLine, int HeaderCells, List<string> DataRows);

    /// <summary>
    /// Splits a template into header/rows blocks. A blank line or a <c>#</c> comment ends the
    /// current block — exactly how the sectioned templates separate their parts, and how the
    /// frontend's splitOrgPackage reads them.
    /// </summary>
    private static List<Block> SplitBlocks(string csv)
    {
        var blocks = new List<Block>();
        Block? current = null;
        foreach (var line in csv.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) { current = null; continue; }
            if (current is null)
            {
                current = new Block(line, Csv.SplitRow(line).Count, new List<string>());
                blocks.Add(current);
            }
            else current.DataRows.Add(line);
        }
        return blocks;
    }

    // ── The guard ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Watched RED: deleting the trailing <c>"true"</c> from any single example row — say
    /// <c>BranchesController.CsvExampleRow</c> (12 cells) or
    /// <c>PayrollController.SalaryStructureCsvExampleRow</c> (20) — fails this test naming that
    /// endpoint and both counts. Restoring the cell turns it green again.
    /// </summary>
    [Fact]
    public void EveryTemplate_ExampleRowCellCount_MatchesItsHeaderColumnCount()
    {
        var failures = new List<string>();

        foreach (var endpoint in DiscoverTemplateEndpoints())
        {
            foreach (var (name, csv) in Download(endpoint))
            {
                var blocks = SplitBlocks(csv);
                if (blocks.Count == 0)
                {
                    failures.Add($"{name}: served no header row at all.");
                    continue;
                }

                foreach (var block in blocks)
                {
                    // A header-only template passes a cell-count check vacuously. Requiring the
                    // worked example keeps the check from being satisfiable by deleting the row.
                    if (block.DataRows.Count == 0)
                    {
                        failures.Add(
                            $"{name}: header '{Preview(block.HeaderLine)}' carries no example row — "
                            + "every template must ship one worked example per section.");
                        continue;
                    }

                    foreach (var row in block.DataRows)
                    {
                        var cells = Csv.SplitRow(row).Count;
                        if (cells != block.HeaderCells)
                            failures.Add(
                                $"{name}: example row has {cells} cell(s) but its header declares "
                                + $"{block.HeaderCells} column(s). CSV rows are positional, so every "
                                + $"value from the mismatch onwards lands in the wrong column. "
                                + $"header='{Preview(block.HeaderLine)}' row='{Preview(row)}'");
                    }
                }
            }
        }

        failures.Should().BeEmpty(
            "every import template must ship an example row that lines up with its header:\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// The control for the guard above. Discovery is what makes that test cover new templates
    /// automatically — and it is also what would make it pass while measuring nothing, if a route
    /// rename or a refactor quietly emptied the discovered set.
    /// </summary>
    [Fact]
    public void TemplateDiscovery_FindsEveryKnownTemplateEndpoint()
    {
        var discovered = DiscoverTemplateEndpoints();

        discovered.Should().HaveCountGreaterThanOrEqualTo(16,
            "the product ships a CSV template for companies, branches, locations, departments, "
            + "designations, cost centres, grades, approval policies, leave types, leave requests, "
            + "job openings, employees, salary structures, employee salaries, the organisation-"
            + "structure package and the migration package — if this count collapses, discovery "
            + "broke and the shape guard above is silently measuring nothing");

        var names = discovered.Select(e => e.Name).ToList();
        names.Should().Contain("EmployeesController.ImportTemplate",
            "the employee template is the widest and the only registry-derived one");
        names.Should().Contain("PayrollController.StructuresImportTemplate",
            "the 20-column salary-structure template is the one a short example row corrupts most quietly");
    }

    /// <summary>
    /// The live bug this work replaces: a pilot customer downloading an import template and finding
    /// another tenant's company name printed in the example row.
    ///
    /// Watched RED against the pre-fix controllers, which shipped
    /// <c>IntelliFlow Systems LLC,HQ,…</c> (branches) and
    /// <c>Evostel Trading LLC,…</c> (companies).
    /// </summary>
    [Fact]
    public void NoTemplate_ContainsASeededDemoTenantName()
    {
        var offences = new List<string>();

        foreach (var endpoint in DiscoverTemplateEndpoints())
            foreach (var (name, csv) in Download(endpoint))
                foreach (var brand in SeededTenantNames)
                    if (csv.Contains(brand, StringComparison.OrdinalIgnoreCase))
                        offences.Add($"{name} contains the seeded tenant name '{brand}'.");

        offences.Should().BeEmpty(
            "an import template is handed to a customer, so its example row must be a neutral "
            + "placeholder and never another tenant's name:\n" + string.Join("\n", offences));
    }

    // ── Employees: derived from the registry, not hand-written ───────────────────────────────

    /// <summary>
    /// The employee template's columns come from <see cref="EmployeeFieldRegistry"/> — "the ONE
    /// source the template, export header, and importer header-validation all read". Its example row
    /// must come from the same place. A hand-written row would be positional against a list nobody
    /// maintains by hand, so it would drift by exactly one column the first time a field is added to
    /// the catalog — the drift the registry was introduced to end.
    ///
    /// This proves derivation rather than coincidence: it checks the placeholders against each
    /// column's declared input type BY POSITION, which only holds if the row was generated from the
    /// same ordered catalog the header was.
    /// </summary>
    [Fact]
    public void EmployeeTemplate_ExampleRow_IsDerivedFromTheFieldRegistry()
    {
        var columns = EmployeeFieldRegistry.Catalog.Where(d => d.CsvHeader is not null).ToList();
        var template = Download(
            DiscoverTemplateEndpoints().Single(e => e.Name == "EmployeesController.ImportTemplate"))
            .Single().Csv;

        var block = SplitBlocks(template).Single();
        var header = Csv.SplitRow(block.HeaderLine);
        var row = Csv.SplitRow(block.DataRows.Single());

        header.Should().BeEquivalentTo(EmployeeFieldRegistry.CsvHeaders, o => o.WithStrictOrdering());
        row.Should().HaveCount(columns.Count);

        for (var i = 0; i < columns.Count; i++)
        {
            var expected = columns[i].InputType switch
            {
                "date" => "YYYY-MM-DD",
                "number" => "0",
                "email" => "name@example.com",
                "toggle" => "false",
                _ => "EXAMPLE",
            };
            row[i].Should().Be(expected,
                $"column {i} ('{columns[i].CsvHeader}') is declared as '{columns[i].InputType}' in the "
                + "catalog, so its placeholder must be the one derived for that input type — if this "
                + "fails by an offset, the example row and the header are no longer built from the "
                + "same ordered catalog");
        }

        // A registry-derived row cannot be short by construction; this pins the property the
        // hardcoded 25-value row in PR #10 violated against a header that had grown well past it.
        EmployeeFieldRegistry.CsvExampleRow.Should().HaveSameCount(EmployeeFieldRegistry.CsvHeaders);
    }

    // ── Csv.Template(headers, exampleRow) ────────────────────────────────────────────────────

    /// <summary>The overload refuses to emit the corruption rather than emitting it silently.</summary>
    [Fact]
    public void CsvTemplate_RefusesAnExampleRowThatDoesNotMatchTheHeader()
    {
        var act = () => Csv.Template(new[] { "A", "B", "C" }, new[] { "1", "2" });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*2 cell(s)*3 column(s)*positional*");
    }

    /// <summary>
    /// Plan step 1 claimed the new overload inherits the formula-injection escaping for free by going
    /// through <see cref="Csv.Build"/>. This is that claim measured rather than assumed: a template is
    /// a file a customer opens in Excel, so it is exactly as exposed to CSV injection (CWE-1236) as an
    /// export is.
    /// </summary>
    [Fact]
    public void CsvTemplate_ExampleRow_InheritsFormulaInjectionEscapingAndQuoting()
    {
        var csv = Csv.Template(
            new[] { "Formula", "Delimiter", "Quote" },
            new[] { "=1+1", "a,b", "he said \"hi\"" });

        var row = csv.Split('\n')[1];
        row.Should().StartWith("'=1+1", "a leading '=' must be neutralised into text");
        row.Should().Contain("\"a,b\"", "a cell containing the delimiter must be quoted");
        row.Should().Contain("\"he said \"\"hi\"\"\"", "an embedded quote must be doubled");

        // …and it still round-trips as three cells, which is what the shape guard measures.
        Csv.SplitRow(row).Should().HaveCount(3);
    }

    private static string Preview(string line) => line.Length <= 90 ? line : line[..90] + "…";
}
