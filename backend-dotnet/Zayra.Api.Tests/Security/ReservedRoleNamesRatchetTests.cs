using System.Reflection;
using System.Text.RegularExpressions;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Reports;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// RATCHET: every role NAME the product grants authority to by itself must be reserved
/// (<see cref="PrivilegeCeiling.ReservedRoleNames"/>), so nobody but an Admin can create, rename to, assign or
/// edit a role carrying it.
///
/// <para>Sources scanned: <c>IsInRole("…")</c> literals; every <c>[Authorize(Roles = …)]</c> on a controller or action,
/// read by reflection so constants (<c>Roles = HrRoles</c>) are resolved; role pattern checks in source
/// (<c>role is "X" or "Y"</c>, e.g. JawazatConstants.IsHrRole and the AI governance checks); OfferRules'
/// approver role names; and the report data domains' role lists. A name used in any of them but not reserved fails the build. <c>IsInRole(expr)</c> with a
/// non-literal argument is allowed only at the sites listed below, each of which reads an approval step's
/// ApproverRole; those names are reserved at run time per tenant
/// (<see cref="PrivilegeCeilingGraph.LoadApproverRouteNamesAsync"/>). Replacing name checks with permissions is
/// backlog; until then this list is the control.</para>
/// </summary>
public sealed class ReservedRoleNamesRatchetTests
{
    /// <summary>The only non-literal IsInRole sites, and why each is covered.</summary>
    private static readonly Dictionary<string, string> DynamicIsInRoleSites = new(StringComparer.Ordinal)
    {
        // requiredRole is "HR Manager" or a LoanApproval.ApproverRole, which LoansController only ever writes as
        // "HR Manager"/"HR Director" (both reserved); pending loan steps are also reserved at run time.
        ["Controllers/Finance/LoansController.cs|requiredRole"] = "loan approval step role",
        // requiredRole is a pending LeaveApproval.ApproverRole: reserved at run time (pending leave steps
        // and approval workflow steps).
        ["Controllers/Leave/LeaveRequestsController.cs|requiredRole"] = "leave approval step role",
    };

    private static readonly Regex LiteralIsInRole = new(@"IsInRole\(\s*""([^""]+)""\s*\)", RegexOptions.Compiled);
    private static readonly Regex DynamicIsInRole = new(@"IsInRole\(\s*([^""\s)][^)]*)\)", RegexOptions.Compiled);
    // "<something>Role(s)… is "X" or "Y"" and the "r => r is "X"" lambda over a role list.
    private static readonly Regex RolePattern = new(
        @"(?:\b\w*[Rr]ole\w*\)?|[Rr]oles\w*\??\.Any\(\s*(?<v>\w+)\s*=>\s*\k<v>)\s+is\s+(?<names>(?:""[^""]+""(?:\s+or\s+)?)+)",
        RegexOptions.Compiled);
    private static readonly Regex Quoted = new(@"""([^""]+)""", RegexOptions.Compiled);

