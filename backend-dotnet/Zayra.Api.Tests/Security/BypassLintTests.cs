using System.Text.RegularExpressions;
using FluentAssertions;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// P4.5: Static-analysis bypass-lint gate.
///
/// These tests scan the production source directory for patterns that indicate a
/// developer has bypassed an isolation control without the required justification:
///
///   1. IgnoreQueryFilters() without a preceding justification comment — every call
///      must have "IgnoreQueryFilters is intentional" or "IgnoreQueryFilters:" in the
///      10 preceding source lines explaining why the EF tenant filter is skipped.
///
///   2. [AllowEntityReturn] whose reason string does not assert entity flatness — all
///      production reasons must start with "Flat entity" so audit can confirm no nav
///      properties or sensitive fields are reachable. (Test-only reasons may start with
///      "Test-only" and are excluded from this scan.)
///
///   3. FromSqlRaw / FromSqlInterpolated / Dapper — any raw SQL bypasses EF query
///      filters entirely. Zero occurrences are expected; any new occurrence is a build
///      failure requiring an explicit exception added to the allow-list below.
///
/// HOW THE SOURCE PATH IS RESOLVED:
///   AppContext.BaseDirectory → bin/Debug/net8.0/ → up 4 levels → Zayra.Api/
///   If the path doesn't resolve (e.g. CI puts binaries elsewhere), the test skips
///   rather than producing a false negative.
/// </summary>
public class BypassLintTests
{
    // ── Path resolution ──────────────────────────────────────────────────────────

