using FluentAssertions;

namespace Zayra.Api.Tests;

/// <summary>
/// Docs half of the cost-accounting schema contract (kynexone-schema rule: a data-dictionary line for every new column).
/// There is no generated SchemaDocsSync yet, so this pins the hand-written entries: each table that gained a cost-accounting
/// column has a "### `table` — cost accounting" entry in <c>docs/schema/DATA_DICTIONARY.md</c> that names every new column.
/// M1 adds no table, so there is no ownership / retention / RLS-shape row to add (v1-spec 1.13).
/// </summary>
public sealed class CostAccountingSchemaDocsTests
{
    private static readonly Dictionary<string, string[]> NewColumns = new()
    {
        ["cost_centers"] = ["parent_id", "functional_class", "gl_segment"],
        ["gl_accounts"] = ["cost_center_rule"],
        ["gl_account_mappings"] = ["functional_class", "segment_cost_center_id"],
        ["companies"] = ["cost_accounting_enabled", "default_cost_center_id", "cost_centre_small_cell_min"],
        ["employees"] = ["cost_center_is_override"],
        ["payroll_earnings"] = ["cost_center_id", "cost_center_source"],
        ["payroll_deductions"] = ["cost_center_id", "cost_center_source"],
        ["payroll_slips"] = ["cost_center_id"],
        ["payroll_runs"] = ["cost_center_resolution_hash", "cost_accounting_applied"],
        ["eosb_provision_snapshots"] = ["cost_center_id"],
        ["finance_gl_entries"] = ["cost_center_id", "cost_center_code", "cost_center_name", "cost_center_gl_segment", "posting_key"],
        ["gl_journal_export_lines"] = ["cost_center_code", "cost_center_name", "cost_center_gl_segment"],
        ["gl_journal_exports"] = ["amount_scale"],
        ["departments"] = ["cost_center_id"],
        ["positions"] = ["cost_center_id"],
        ["timesheet_entries"] = ["cost_center_id"],
    };

    [Fact]
    public void DataDictionary_HasACostAccountingEntry_ForEveryTableThatGainedAColumn()
    {
        var text = File.ReadAllText(DictionaryPath());
        var missing = new List<string>();
        foreach (var (table, columns) in NewColumns)
        {
            var heading = $"### `{table}` — cost accounting";
            var start = text.IndexOf(heading, StringComparison.Ordinal);
            if (start < 0) { missing.Add($"{table}: no '{heading}' entry"); continue; }
            var end = text.IndexOf("\n### ", start + heading.Length, StringComparison.Ordinal);
            var block = end < 0 ? text[start..] : text[start..end];
            missing.AddRange(columns.Where(c => !(block.Contains($"`{c}`", StringComparison.Ordinal) || block.Contains($"`{c} ", StringComparison.Ordinal))).Select(c => $"{table}.{c}"));
        }
        missing.Should().BeEmpty("every new cost-accounting column needs its data-dictionary line: " + string.Join(", ", missing));
    }

    [Fact]
    public void DataDictionary_StatesWhichNewForeignKeysAreNotValidAndWhy()
    {
        var text = File.ReadAllText(DictionaryPath());
        text.Should().Contain("NOT VALID").And.Contain("CostAccountingFoundation");
    }

    [Fact]
    public void M3Document_RecordsTheExactSqlOfTheFourHeldBackChecks_AndTheForeignKeyValidation()
    {
        var path = Path.Combine(Path.GetDirectoryName(DictionaryPath())!, "M3_COST_ACCOUNTING_VALIDATION.md");
        File.Exists(path).Should().BeTrue();
        var text = File.ReadAllText(path);
        foreach (var fragment in new[]
        {
            "ck_cost_centers__code_format", "ck_finance_gl_entries__amount_nonnegative", "ck_finance_gl_entries__has_account",
            "ck_finance_gl_entries__currency_format", "VALIDATE CONSTRAINT fk_departments__cost_center",
            "scripts/cost-accounting-preflight-audit.sql", "NOT VALID",
        })
            text.Should().Contain(fragment);
    }

    private static string DictionaryPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "schema", "DATA_DICTIONARY.md")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the repository root must be findable from the test binary");
        return Path.Combine(dir!.FullName, "docs", "schema", "DATA_DICTIONARY.md");
    }
}
