using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Seed;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Every permission key the API REQUIRES must exist in the permission catalog.
///
/// <para>WHY. A permission is only grantable if it is a row in <c>permissions</c>: role bundles are built
/// from the catalog (<see cref="AuthSeeder"/>), the Admin backfill cross-joins the catalog, and a per-user
/// Allow override is refused for a key that is not in it (AccessManagementService). So an endpoint gated
/// on a key that is NOT in the catalog can never be reached by anyone — it answers 403 to every role,
/// including Admin, forever. <c>GosiController.DeactivateContributionRule</c> required
/// <c>payroll.manage</c>, which has never been seeded, so no tenant could ever deactivate a GOSI rate
/// override it had created. Nothing failed: a 403 looks exactly like correct authorization.</para>
///
/// <para>HOW. Permission requirements are declared three ways in this codebase, and the scanner reads all
/// three from the compiled assembly rather than from the source, so a constant, a helper wrapper or an
/// async state machine cannot hide a key:</para>
/// <list type="number">
/// <item><c>[HasPermission("a", "b")]</c> on a controller or action (and its synthesized
/// <c>perm:</c> policy) — read by reflection.</item>
/// <item>Imperative <c>HasPermission("x")</c> / <c>HasAnyPermission("x", "y")</c> calls, whether the
/// controller's private helper or the <see cref="ClaimsPrincipalPermissionExtensions"/> extension — read
/// from IL: every string loaded since the previous call instruction is an argument of that call, which
/// covers <c>const</c> keys (the compiler inlines them as <c>ldstr</c>) and <c>params</c> arrays.</item>
/// <item>Raw claim comparisons (<c>c.Type == "permission" &amp;&amp; c.Value == "x"</c>) inside a controller —
/// read from IL: in a controller method that loads the <c>"permission"</c> claim type, every other
/// key-shaped literal is a required permission.</item>
/// </list>
///
/// <para>Keys resolved from DATA at runtime (report or job descriptors) are out of reach of a static
/// scan; this guard covers what the code states literally.</para>
/// </summary>
public class PermissionCatalogCoverageTests
{
    private static readonly Assembly ApiAssembly = typeof(HasPermissionAttribute).Assembly;

    /// <summary>
    /// Known dead permissions on <c>main</c> — referenced by an endpoint, absent from the catalog, so the
    /// endpoint 403s for every caller. Each entry is a DEBT, not an exemption: the stale-entry test below
    /// fails the moment an entry is fixed (added to the catalog, or no longer referenced), so this list can
    /// only shrink. Do NOT add a new key here to get a build green — fix the key or seed the permission.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownDeadPermissions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Fixed in PR #136 (fix/settlement-maker-checker-and-dashboard-scope), not merged yet.
            // REMOVE this entry when #136 lands — KnownDeadPermissionAllowlist_HasNoStaleEntries will fail until you do.
            ["payroll.review"] = "PayrollController final-settlement review/approve gate — PR #136",

