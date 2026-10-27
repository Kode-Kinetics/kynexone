using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Bulk-write RATCHET. <c>ExecuteUpdateAsync</c> / <c>ExecuteDeleteAsync</c> go straight to SQL: they bypass change
/// tracking, so the central company write guard in <c>ZayraDbContext.SaveChanges</c> never sees them. On a company-owned
/// entity the controller's own WHERE clause is then the whole company boundary, and a forgotten predicate rewrites or
/// deletes another legal entity's rows. New controller code loads the rows (the read filter applies) and saves, which
/// the guard checks. Where a bulk write is genuinely needed, list it below with the reason the predicate is enough.
/// Counts may only go DOWN without discussion.
/// </summary>
public class BulkWriteCompanyScopeRatchetTests
{
    // "file|Entity" -> approved count. Each of these runs inside an action that has already loaded the run, batch or
    // employee through a company-filtered query and then narrows the bulk statement by that row's own id and TenantId.
    private static readonly Dictionary<string, int> Approved = new()
    {
        ["Controllers/Finance/BonusesController.cs|EmployeeBonus"] = 1,
        ["Controllers/PayrollController.cs|EmployeeBonus"] = 1,
        ["Controllers/PayrollController.cs|PayrollDeduction"] = 1,
        // Moved with the payroll pass (queued job). Both go through the company-scoped read view (Op), the tenant and
        // this run's own consumed ids, the same bound they had in the controller.
        ["Infrastructure/Payroll/PayrollRunProcessor.cs|EmployeeBonus"] = 1,
        ["Infrastructure/Payroll/PayrollRunProcessor.cs|LeaveEncashmentRequest"] = 1,
        ["Controllers/PayrollController.cs|PayrollRunEmployeeSelection"] = 1,
        ["Controllers/PayrollController.cs|PayrollSlip"] = 2,
        ["Controllers/PayrollController.cs|PayrollValidationOverride"] = 1,
        ["Controllers/PayrollController.cs|Payslip"] = 2,
        ["Controllers/PayrollController.cs|WPSFileBatch"] = 2,
    };

    private static readonly Regex BulkCall = new(@"Execute(?:Update|Delete)Async", RegexOptions.Compiled);
    private static readonly Regex DbSetRef = new(@"\b_?(?:db|context)\.(\w+)", RegexOptions.Compiled);

    private static string ApiRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(Path.Combine(candidate, "Controllers"))) return candidate;
        }
        throw new DirectoryNotFoundException("Zayra.Api/Controllers not found above the test binaries.");
    }

    private static Dictionary<string, Type> CompanyOwnedSets() =>
        typeof(ZayraDbContext).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => (p.Name, Entity: p.PropertyType.GetGenericArguments()[0]))
            .Where(x => typeof(ICompanyScoped).IsAssignableFrom(x.Entity))
            .ToDictionary(x => x.Name, x => x.Entity);

    private static readonly string[] MovedControllerCode = ["Infrastructure/Payroll/PayrollRunProcessor.cs"];

    private static Dictionary<string, int> Scan()
    {
        var root = ApiRoot();
        var sets = CompanyOwnedSets();
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        // The payroll pass moved out of PayrollController into a service the queued job calls; its bulk writes are
        // still request-driven and stay under this ratchet at their new path.
        var files = Directory.EnumerateFiles(Path.Combine(root, "Controllers"), "*.cs", SearchOption.AllDirectories)
            .Concat(MovedControllerCode.Select(f => Path.Combine(root, f)));
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (Match call in BulkCall.Matches(text))
            {
                var before = text[Math.Max(0, call.Index - 900)..call.Index];
                before = before[(before.LastIndexOf(';') + 1)..];
                var refs = DbSetRef.Matches(before);
                if (refs.Count == 0) continue;
                if (!sets.TryGetValue(refs[^1].Groups[1].Value, out var entity)) continue;
                var key = $"{rel}|{entity.Name}";
                found[key] = found.GetValueOrDefault(key) + 1;
            }
        }
        return found;
    }

    [Fact]
    public void NoNewBulkWriteMayTargetACompanyOwnedEntityInAController()
    {
        var offenders = Scan()
            .Where(kv => !Approved.TryGetValue(kv.Key, out var ok) || kv.Value > ok)
            .Select(kv => $"{kv.Key}: {kv.Value} bulk write(s), approved {(Approved.TryGetValue(kv.Key, out var ok) ? ok : 0)}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        offenders.Should().BeEmpty(
            "ExecuteUpdate/ExecuteDelete skip the company write guard. Load the rows through the filtered query and save " +
            "them instead, or add the site to Approved with the reason its WHERE clause is the whole boundary.\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void ApprovedBulkWritesAreNotInflated()
    {
        var actual = Scan();
        var stale = Approved
            .Where(kv => actual.GetValueOrDefault(kv.Key) < kv.Value)
            .Select(kv => $"{kv.Key}: approved {kv.Value}, actually {actual.GetValueOrDefault(kv.Key)}")
            .ToList();
        stale.Should().BeEmpty("the approved list may only go down.\n  " + string.Join("\n  ", stale));
    }
}
