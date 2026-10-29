using System.Text.RegularExpressions;
using FluentAssertions;
using Zayra.Api.Infrastructure.FieldEncryption;

namespace Zayra.Api.Tests.FieldEncryption;

/// <summary>
/// The save interceptor is the only thing that keeps a protected column's ciphertext and blind index in step with its plaintext,
/// and it only sees tracked <c>SaveChanges</c>. A write that goes around it (<c>ExecuteUpdate</c>, raw SQL, a bulk import) leaves a
/// stale ciphertext that readers will later trust (a payment file built from an old IBAN). This ratchet fails on any such write to a
/// catalogued column or table outside Migrations, so a new one has to be routed through the interceptor (or an explicit re-encrypt
/// in the same unit of work) and declared here with its reason.
/// </summary>
public sealed partial class FieldEncryptionWritePathRatchetTests
{
    /// <summary>file (relative to Zayra.Api) -> approved findings. Empty on purpose; add an entry only with a reason.</summary>
    private static readonly Dictionary<string, int> Approved = new(StringComparer.Ordinal);

    private static readonly string[] Tables =
    [
        "employees", "employee_drafts", "employee_payroll_profiles", "payroll_payment_records", "sif_file_records",
        "loan_disbursement_lines", "visa_records", "passport_records", "employee_dependents",
    ];

    private static string PropertyAlternation => string.Join("|", EncryptedFieldCatalog.All.Select(f => f.Property).Distinct());

    [GeneratedRegex(@"\b(?:BeginBinaryImport|BeginTextImport|SqlBulkCopy|BulkInsert|BulkUpdate|BulkMerge)\b|\bCOPY\s+\w+\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex BulkLoad();

    private static string RawWriteOfProtectedTable() =>
        @"\b(?:UPDATE|INSERT\s+INTO|MERGE\s+INTO)\s+(?:public\.)?""?(?:" + string.Join("|", Tables) + @")""?\b";

    private static List<string> Scan(string source)
    {
        var code = string.Join('\n', source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        var findings = new List<string>();
        var setCatalogued = new Regex(@"SetProperty\(\s*\w+\s*=>\s*\w+\.(?:" + PropertyAlternation + @")\b");
        for (var at = code.IndexOf("ExecuteUpdate", StringComparison.Ordinal); at >= 0; at = code.IndexOf("ExecuteUpdate", at + 1, StringComparison.Ordinal))
        {
            var start = code.LastIndexOfAny([';', '{', '}'], at) + 1;
            var end = code.IndexOf(';', at);
            var statement = code[start..(end < 0 ? code.Length : end)];
            if (setCatalogued.IsMatch(statement)) findings.Add(Squash(statement));
        }
        foreach (Match m in Regex.Matches(code, RawWriteOfProtectedTable(), RegexOptions.IgnoreCase)) findings.Add(Squash(m.Value));
        foreach (Match m in BulkLoad().Matches(code)) findings.Add(Squash(m.Value));
        return findings;
    }

    private static string Squash(string s) { var f = Regex.Replace(s, @"\s+", " ").Trim(); return f.Length <= 160 ? f : f[..160] + "…"; }

    private static Dictionary<string, List<string>> ScanTree()
    {
        var root = ApiRoot();
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (rel.StartsWith("Migrations/") || rel.StartsWith("obj/") || rel.StartsWith("bin/") || rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            if (rel.StartsWith("Infrastructure/FieldEncryption/")) continue; // the encryption code itself writes only through the interceptor/backfill
            var found = Scan(File.ReadAllText(file));
            if (found.Count > 0) result[rel] = found;
        }
        return result;
    }

    [Fact]
    public void NoWriteToAProtectedColumn_GoesAroundTheInterceptor()
    {
        var offenders = ScanTree().Where(kv => !Approved.TryGetValue(kv.Key, out var ok) || kv.Value.Count > ok)
            .Select(kv => $"{kv.Key}:\n      " + string.Join("\n      ", kv.Value)).ToList();
        offenders.Should().BeEmpty("a bulk or raw write to an encrypted column's table skips the interceptor and leaves ciphertext stale. " +
            "Write through SaveChanges, or re-encrypt in the same unit of work and add the site to Approved with its reason.\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheAllowlistHasNoSlackAndTheScannerStillSeesWhatItMust()
    {
        var actual = ScanTree();
        Approved.Where(kv => !actual.TryGetValue(kv.Key, out var f) || f.Count != kv.Value).Should().BeEmpty();
    }

    [Theory]
    [InlineData("await db.Employees.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.IqamaNumber, \"x\"), ct);", 1)]
    [InlineData("await db.EmployeePayrollProfiles.ExecuteUpdateAsync(s => s.SetProperty(p => p.Iban, iban), ct);", 1)]
    [InlineData("await db.Employees.ExecuteUpdateAsync(s => s.SetProperty(e => e.FullName, \"x\"), ct);", 0)]
    [InlineData("await db.Database.ExecuteSqlRawAsync(\"UPDATE employees SET bank_iban = {0} WHERE id = {1}\", a, b);", 1)]
    [InlineData("await db.Database.ExecuteSqlRawAsync(\"INSERT INTO visa_records (id) VALUES ({0})\", a);", 1)]
    [InlineData("await db.Database.ExecuteSqlRawAsync(\"UPDATE users SET is_active = false\");", 0)]
    [InlineData("using var w = conn.BeginBinaryImport(\"COPY employees (id) FROM STDIN\");", 2)]
    public void Scanner(string source, int expected) => Scan(source).Should().HaveCount(expected);

    private static string ApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir?.Parent is not null; i++)
        {
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException($"Could not locate the Zayra.Api source root from {AppContext.BaseDirectory}.");
    }
}