    [Fact]
    public void EveryRoleNameTheCodeGrantsAuthorityTo_IsReserved()
    {
        var root = SourceRoot();
        Assert.True(root is not null, "Zayra.Api source not found next to the test binaries: the ratchet cannot run, so it fails.");

        var used = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (file, line) in CodeLines(root!))
        {
            foreach (Match m in LiteralIsInRole.Matches(line)) used.TryAdd(AuthService.Normalize(m.Groups[1].Value), file);
            foreach (Match m in RolePattern.Matches(line))
                foreach (Match q in Quoted.Matches(m.Groups["names"].Value)) used.TryAdd(AuthService.Normalize(q.Groups[1].Value), file);
        }
        // [Authorize(Roles = …)] by reflection: constants are already resolved in the attribute.
        var api = typeof(Zayra.Api.Controllers.AccessController).Assembly;
        foreach (var type in api.GetTypes().Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t)))
        {
            var members = new MemberInfo[] { type }.Concat(type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));
            foreach (var attribute in members.SelectMany(m => m.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(inherit: true)))
                foreach (var name in (attribute.Roles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    used.TryAdd(AuthService.Normalize(name), $"[Authorize] on {type.Name}");
        }
        foreach (var name in Zayra.Api.Infrastructure.Recruitment.OfferRules.ApproverRoleNames)
            used.TryAdd(AuthService.Normalize(name), "OfferRules.ApproverRoleNames");
        foreach (var domain in typeof(DataDomains).GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.FieldType == typeof(DataDomain)).Select(f => (DataDomain)f.GetValue(null)!))
            foreach (var name in domain.Roles) used.TryAdd(AuthService.Normalize(name), "ReportAccessPolicy.DataDomains");

        Assert.NotEmpty(used);
        var unreserved = used.Where(kv => !PrivilegeCeiling.ReservedRoleNames.Contains(kv.Key))
            .Select(kv => $"'{kv.Key}' (first seen in {kv.Value})").ToList();
        Assert.True(unreserved.Count == 0,
            "These role names are checked by name in code but are not in PrivilegeCeiling.ReservedRoleNames, so a "
            + "non-Admin could create a role with the name and pass the check: " + string.Join("; ", unreserved));
    }

    [Fact]
    public void NonLiteralIsInRole_OnlyAtTheKnownApprovalStepSites()
    {
        var root = SourceRoot();
        Assert.True(root is not null, "Zayra.Api source not found next to the test binaries: the ratchet cannot run, so it fails.");

        var sites = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (file, line) in CodeLines(root!))
            foreach (Match m in DynamicIsInRole.Matches(line))
                sites.Add($"{file}|{m.Groups[1].Value.Trim()}");

        var unknown = sites.Where(s => !DynamicIsInRoleSites.ContainsKey(s)).ToList();
        Assert.True(unknown.Count == 0,
            "IsInRole with a computed role name grants authority to whatever role carries that name. Reserve the "
            + "source of the name at run time (PrivilegeCeilingGraph.LoadApproverRouteNamesAsync) and list the site "
            + "here, or check a permission instead: " + string.Join("; ", unknown));
        var gone = DynamicIsInRoleSites.Keys.Where(k => !sites.Contains(k)).ToList();
        Assert.True(gone.Count == 0, "Listed dynamic IsInRole sites no longer exist; remove them: " + string.Join("; ", gone));
    }

    [Fact]
    public void AReservedName_IsAnAdminsToCreateOrRenameTo_AndACustomRoleCarryingOne_IsAdminOnly()
    {
        var consoleAdmin = PrivilegeCeiling.ForCaller(Guid.NewGuid(), false, ["security.manage"], []);
        var admin = PrivilegeCeiling.ForCaller(Guid.NewGuid(), true, ["security.manage"], []);
        foreach (var name in PrivilegeCeiling.ReservedRoleNames)
        {
            Assert.Equal(PrivilegeCeiling.Codes.ReservedRoleName, PrivilegeCeiling.NameRefusal(consoleAdmin, name.ToLowerInvariant())?.Code);
            Assert.Null(PrivilegeCeiling.NameRefusal(admin, name));
            // A custom role an Admin gave this name grants the name's authority: Admin-only to assign.
            Assert.True(PrivilegeCeiling.IsAdminOnlyRole(new PrivilegeCeiling.RoleFacts(
                Guid.NewGuid(), name, name, Guid.NewGuid(), IsSystem: false, IsEditable: true, Array.Empty<string>())), name);
        }
        // A seeded role with a reserved name is assigned through the ordinary ceiling (unless protected).
        Assert.False(PrivilegeCeiling.IsAdminOnlyRole(new PrivilegeCeiling.RoleFacts(
            Guid.NewGuid(), "Employee", "EMPLOYEE", Guid.NewGuid(), IsSystem: true, IsEditable: true, Array.Empty<string>())));
    }

    private static IEnumerable<(string File, string Line)> CodeLines(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f)
                .Select(l => l.Trim())
                .Where(l => !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith("*", StringComparison.Ordinal))
                .Select(l => (Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'), l)));

    private static string? SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir?.Parent is not null; i++)
        {
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
