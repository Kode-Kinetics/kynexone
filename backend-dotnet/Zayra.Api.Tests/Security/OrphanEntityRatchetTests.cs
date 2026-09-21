using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// ORPHAN-ENTITY RATCHET — every <c>DbSet&lt;T&gt;</c> must be referenced outside
/// <c>Models/</c>, <c>Data/</c> and <c>Migrations/</c>.
///
/// <para><b>The defect class.</b> This repository's single structural defect is configuration that
/// is stored and never read, and its root cause is that <i>the read is the only optional step</i>.
/// Building a feature means writing a model, a migration, a controller write path, an API client
/// and a form input. Every one of those is visible in a demo and survives a save-and-reload check.
/// The consumer — the line that reads the value and changes behaviour — comes last, is invisible,
/// and nothing in the build catches its absence. No test fails. No type error. The screen still
/// works. The failure surfaces a quarter later at a customer, as a control that was never there.
/// A table with a <c>DbSet</c>, an EF mapping, a migration and no code anywhere else is the purest
/// form of it: the whole apparatus of a feature, with the one load-bearing part missing.</para>
///
/// <para><b>Why a ratchet and not a clean sweep.</b> Twenty-nine entities are already in this
/// state. Deleting them all in one change would touch every module at once, and three or four of
/// them are a product decision rather than debt (<c>EmployeeDependent</c> drives medical insurance
/// and GOSI in the GCC; <c>PerformanceRatingScale</c>/<c>Option</c> are normally configurable).
/// So the surface is frozen instead: the twenty-nine are pinned below and <b>the count may only go
/// down</b>. A thirtieth fails the build.</para>
///
/// <para><b>THE RULE FOR NEW CODE:</b> do not add a <c>DbSet</c> before the code that reads it.
/// Sixteen new entities landed in one week without adding an orphan, so the rule is already being
/// followed — this test makes it expensive to stop. Removing an entity, or giving one a consumer,
/// should remove its line from <see cref="PinnedOrphans"/> in the same change.</para>
///
/// <para><b>Method.</b> Every <c>public DbSet&lt;T&gt;</c> declared in <c>Data/</c>, matched by
/// <b>substring</b> against a blob of every <c>.cs</c> file under <c>Zayra.Api</c> outside
/// <c>Models/</c>, <c>Data/</c> and <c>Migrations/</c>, with comments stripped first.</para>
///
/// <para>Substring, not a word boundary, is deliberate: an entity is normally reached through its
/// plural DbSet accessor (<c>db.AttendanceLockPeriods</c>), so a <c>\b</c>-anchored match would
/// report five live entities — <c>AttendanceLockPeriod</c>, <c>ComplianceRequirement</c>,
/// <c>EmployeeActionItem</c>, <c>HRRequestAttachment</c>, <c>NitaqatEmployeeWeightOverride</c> —
/// as dead.</para>
///
/// <para><b>And substring alone is not enough</b>, which this guard learned the hard way: an
/// entity ending in <i>y</i> pluralises to <i>ies</i>, so <c>RoleCompetency</c> does NOT appear
/// inside <c>db.RoleCompetencies</c>. A name-only scan would accuse any such entity that is
/// reached solely through its accessor — inventing an orphan, which is the one direction a guard
/// like this must never fail in. The <i>y → ies</i> form is therefore accepted as a reference too.
/// Re-measured with the rule in place, the orphan set is unchanged at 29 (<c>RoleCompetency</c> has
/// no <c>RoleCompetencies</c> reference either), so the rule costs nothing today and protects the
/// next <c>…y</c> entity somebody adds.</para>
/// </summary>
public class OrphanEntityRatchetTests
{
    /// <summary>
    /// The entities that today have a table and no code. <b>This list may only shrink.</b>
    ///
    /// <para>Measured at 29 when the register was written and 29 today, over a week in which 16 new
    /// entities landed — the number is a frozen inheritance, not a growing one.</para>
    /// </summary>
    private static readonly HashSet<string> PinnedOrphans = new(StringComparer.Ordinal)
    {
        // ── AI / analytics: modelled, migrated, never built ──────────────────────────────────
        "AIModelConfig",
        "AIRecommendation",
        "BurnoutRiskSignal",
        "CandidateAIScore",
        "EmployeeChurnPrediction",
        "EmployeeSentimentPulse",
        "ResumeParseResult",

        // ── Attendance: two of these are actively misleading ─────────────────────────────────
        "AttendanceDeviceConnector",
        // A second, richer model of a geofence (RadiusMeters, ClockInRequiredInside,
        // SpoofingRiskCheckEnabled) competing with Location.GeofenceRadiusMeters, which IS the
        // one written by the Setup screen. Keeping both guarantees the next engineer wires the
        // wrong one.
        "AttendanceGeofence",
        "AttendanceLocation",
        // AttendanceRule/OvertimeRule are generic RuleValueJson rule engines. There is no rule
        // engine.
        "AttendanceRule",
        "OvertimeRule",
        "OvertimeAdjustment",

        // ── Leave: the year-end engine's data model, with no engine ──────────────────────────
        // LeaveAccrualRule carries AccrualFrequency, AccrualDays, CarryForwardMaxDays,
        // CarryForwardExpiryDays and NegativeBalanceAllowed, and has its own index. There is no
        // accrual job, no carry-forward job and no expiry job — the only registered background
        // job type in the product is attendance.process. Do NOT delete this one without a product
        // decision: it is the only entry here with a future.
        "LeaveAccrualRule",
        "LeaveAttachment",
        "LeaveModificationRequest",
        "LeaveRequestDate",

        // ── Payroll / finance ────────────────────────────────────────────────────────────────
        "BankTransferFile",
        "PayrollAllowance",
        "PayrollCycle",
        "PayrollException",

        // ── Recruitment / performance: product decision needed before deletion ───────────────
        "CandidateDocument",
        "PerformanceRatingOption",
        "PerformanceRatingScale",
        "RoleCompetency",
        // Dependants drive medical insurance and GOSI across the GCC. Its absence is a functional
        // gap, not debt — get a product decision before removing it.
        "EmployeeDependent",

        // ── Retired by F1 ────────────────────────────────────────────────────────────────────
        // The frozen ApprovalPolicy model. The live approval path uses ApprovalWorkflowStep, a
        // different entity. ApprovalPoliciesController answers 410 on every verb and reads
        // nothing; the table is deliberately kept so the migrated data survives.
        "ApprovalPolicyStep",

        // ── Other ────────────────────────────────────────────────────────────────────────────
        "ESSDashboardPreference",

        // ── NOT DEBT — a blind spot in the scanner, and it must never be deleted ─────────────
        // DataProtectionKey is the ASP.NET Core data-protection entity. ZayraDbContext implements
        // IDataProtectionKeyContext and Program.cs calls PersistKeysToDbContext<ZayraDbContext>(),
        // so the framework reaches this DbSet through the interface and application code never
        // names the type. It is counted here only because this scan measures references by name.
        // Removing it would silently invalidate every issued auth cookie and antiforgery token.
        // It is pinned rather than allow-listed away so the number below reconciles, by name, with
        // the audit that produced it.
        "DataProtectionKey",
    };