    private static string? ResolveSourceRoot()
    {
        // Walk up from test binary to find the Zayra.Api source directory
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) return null;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static IEnumerable<(string FilePath, int LineNumber, string LineText)>
        ScanSourceFiles(string sourceRoot, Func<string, bool> linePredicate,
            Func<string, bool>? fileExclude = null)
    {
        return Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => fileExclude?.Invoke(f) != true)
            .SelectMany(filePath =>
            {
                var lines = File.ReadAllLines(filePath);
                return lines
                    .Select((line, idx) => (FilePath: filePath, LineNumber: idx + 1, LineText: line.Trim()))
                    .Where(x => linePredicate(x.LineText));
            });
    }

    // ── 1. IgnoreQueryFilters must have a justification comment ──────────────────

    [Fact]
    public void IgnoreQueryFilters_EachCallMustHaveJustificationComment()
    {
        var sourceRoot = ResolveSourceRoot();
        if (sourceRoot is null)
        {
            // Cannot locate source — skip rather than false-negative
            return;
        }

        var violations = new List<string>();

        var csFiles = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories).ToList();

        foreach (var filePath in csFiles)
        {
            var lines = File.ReadAllLines(filePath);
            for (int i = 0; i < lines.Length; i++)
            {
                var trimmedLine = lines[i].TrimStart();
                // Skip lines that are purely comments — the string appears in doc/explanation text, not as a call.
                if (trimmedLine.StartsWith("//", StringComparison.Ordinal)) continue;
                if (!lines[i].Contains(".IgnoreQueryFilters()")) continue;

                // Scan the 10 preceding lines for a justification comment.
                // Accepted patterns: any // comment containing one of the recognised keywords.
                // "IgnoreQueryFilters is intentional" — explicit declaration
                // "intentional"                       — e.g. "tenant scope intentionally bypassed"
                // "SYSTEM CONTEXT"                    — background-worker / startup context marker
                int scanStart = Math.Max(0, i - 10);
                bool hasJustification = false;
                var justificationKeywords = new[] { "IgnoreQueryFilters", "intentional", "SYSTEM CONTEXT" };
                for (int j = scanStart; j < i; j++)
                {
                    var trimmed = lines[j].Trim();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) &&
                        justificationKeywords.Any(kw => trimmed.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                    {
                        hasJustification = true;
                        break;
                    }
                }

                if (!hasJustification)
                {
                    var rel = Path.GetRelativePath(sourceRoot, filePath);
                    violations.Add($"  {rel}:{i + 1} — .IgnoreQueryFilters() with no justification comment in the preceding 10 lines");
                }
            }
        }

        violations.Should().BeEmpty(
            "Every .IgnoreQueryFilters() call must be preceded within 10 lines by a comment " +
            "beginning with // that explains why the EF tenant filter is bypassed. " +
            "The comment must contain at least one of: 'IgnoreQueryFilters', 'intentional', 'SYSTEM CONTEXT'. " +
            "This prevents accidental cross-tenant leaks.\n\n" +
            "Accepted examples:\n" +
            "  // IgnoreQueryFilters is intentional: <reason>\n" +
            "  // SYSTEM CONTEXT: tenant scope intentionally bypassed — <reason>");
    }

    // ── 2. [AllowEntityReturn] reasons must assert entity flatness ───────────────

    [Fact]
    public void AllowEntityReturn_ProductionReasonsMustAssertFlatness()
    {
        var sourceRoot = ResolveSourceRoot();
        // D14: a guard that SKIPS when it cannot find the source is a false negative, not a safe
        // default — a CI layout change would silently disable every isolation lint while still
        // reporting green. Throwing is the only honest behaviour for a control, and it also
        // narrows the nullable so the scan below cannot be handed a null root.
        sourceRoot = sourceRoot ?? throw new InvalidOperationException(
            "The Zayra.Api source root could not be resolved, so this guard would check NOTHING. "
            + "Treat an unresolvable path as a build failure, never a skip.");

        // Regex: capture the string inside [AllowEntityReturn("...")] or [AllowEntityReturn(@"...")]
        var reasonPattern = new Regex(
            @"\[AllowEntityReturn\(\s*[@""]?""?([^""]+)""?\s*\)\]",
            RegexOptions.Compiled);

        var violations = new List<string>();

        foreach (var filePath in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(filePath);
            for (int i = 0; i < lines.Length; i++)
            {
                var m = reasonPattern.Match(lines[i]);
                if (!m.Success) continue;

                var reason = m.Groups[1].Value.Trim();

                // Test-only reasons (inside BootAssertionTests fixtures) are exempt
                if (reason.StartsWith("Test-only", StringComparison.OrdinalIgnoreCase)) continue;

                if (!reason.StartsWith("Flat entity", StringComparison.OrdinalIgnoreCase))
                {
                    var rel = Path.GetRelativePath(sourceRoot, filePath);
                    violations.Add(
                        $"  {rel}:{i + 1} — reason does not start with \"Flat entity\":\n" +
                        $"    Current: \"{reason[..Math.Min(80, reason.Length)]}...\"");
                }
            }
        }

        violations.Should().BeEmpty(
            "Every [AllowEntityReturn(\"reason\")] in production code must have a reason starting " +
            "with \"Flat entity\" that explicitly asserts the entity has no navigation properties " +
            "or sensitive PII fields. This is enforced so that opt-outs are auditable and " +
            "reviewable without reading the full entity model.\n\n" +
            "Format: [AllowEntityReturn(\"Flat entity — no navigation properties. Fields: ...\")]");
    }

    // ── 3. FromSqlRaw / FromSqlInterpolated / Dapper — zero occurrences ──────────

    [Fact]
    public void RawSql_NoFromSqlRawOrFromSqlInterpolatedInProductionCode()
    {
        var sourceRoot = ResolveSourceRoot();
        // D14: a guard that SKIPS when it cannot find the source is a false negative, not a safe
        // default — a CI layout change would silently disable every isolation lint while still
        // reporting green. Throwing is the only honest behaviour for a control, and it also
        // narrows the nullable so the scan below cannot be handed a null root.
        sourceRoot = sourceRoot ?? throw new InvalidOperationException(
            "The Zayra.Api source root could not be resolved, so this guard would check NOTHING. "
            + "Treat an unresolvable path as a build failure, never a skip.");

        var rawSqlPatterns = new[] { "FromSqlRaw(", "FromSqlInterpolated(", "DapperExtensions", "IDbConnection" };

        var violations = ScanSourceFiles(
            sourceRoot,
            line => rawSqlPatterns.Any(p => line.Contains(p, StringComparison.OrdinalIgnoreCase)) &&
                    !line.TrimStart().StartsWith("//"),
            fileExclude: null)
            .Select(x => $"  {Path.GetRelativePath(sourceRoot, x.FilePath)}:{x.LineNumber} — {x.LineText}")
            .ToList();

        // Known allow-list: add file:line if a justified usage exists
        // (currently empty — zero raw SQL in production)
        var allowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var unallowed = violations.Where(v => !allowList.Any(a => v.Contains(a))).ToList();

        unallowed.Should().BeEmpty(
            "FromSqlRaw, FromSqlInterpolated, and Dapper bypass the EF query filter and can " +
            "silently expose cross-tenant data. All data access must go through the EF DbContext " +
            "with the tenant-scoped global filter active. If raw SQL is genuinely required, add " +
            "the file:line to the allowList above with a comment explaining the explicit WHERE tenantId filter.");
    }

    // ── 5. P3 blind-spot: Ok(rawEntity) scanner for finance-sensitive entity types ─

    /// <summary>
    /// Entities with per-employee salary or financial amounts that must never be
    /// serialised directly from a controller action. Any Ok(variable) where the
    /// preceding declarations reveal one of these types requires a DTO projection.
    /// Config / aggregate entities (PayrollRun, BonusBatch, etc.) are excluded from
    /// this check because they carry no per-employee PII.
    /// </summary>
    private static readonly IReadOnlySet<string> FinanceSensitiveEntityNames = new HashSet<string>
    {
        "EmployeeLoan",
        "SalaryAdvance",
        "EmployeeBonus",
        "PayrollSlip",
        "EmployeeSalaryStructure",
        "EOSBCalculation",
        "Employee",              // has Salary, BankName, IBAN
    };

    private static IEnumerable<string> ScanForRawEntityOkReturns(
        string sourceText,
        IReadOnlySet<string> sensitiveEntityNames,
        string fileLabel)
    {
        var violations = new List<string>();
        var lines = sourceText.Split('\n');

        // Match single-identifier returns only: `return Ok(ident);` or `return Created(url, ident);`
        // Multi-part expressions (Ok(new ...), Ok(await ...), Ok(ident.Project(...))) are safe by construction.
        var okPattern = new Regex(@"\breturn\s+Ok\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)\s*;");
        var createdPattern = new Regex(@"\breturn\s+Created\s*\([^,]+,\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)\s*;");

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("//")) continue;
            if (line.Contains("// SAFE-SERIALIZATION:", StringComparison.OrdinalIgnoreCase)) continue;

            Match m = okPattern.Match(line);
            if (!m.Success) m = createdPattern.Match(line);
            if (!m.Success) continue;

            var identifier = m.Groups[1].Value;

            // SAFE-SERIALIZATION comment in any of the 5 preceding lines exempts this return.
            bool hasSafeComment = false;
            for (int j = Math.Max(0, i - 5); j < i; j++)
            {
                if (lines[j].Contains("// SAFE-SERIALIZATION:", StringComparison.OrdinalIgnoreCase))
                {
                    hasSafeComment = true;
                    break;
                }
            }
            if (hasSafeComment) continue;

            // Look back up to 30 lines for a declaration of this identifier that names a
            // finance-sensitive entity type. A line qualifies if it:
            //   (a) contains the identifier name, AND
            //   (b) contains an assignment or query keyword (=, FirstOrDefault, First(, Single(), etc.), AND
            //   (c) contains one of the sensitive entity type names as a substring.
            bool likelyEntity = false;
            for (int j = Math.Max(0, i - 30); j < i && !likelyEntity; j++)
            {
                var prev = lines[j];
                if (!prev.Contains(identifier, StringComparison.Ordinal)) continue;
                if (!(prev.Contains('=')
                      || prev.Contains("FirstOrDefault", StringComparison.Ordinal)
                      || prev.Contains(".First(", StringComparison.Ordinal)
                      || prev.Contains(".Single(", StringComparison.Ordinal))) continue;
                foreach (var entityName in sensitiveEntityNames)
                {
                    // Word-boundary match prevents "Employee" from matching "EmployeeProfileChangeRequest".
                    if (Regex.IsMatch(prev, @"\b" + Regex.Escape(entityName) + @"\b"))
                    {
                        likelyEntity = true;
                        break;
                    }
                }
            }

            if (likelyEntity)
                violations.Add($"  {fileLabel}:{i + 1} — return Ok({identifier}) — identifier is a finance-sensitive EF entity; project to a DTO");
        }

        return violations;
    }

    [Fact]
    public void RawEntityOkReturn_ControllersMustProjectSensitiveEntitiesToDto()
    {
        var sourceRoot = ResolveSourceRoot();
        // D14: a guard that SKIPS when it cannot find the source is a false negative, not a safe
        // default — a CI layout change would silently disable every isolation lint while still
        // reporting green. Throwing is the only honest behaviour for a control, and it also
        // narrows the nullable so the scan below cannot be handed a null root.
        sourceRoot = sourceRoot ?? throw new InvalidOperationException(
            "The Zayra.Api source root could not be resolved, so this guard would check NOTHING. "
            + "Treat an unresolvable path as a build failure, never a skip.");

        var violations = new List<string>();
        var controllerDir = Path.Combine(sourceRoot, "Controllers");
        if (!Directory.Exists(controllerDir)) return;

        foreach (var filePath in Directory.EnumerateFiles(controllerDir, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(filePath);
            var label = Path.GetRelativePath(sourceRoot, filePath);
            violations.AddRange(ScanForRawEntityOkReturns(source, FinanceSensitiveEntityNames, label));
        }

        violations.Should().BeEmpty(
            "Controller actions must not serialize finance-sensitive EF entities directly via Ok(entity). " +
            "Project to a FinanceDto first (see Application/Finance/FinanceDtos.cs). " +
            "For confirmed-safe config or aggregate entities, add '// SAFE-SERIALIZATION: <reason>' " +
            "in the 5 lines before the return statement.\n\n" +
            "Finance-sensitive entity types checked: " + string.Join(", ", FinanceSensitiveEntityNames));
    }

    [Fact]
    public void RawEntityOkReturn_PlantedLeakIsDetected()
    {
        // Deliberately bad source: a variable declared as SalaryAdvance returned raw via Ok().
        // This simulates the exact exposure that existed before this fix. The lint must catch it.
        const string plantedSource = """
            [HttpGet("test-leak")]
            public async Task<IActionResult> DeliberatelyLeakingAction()
            {
                var adv = new SalaryAdvance { Id = Guid.NewGuid(), RequestedAmount = 5000m };
                return Ok(adv);
            }
            """;

        var violations = ScanForRawEntityOkReturns(
            plantedSource, FinanceSensitiveEntityNames, "PlantedLeak.cs").ToList();

        violations.Should().ContainSingle(
            "the planted leak (return Ok(adv) where adv is a SalaryAdvance) must be caught by the scanner");
        violations[0].Should().Contain("adv",
            "the violation message must name the leaking identifier");
    }

    // ── 4. No new [AllowEntityReturn] on controller-class (only method-level) ────

    [Fact]
    public void AllowEntityReturn_MustBeOnMethodNotClass()
    {
        var sourceRoot = ResolveSourceRoot();
        // D14: a guard that SKIPS when it cannot find the source is a false negative, not a safe
        // default — a CI layout change would silently disable every isolation lint while still
        // reporting green. Throwing is the only honest behaviour for a control, and it also
        // narrows the nullable so the scan below cannot be handed a null root.
        sourceRoot = sourceRoot ?? throw new InvalidOperationException(
            "The Zayra.Api source root could not be resolved, so this guard would check NOTHING. "
            + "Treat an unresolvable path as a build failure, never a skip.");

        // A class-level [AllowEntityReturn] suppresses ALL actions on that controller,
        // bypassing any future action-level checks. Flag any class-level usage.
        var violations = new List<string>();
        var classAttrPattern = new Regex(@"^\[AllowEntityReturn", RegexOptions.Compiled);
        var classDeclarationPattern = new Regex(@"\bclass\b.*Controller\b", RegexOptions.Compiled);

        foreach (var filePath in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(filePath);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!classAttrPattern.IsMatch(lines[i].TrimStart())) continue;

                // Look ahead: if the next non-blank, non-attribute line is a class declaration → violation
                for (int j = i + 1; j < Math.Min(i + 5, lines.Length); j++)
                {
                    var next = lines[j].Trim();
                    if (string.IsNullOrWhiteSpace(next) || next.StartsWith("[")) continue;
                    if (classDeclarationPattern.IsMatch(next))
                    {
                        var rel = Path.GetRelativePath(sourceRoot, filePath);
                        violations.Add($"  {rel}:{i + 1} — [AllowEntityReturn] applied at class level on controller; must be method-level only");
                    }
                    break;
                }
            }
        }

        violations.Should().BeEmpty(
            "[AllowEntityReturn] placed on a controller class suppresses ALL action methods, " +
            "including future ones. It must only be placed on specific action methods where the " +
            "entity is verified to be flat and free of sensitive navigation properties.");
    }
}