            // The ten keys found when this guard was introduced (appraisal.*, sensitive_data.view,
            // ai.policy.ask, policy.documents.read, finance.gl.export, finance.erp.confirm) were fixed by
            // fix/dead-permission-checks: remapped to existing keys, except finance.erp.confirm, now seeded.
        };

    [Fact]
    public async Task EveryPermissionTheApiRequires_ExistsInThePermissionCatalog()
    {
        var catalog = await LoadCatalogAsync();
        var referenced = ScanRequiredPermissions();

        var dead = referenced
            .Where(kv => !catalog.Contains(kv.Key) && !KnownDeadPermissions.ContainsKey(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}  ←  {string.Join(", ", kv.Value.OrderBy(x => x, StringComparer.Ordinal))}")
            .ToList();

        dead.Should().BeEmpty(
            "an endpoint gated on a permission that is not in the catalog 403s for EVERY caller — no role bundle, " +
            "Admin backfill or per-user override can grant a key the catalog does not hold. Either gate the endpoint " +
            "on an existing permission or add the key to AuthSeeder.EnsurePermissions (and to the role bundles that " +
            "should reach it). Offenders:\n  " + string.Join("\n  ", dead));
    }

    [Fact]
    public async Task KnownDeadPermissionAllowlist_HasNoStaleEntries()
    {
        var catalog = await LoadCatalogAsync();
        var referenced = ScanRequiredPermissions();

        var stale = KnownDeadPermissions.Keys
            .Where(k => catalog.Contains(k) || !referenced.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        stale.Should().BeEmpty(
            "an allowlisted dead permission that is now in the catalog, or no longer referenced, has been FIXED — " +
            "delete its KnownDeadPermissions entry so the guard covers it again");
    }

    [Fact]
    public void Scanner_SeesEveryDeclarationShape()
    {
        // A guard that has never been seen to find anything is not a guard. One known site per shape.
        var referenced = ScanRequiredPermissions();

        // 1. [HasPermission] attribute on an action.
        referenced.Should().ContainKey("employees.approve");
        referenced["employees.approve"].Should().Contain(s => s.StartsWith("EmployeesController.ApproveChange", StringComparison.Ordinal));

        // 2. Imperative private-helper call inside an async action (state machine), incl. the GOSI rule write.
        referenced.Should().ContainKey("payroll.rates.statutory_override");
        referenced["payroll.rates.statutory_override"].Should()
            .Contain(s => s.StartsWith("GosiController.CreateContributionRule", StringComparison.Ordinal))
            .And.Contain(s => s.StartsWith("GosiController.DeactivateContributionRule", StringComparison.Ordinal),
                "deactivating a GOSI rate override is the inverse of creating one and takes the same permission");

        // 2b. A const key passed to the helper is inlined by the compiler and still seen.
        referenced.Should().ContainKey(Zayra.Api.Controllers.FinanceGlController.PredicateAuthorPermission);

        // 3. Raw claim comparison in a controller lambda.
        referenced.Should().ContainKey("approvals.decide");
        referenced["approvals.decide"].Should().Contain(s => s.StartsWith("AttendanceController", StringComparison.Ordinal));

        // The retired key is gone from the GOSI controller.
        referenced.Should().NotContainKey("payroll.manage");
    }

    // ── Catalog ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The catalog exactly as production builds it: AuthSeeder's own upsert, not a copy of it.</summary>
    private static async Task<HashSet<string>> LoadCatalogAsync()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase("perm-catalog-" + Guid.NewGuid().ToString("N")).Options;
        await using var db = new ZayraDbContext(options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = tenantId, Name = "Catalog", Slug = $"cat-{Guid.NewGuid():N}"[..20] });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId, CancellationToken.None);
        var keys = await db.Permissions.Select(p => p.Key).ToListAsync();
        keys.Should().NotBeEmpty("the scanner compares against the seeded catalog; an empty catalog would flag everything");
        return keys.ToHashSet(StringComparer.OrdinalIgnoreCase); // the claim check is case-insensitive
    }

    // ── Scanner ──────────────────────────────────────────────────────────────────────────────────

    private static readonly Regex PermissionKeyShape = new(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$", RegexOptions.Compiled);

    private static readonly HashSet<string> CheckMethodNames = new(StringComparer.Ordinal)
    {
        "HasPermission", "HasAnyPermission",
    };

    /// <summary>permission key → the sites that require it ("Controller.Action").</summary>
    internal static Dictionary<string, HashSet<string>> ScanRequiredPermissions()
    {
        var found = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Add(string key, string site)
        {
            if (!PermissionKeyShape.IsMatch(key)) return;
            if (!found.TryGetValue(key, out var sites)) found[key] = sites = new HashSet<string>(StringComparer.Ordinal);
            sites.Add(site);
        }

        foreach (var type in SafeTypes(ApiAssembly))
        {
            var outer = Outermost(type);
            var isControllerFamily = typeof(ControllerBase).IsAssignableFrom(outer) && !outer.IsAbstract;

            // 1. Attributes — controller-level and action-level.
            if (type == outer && isControllerFamily)
            {
                foreach (var key in DeclaredPermissions(type)) Add(key, type.Name);
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    foreach (var key in DeclaredPermissions(m)) Add(key, $"{type.Name}.{m.Name}");
            }

            // 2 + 3. IL of every body declared on this type (async state machines and closures are nested
            // types, so they are visited on their own and attributed to their outermost declaring type).
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                   | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var bodies = type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all));
            foreach (var method in bodies)
            {
                byte[]? il;
                try { il = method.GetMethodBody()?.GetILAsByteArray(); }
                catch { continue; }
                if (il is null || il.Length == 0) continue;

                var site = $"{outer.Name}.{FriendlyName(type, method)}";
                var sinceLastCall = new List<string>();
                var allLiterals = new List<string>();
                var loadsPermissionClaimType = false;

                foreach (var (op, operand) in Decode(il))
                {
                    if (op == OpCodes.Ldstr)
                    {
                        string s;
                        try { s = method.Module.ResolveString(operand); } catch { continue; }
                        sinceLastCall.Add(s);
                        allLiterals.Add(s);
                        if (s == ClaimsPrincipalPermissionExtensions.PermissionClaimType) loadsPermissionClaimType = true;
                        continue;
                    }
                    if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
                    {
                        var calleeName = ResolveMethodName(method, operand);
                        if (calleeName is not null && CheckMethodNames.Contains(calleeName))
                            foreach (var s in sinceLastCall) Add(s, site);
                        sinceLastCall.Clear();
                    }
                }

                if (isControllerFamily && loadsPermissionClaimType)
                    foreach (var s in allLiterals) Add(s, site);
            }
        }
        return found;
    }

    private static IEnumerable<string> DeclaredPermissions(MemberInfo member)
    {
        foreach (var attr in member.GetCustomAttributes<HasPermissionAttribute>(inherit: false))
            foreach (var p in attr.Permissions) yield return p;
        foreach (var attr in member.GetCustomAttributes<AuthorizeAttribute>(inherit: false))
        {
            if (attr is HasPermissionAttribute) continue;
            if (attr.Policy is { } policy && policy.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
                foreach (var p in policy[HasPermissionAttribute.PolicyPrefix.Length..].Split(HasPermissionAttribute.Separator,
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    yield return p;
        }
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    private static Type Outermost(Type t)
    {
        while (t.DeclaringType is not null) t = t.DeclaringType;
        return t;
    }

    /// <summary>"ApproveChange" for the action itself or its async state machine (<c>&lt;ApproveChange&gt;d__12</c>).</summary>
    private static string FriendlyName(Type declaring, MethodBase method)
    {
        for (var t = declaring; t is not null; t = t.DeclaringType)
        {
            var m = Regex.Match(t.Name, @"^<(?<name>[^>]+)>");
            if (m.Success) return m.Groups["name"].Value;
        }
        var own = Regex.Match(method.Name, @"^<(?<name>[^>]+)>");
        return own.Success ? own.Groups["name"].Value : method.Name;
    }

    private static string? ResolveMethodName(MethodBase context, int token)
    {
        try
        {
            var genericTypeArgs = context.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
            var genericMethodArgs = context.IsGenericMethod ? context.GetGenericArguments() : null;
            return context.Module.ResolveMethod(token, genericTypeArgs, genericMethodArgs)?.Name;
        }
        catch
        {
            return null;
        }
    }

    // ── A minimal IL decoder: opcode + 32-bit token operand where there is one ─────────────────────

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private static IEnumerable<(OpCode Op, int Operand)> Decode(byte[] il)
    {
        var i = 0;
        while (i < il.Length)
        {
            short value = il[i++];
            if (value == 0xFE && i < il.Length) value = unchecked((short)(0xFE00 | il[i++]));
            if (!OpCodesByValue.TryGetValue(value, out var op)) yield break; // not IL we understand — stop, never misread

            var operand = 0;
            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    i += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    i += 8;
                    break;
                case OperandType.InlineSwitch:
                    var count = BitConverter.ToInt32(il, i);
                    i += 4 + 4 * count;
                    break;
                default: // InlineBrTarget, InlineField, InlineI, InlineMethod, InlineSig, InlineString, InlineTok, InlineType, ShortInlineR
                    operand = BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
            }
            yield return (op, operand);
        }
    }
}