    [Fact]
    public void EveryDbSetMustBeReferencedOutsideModelsDataAndMigrations()
    {
        var apiRoot = ResolveApiRoot();
        var declared = DeclaredEntities(apiRoot);
        var blob = ConsumerBlob(apiRoot);

        // Anti-vacuous-pass guards. An empty entity list or an empty blob would make every
        // assertion below pass trivially — exactly the failure mode that let one guard in this
        // suite pass on an empty list. A wrong source root must fail, never report green.
        declared.Should().HaveCountGreaterThan(200,
            "the DbSet declarations could not be read, so this guard would be inert.");
        blob.Length.Should().BeGreaterThan(1_000_000,
            "the consumer blob is empty or truncated, so every entity would look like an orphan.");

        var orphans = declared.Where(name => !IsReferenced(blob, name))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var unpinned = orphans.Where(name => !PinnedOrphans.Contains(name)).ToList();

        unpinned.Should().BeEmpty(
            "a DbSet with no reference outside Models/, Data/ and Migrations/ is a table, a migration "
            + "and an EF mapping with no code that reads it — the write half of a feature whose read "
            + "half was never written. Write the consumer, or do not add the DbSet.\n\n"
            + "  New orphan(s): " + string.Join(", ", unpinned));
    }

    [Fact]
    public void ThePinnedOrphanCountMayOnlyGoDown()
    {
        var apiRoot = ResolveApiRoot();
        var declared = DeclaredEntities(apiRoot);
        var blob = ConsumerBlob(apiRoot);

        declared.Should().HaveCountGreaterThan(200,
            "the DbSet declarations could not be read, so this guard would be inert.");

        var orphanCount = declared.Count(name => !IsReferenced(blob, name));

        orphanCount.Should().BeLessThanOrEqualTo(PinnedOrphans.Count,
            $"the orphan surface may only shrink. Pinned {PinnedOrphans.Count}, found {orphanCount}. "
            + "Giving an entity a consumer, or deleting it, should remove its line from PinnedOrphans "
            + "in the same change.");

        // The other direction: a stale pin is a guard that has quietly stopped guarding that name.
        var stale = PinnedOrphans.Where(name => IsReferenced(blob, name))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        stale.Should().BeEmpty(
            "these entities now HAVE a consumer and must be removed from PinnedOrphans so the count "
            + "records the real remaining debt: " + string.Join(", ", stale));
    }

    // ── Scanning ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the consumer blob mentions this entity. Accepts the type name, and — for a name
    /// ending in <c>y</c> — its <c>ies</c> plural, because that is how the DbSet accessor for such
    /// an entity is spelled and the type name is NOT a substring of it
    /// (<c>RoleCompetencies</c> does not contain <c>RoleCompetency</c>). Without this clause the
    /// scan invents orphans; the ordinary plural (<c>…Periods</c>) needs no clause because the
    /// singular is already a substring of it.
    /// </summary>
    internal static bool IsReferenced(string blob, string entityName)
        => blob.Contains(entityName, StringComparison.Ordinal)
           || (entityName.EndsWith('y')
               && blob.Contains(string.Concat(entityName.AsSpan(0, entityName.Length - 1), "ies"), StringComparison.Ordinal));

    private static readonly Regex DbSetDeclaration =
        new(@"public\s+(?:virtual\s+)?DbSet<\s*([A-Za-z0-9_.]+)\s*>", RegexOptions.Compiled);

    private static IReadOnlyCollection<string> DeclaredEntities(string apiRoot)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(apiRoot, "Data"), "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match m in DbSetDeclaration.Matches(File.ReadAllText(file)))
            {
                // A namespace-qualified declaration names the same entity; keep the simple name so
                // the substring search below matches how call sites write it.
                var name = m.Groups[1].Value;
                var dot = name.LastIndexOf('.');
                names.Add(dot >= 0 ? name[(dot + 1)..] : name);
            }
        }
        return names;
    }

    /// <summary>
    /// Every .cs file that could legitimately consume an entity, concatenated <b>with comments
    /// stripped</b>.
    ///
    /// <para>Stripping comments is not tidiness — it is the difference between a guard that works
    /// and one that can be silenced by writing prose. Naming an orphan in a comment (for instance,
    /// the refusal message in <c>LeavePoliciesController</c> explaining that
    /// <c>LeaveAccrualRule</c> is the year-end engine's data model) made this scan count it as
    /// consumed and quietly dropped it out of the pinned set. A mention is not a consumer. This
    /// guard exists precisely because writing something down is cheap and wiring it is not, so it
    /// must not be satisfiable by writing something down.</para>
    /// </summary>
    private static string ConsumerBlob(string apiRoot)
    {
        var sep = Path.DirectorySeparatorChar;
        var parts = new List<string>();
        foreach (var file in Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file);
            var probe = $"{sep}{relative}{sep}";
            if (probe.Contains($"{sep}Models{sep}", StringComparison.Ordinal)) continue;
            if (probe.Contains($"{sep}Data{sep}", StringComparison.Ordinal)) continue;
            if (probe.Contains($"{sep}Migrations{sep}", StringComparison.Ordinal)) continue;
            if (probe.Contains($"{sep}obj{sep}", StringComparison.Ordinal)) continue;
            if (probe.Contains($"{sep}bin{sep}", StringComparison.Ordinal)) continue;
            parts.Add(StripComments(File.ReadAllText(file)));
        }
        return string.Join("\n", parts);
    }

    /// <summary>
    /// Blanks <c>//</c> line comments (including <c>///</c> doc comments) and <c>/* */</c> blocks,
    /// leaving everything else byte-for-byte. String literals containing "//" — a URL, a path —
    /// are left alone, because truncating there would blank real code that follows on the line.
    /// The consequence is conservative in the safe direction: a name mentioned only inside such a
    /// literal still counts as a reference, so the scan can under-report an orphan and never
    /// invent one.
    /// </summary>
    internal static string StripComments(string source)
    {
        var output = new System.Text.StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];

            if (c == '"' || c == '\'')
            {
                // Copy the literal verbatim, honouring escapes so an escaped quote does not end it.
                var quote = c;
                output.Append(c);
                i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        output.Append(source[i]).Append(source[i + 1]);
                        i += 2;
                        continue;
                    }
                    output.Append(source[i]);
                    if (source[i] == quote) { i++; break; }
                    if (source[i] == '\n' && quote == '"') { i++; break; }   // unterminated: do not run away
                    i++;
                }
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i = Math.Min(i + 2, source.Length);
                continue;
            }

            output.Append(c);
            i++;
        }
        return output.ToString();
    }

    /// <summary>
    /// Self-test for the detection half. The ratchet is at its pinned count today, so nothing in
    /// the repository can demonstrate that it would actually catch a thirtieth orphan — a scan that
    /// returned "no orphans" for any input would sit green for ever. This drives the same substring
    /// rule over a synthetic model and proves both directions: an unreferenced entity is detected,
    /// and one reached only through its plural DbSet accessor is not falsely accused.
    /// </summary>
    [Fact]
    public void TheDetectionRuleCatchesAnOrphanAndDoesNotInventOne()
    {
        var declared = new[] { "LiveEntity", "AttendanceLockPeriod", "RoleCompetency", "BrandNewOrphan" };
        var blob = StripComments("""
            // BrandNewOrphan is planned for next quarter.
            var x = db.LiveEntity.Where(e => e.Id == id);
            var y = db.AttendanceLockPeriods.ToList();
            var z = db.RoleCompetencies.ToList();
            """);

        var orphans = declared.Where(n => !IsReferenced(blob, n)).ToList();

        orphans.Should().BeEquivalentTo(new[] { "BrandNewOrphan" },
            "an entity named only in a comment is an orphan; one reached through a plain plural "
            + "accessor (AttendanceLockPeriods) is not, which is why this counts substrings; and "
            + "one reached through an -ies plural (RoleCompetencies) is not either, which is why "
            + "the y-to-ies form counts as a reference. Getting that last case wrong would INVENT "
            + "an orphan, the one direction this guard must never fail in");
    }

    /// <summary>
    /// Self-test for the scanner. Without it, a StripComments that returned the empty string would
    /// make every entity look like an orphan and a StripComments that returned its input unchanged
    /// would make a comment count as a consumer — and in both cases the ratchet above could still
    /// report green after a pin update.
    /// </summary>
    [Fact]
    public void TheCommentStripperRemovesCommentsAndKeepsCode()
    {
        const string source = """
            // MysteryEntity is the year-end engine's data model.
            /// <summary>See MysteryEntity.</summary>
            /* MysteryEntity again */
            public class Thing { public string Url = "https://example.com/MysteryEntity"; public RealEntity R; }
            """;

        var stripped = StripComments(source);

        stripped.Should().NotContain("year-end engine", "line and doc comments must go");
        stripped.Should().NotContain("again", "block comments must go");
        stripped.Should().Contain("RealEntity", "code must survive untouched");
        stripped.Should().Contain("https://example.com/MysteryEntity",
            "a // inside a string literal is not a comment, and truncating there would blank real code");
        stripped.Should().Contain("public class Thing");
    }

    private static string ResolveApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) break;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }

        // A guard that SKIPS when it cannot find the source is a false negative, not a safe
        // default: a CI layout change would silently disable it while still reporting green.
        throw new InvalidOperationException(
            "The Zayra.Api source root could not be resolved, so this guard would check NOTHING. "
            + "Treat an unresolvable path as a build failure, never a skip.");
    }
}
