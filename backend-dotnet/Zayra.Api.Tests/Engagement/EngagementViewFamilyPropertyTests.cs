using System.Diagnostics;
using FluentAssertions;
using Zayra.Api.Infrastructure.Privacy;

namespace Zayra.Api.Tests.Engagement;

/// <summary>
/// Adversarial tests of <see cref="CohortViewFamily"/>. The oracle is the <see cref="LinearSpanAudit"/>: over the
/// atomic cells of all dimensions (the scope dimensions company and team included), no group of 1..k−1 people may be a
/// linear combination of the visible views — nor of the visible views plus every scoped view the readers can obtain.
/// It runs on the golden data, the privacy reviews' scenarios and 400 randomised populations (k = 3 and 5; populations
/// with and without several companies and teams). Comment lists (set mode) are audited by Boolean-algebra atoms.
/// </summary>
public sealed class EngagementViewFamilyPropertyTests
{
    private static readonly string[] Dims = ["company", "department", "designation", "grade", "tenure", "team"];

    private static CohortRecord R(string company, string dept, string desig, string tenure = "1to3y", string? team = null) => new(new Dictionary<string, string?>
    {
        ["company"] = company, ["department"] = dept, ["designation"] = desig, ["grade"] = "b1", ["tenure"] = tenure,
        ["team"] = team ?? (dept == "Ops" ? "m1" : "m2"),
    });

    private static CohortFilter F(string dimension, string value) => new(dimension, [value]);

    private static CohortScope Scope(string dimension, params string[] values) => CohortScope.From([new CohortFilter(dimension, values)])!;

    /// <summary>Every scoped view a reader with one of <paramref name="scopes"/> can request (no filter, one filter, a
    /// one-filter breakdown), as member sets — the extra rows of the audit.</summary>
    private static List<IReadOnlyCollection<int>> ScopedRows(CohortViewFamily family, IEnumerable<CohortScope> scopes)
    {
        var rows = new List<IReadOnlyCollection<int>>();
        var records = family.Records;
        foreach (var scope in scopes)
        {
            var requests = new List<List<CohortFilter>> { new() };
            foreach (var d in family.Dimensions)
                foreach (var v in records.Select(r => r.ValueOf(d)).Distinct())
                    requests.Add([F(d, v)]);
            foreach (var filters in requests)
            {
                CohortScope? resolved;
                try { resolved = family.Resolve(scope, filters); }
                catch (CohortViewNotPermittedException) { continue; }
                if (!family.Evaluate(resolved, filters).IsVisible) continue;
                rows.Add(Enumerable.Range(0, records.Count)
                    .Where(i => (resolved is null || resolved.Matches(records[i])) && filters.All(f => f.Matches(records[i]))).ToList());
            }
        }
        return rows;
    }

    private static IEnumerable<CohortScope> AllSingleValueScopes(CohortViewFamily family) =>
        new[] { "company", "team" }.SelectMany(d => family.Records.Select(r => r.ValueOf(d)).Distinct().Select(v => Scope(d, v)));

    private static bool Safe(int n, int k) => n == 0 || n >= k;

    /// <summary>R1–R5 as properties of the visible views (R2–R4 are consequences of R6 in the engine).</summary>
    private static void AssertRules(CohortViewFamily family, string label)
    {
        var k = family.K;
        var visible = family.Views().Where(v => v.Visible).Select(v => (v.Filters, Set: v.Members.ToHashSet())).ToList();
        string Key(IReadOnlyList<(string Dim, string Value)> f) => string.Join("&", f.Select(p => $"{p.Dim}={p.Value}"));
        var total = family.Records.Count;
        foreach (var (filters, set) in visible)
        {
            set.Count.Should().BeGreaterThanOrEqualTo(k, $"{label}: R1 '{Key(filters)}'");
            if (filters.Count == 2)
            {
                // R3: both parents visible. R5: the four quadrants.
                var parents = filters.Select(drop => visible.FirstOrDefault(v => v.Filters.Count == 1 && v.Filters[0] == filters.Single(f => f != drop))).ToList();
                parents.Should().OnlyContain(p => p.Set != null, $"{label}: R3 '{Key(filters)}'");
                var nx = parents[0].Set.Count; var ny = parents[1].Set.Count; var n = set.Count;
                (Safe(nx - n, k) && Safe(ny - n, k) && Safe(total - nx - ny + n, k)).Should().BeTrue($"{label}: R5 '{Key(filters)}'");
            }
        }
        foreach (var (fa, a) in visible)
            foreach (var (fb, b) in visible)
                if (b.IsSubsetOf(a)) Safe(a.Count - b.Count, k).Should().BeTrue($"{label}: R2 '{Key(fa)}' − '{Key(fb)}'");
        foreach (var (pf, p) in visible.Where(v => v.Filters.Count < family.MaxFiltersPermitted))
            foreach (var d in family.Dimensions.Where(d => pf.All(f => f.Dim != d)))
            {
                var covered = visible.Where(c => c.Filters.Count == pf.Count + 1 && c.Filters.Any(f => f.Dim == d) && pf.All(c.Filters.Contains))
                    .Sum(c => c.Set.Count);
                Safe(p.Count - covered, k).Should().BeTrue($"{label}: R4 '{Key(pf)}' by {d}");
            }
    }

    private static void AssertNoLeak(CohortViewFamily family, string label)
    {
        AssertRules(family, label);
        LinearSpanAudit.FindLeak(family).Should().BeNull($"{label}: visible views");
        LinearSpanAudit.FindLeak(family, ScopedRows(family, AllSingleValueScopes(family))).Should().BeNull($"{label}: with every reader's scoped views");
    }

    [Fact]
    public void TheAuditItself_FindsTheReviewersInclusionExclusion()
    {
        // Without R5 the four views below were visible; the audit must say so (a check that the oracle is not vacuous).
        var records = Enumerable.Repeat(R("c1", "X", "Y"), 10).Concat(Enumerable.Repeat(R("c1", "X", "Z"), 10))
            .Concat(Enumerable.Repeat(R("c1", "W", "Y"), 10)).Append(R("c1", "W", "Z")).ToList();
        var x = Enumerable.Range(0, 20).ToList();
        var y = Enumerable.Range(0, 10).Concat(Enumerable.Range(20, 10)).ToList();
        var xy = Enumerable.Range(0, 10).ToList();
        var root = Enumerable.Range(0, 31).ToList();
        LinearSpanAudit.FindLeak(records, Dims, 5, [root, x, y, xy]).Should().Contain("a group of 1");
        LinearSpanAudit.FindLeak(records, Dims, 5, [root, x, y]).Should().BeNull("margins alone do not isolate W∩Z");
    }

    [Fact]
    public void TheGoldenPopulation_PassesTheAudit()
    {
        var records = Golden.Groups.Select(g => R("A1", g.Dept, g.Desig)).ToList();
        var family = CohortViewFamily.Build(new CohortPopulation(records), Dims, 5);
        AssertNoLeak(family, "golden");
        family.Evaluate(null, [F("department", "Ops")]).IsVisible.Should().BeTrue("the audit must not pass by hiding everything");
    }

    [Fact]
    public void ReviewAttackA_InclusionExclusionWithinOneFamily_IsClosed()
    {
        // X∩Y = 10, X∩Z = 10, W∩Y = 10, W∩Z = 1: root − X − Y + X∩Y = W∩Z.
        var records = Enumerable.Repeat(R("c1", "X", "Y"), 10).Concat(Enumerable.Repeat(R("c1", "X", "Z"), 10))
            .Concat(Enumerable.Repeat(R("c1", "W", "Y"), 10)).Append(R("c1", "W", "Z")).ToList();
        var family = CohortViewFamily.Build(new CohortPopulation(records), Dims, 5);
        var views = new[]
        {
            family.Evaluate(null, []), family.Evaluate(null, [F("department", "X")]), family.Evaluate(null, [F("designation", "Y")]),
            family.Evaluate(null, [F("department", "X"), F("designation", "Y")]),
        };
        views.All(v => v.IsVisible).Should().BeFalse("root − X − Y + X∩Y would give the one W∩Z person");
        family.Evaluate(null, [F("department", "X"), F("designation", "Y")]).Reason.Should().Be(SuppressionReasons.QuadrantTooSmall);
        AssertNoLeak(family, "attack A");
    }

    [Fact]
    public void ReviewAttackB_ScopeSwitch_IsClosed()
    {
        // c1: Ops 10 + Fin 10; c2: Ops 1 + Fin 9. Group "Ops" minus company-c1 "Ops" = the one c2 Ops person.
        var records = Enumerable.Repeat(R("c1", "Ops", "E"), 10).Concat(Enumerable.Repeat(R("c1", "Fin", "E"), 10))
            .Append(R("c2", "Ops", "E")).Concat(Enumerable.Repeat(R("c2", "Fin", "E"), 9)).ToList();
        var family = CohortViewFamily.Build(new CohortPopulation(records), Dims, 5);
        var group = family.Evaluate(null, [F("department", "Ops")]);
        var c1 = Scope("company", "c1");
        var scoped = family.Evaluate(family.Resolve(c1, [F("department", "Ops")]), [F("department", "Ops")]);
        (group.IsVisible && scoped.IsVisible).Should().BeFalse("group Ops − c1 Ops would isolate one person");
        AssertNoLeak(family, "attack B");
    }

    [Fact]
    public void AScopedReader_UsesTheScopeAsOneOfTwoDimensions()
    {
        var records = Golden.Groups.Select(g => R(g.Dept == "Leg" ? "A2" : "A1", g.Dept, g.Desig)).ToList();
        records.AddRange(Enumerable.Repeat(R("A2", "Ops", "Eng"), 6));
        var family = CohortViewFamily.Build(new CohortPopulation(records), Dims, 3);
        var a1 = Scope("company", "A1");
        // Scope + one filter is permitted; scope + filter + breakdown is not (Ops is in both companies).
        family.Resolve(a1, [F("department", "Ops")]).Should().NotBeNull();
        var act = () => family.Resolve(a1, [F("department", "Ops")], extraDimensions: 1);
        act.Should().Throw<CohortViewNotPermittedException>();
        var two = () => family.Resolve(a1, [F("department", "Ops"), F("designation", "Eng")]);
        two.Should().Throw<CohortViewNotPermittedException>();
    }

    [Fact]
    public void AScopedReader_WhoseFilterIsNestedInTheScope_KeepsTwoDimensions()
    {
        // Finance exists only in A1: for an A1 reader, "Finance" already lies inside the scope, so the scope adds nothing
        // and Finance × designation (two dimensions) is the same family view group HR would see.
        var records = Enumerable.Repeat(R("A1", "Fin", "Ana"), 8).Concat(Enumerable.Repeat(R("A1", "Fin", "Eng"), 8))
            .Concat(Enumerable.Repeat(R("A1", "Ops", "Eng"), 8)).Concat(Enumerable.Repeat(R("A2", "Ops", "Eng"), 8))
            .Concat(Enumerable.Repeat(R("A2", "Ops", "Ana"), 8)).ToList();
        var family = CohortViewFamily.Build(new CohortPopulation(records), Dims, 5);
        var a1 = Scope("company", "A1");
        var resolved = family.Resolve(a1, [F("department", "Fin")], extraDimensions: 1);
        resolved.Should().BeNull("every Finance respondent is in A1");
        var cells = family.Breakdown(resolved, [F("department", "Fin")], "designation");
        cells.Select(c => (c.Value, c.Decision.IsVisible, c.Decision.Count)).Should().BeEquivalentTo(new[] { ("Ana", true, 8), ("Eng", true, 8) });
        family.Evaluate(family.Resolve(a1, [F("department", "Fin"), F("designation", "Ana")]), [F("department", "Fin"), F("designation", "Ana")])
            .Should().Be(SuppressionDecision.Visible(8));
        // Ops is in both companies: the same request is refused for Ops.
        var ops = () => family.Resolve(a1, [F("department", "Ops")], extraDimensions: 1);
        ops.Should().Throw<CohortViewNotPermittedException>();
        AssertNoLeak(family, "nested");
    }

    [Fact]
    public void AScopedView_IsShownOnlyIfEveryComponentIs()
    {
        // Team m2 has 2 respondents: a manager over m1 and m2 sees nothing, since (m1 + m2) − m1 = m2.
        var records = Enumerable.Repeat(R("c1", "Ops", "E", team: "m1"), 10).Concat(Enumerable.Repeat(R("c1", "Ops", "E", team: "m2"), 2))
            .Concat(Enumerable.Repeat(R("c1", "Fin", "E", team: "m3"), 10)).ToList();
        var family = CohortViewFamily.Build(new CohortPopulation(records), Dims, 5);
        var both = Scope("team", "m1", "m2");
        family.Evaluate(family.Resolve(both, []), []).IsVisible.Should().BeFalse();
        AssertNoLeak(family, "components");
    }

    [Fact]
    public void DataCoincidenceNesting_IsCaught()
    {
        // Every supervisor is in A: (A) − (Supervisor) = 2 people although no filter relates them.
        var records = Enumerable.Range(0, 5).Select(_ => R("c1", "A", "Sup")).Concat(Enumerable.Range(0, 2).Select(_ => R("c1", "A", "Eng")))
            .Concat(Enumerable.Range(0, 10).Select(_ => R("c1", "B", "Eng"))).ToList();
        var family = CohortViewFamily.Build(new CohortPopulation(records), Dims, 5);
        (family.Evaluate(null, [F("department", "A")]).IsVisible && family.Evaluate(null, [F("designation", "Sup")]).IsVisible)
            .Should().BeFalse("showing both would isolate the 2 engineers in A");
        AssertNoLeak(family, "coincidence");
    }

    private static List<CohortRecord> RandomPopulation(Random random, bool multiScope)
    {
        var depts = new[] { "A", "B", "C", "D" };
        var desigs = new[] { "X", "Y", "Z" };
        var records = new List<CohortRecord>();
        foreach (var d in depts.Take(random.Next(1, depts.Length + 1)))
            foreach (var g in desigs.Take(random.Next(1, desigs.Length + 1)))
            {
                var n = random.Next(0, 13);
                for (var i = 0; i < n; i++)
                {
                    var company = multiScope ? $"c{random.Next(random.Next(1, 4))}" : "c0";
                    // Teams mostly follow the department, sometimes not — both nesting and crossing occur.
                    var team = multiScope ? (random.Next(4) == 0 ? $"m{random.Next(3)}" : $"m-{d}") : "m0";
                    records.Add(R(company, d, g, random.Next(2) == 0 ? "t1" : "t2", team));
                }
            }
        return records;
    }

    [Fact]
    public void RandomisedPopulations_PassTheLinearSpanAudit()
    {
        var random = new Random(20261011);
        var visibleFiltered = 0;
        var visibleScoped = 0;
        for (var run = 0; run < 400; run++)
        {
            var records = RandomPopulation(random, multiScope: run % 4 >= 2);
            var k = run % 2 == 0 ? 5 : 3;
            var family = CohortViewFamily.Build(new CohortPopulation(records), Dims, k);
            visibleFiltered += family.Views().Count(v => v.Visible && v.Filters.Count > 0);
            AssertRules(family, $"run {run}");
            LinearSpanAudit.FindLeak(family).Should().BeNull($"run {run} (k={k}, {records.Count} people)");
            var scopes = AllSingleValueScopes(family).ToList();
            var companies = records.Select(r => r.ValueOf("company")).Distinct().ToArray();
            if (companies.Length > 1) scopes.Add(Scope("company", companies.Take(2).ToArray()));
            var scopedRows = ScopedRows(family, scopes);
            visibleScoped += scopedRows.Count;
            LinearSpanAudit.FindLeak(family, scopedRows).Should().BeNull($"run {run} with scoped readers (k={k})");
        }
        visibleFiltered.Should().BeGreaterThan(1000, "the audit must be exercised on many visible filtered views, not vacuously");
        visibleScoped.Should().BeGreaterThan(500);
    }

    [Fact]
    public void RandomisedCommentLists_LeaveNoSmallAtom()
    {
        var random = new Random(611);
        var shown = 0;
        for (var run = 0; run < 200; run++)
        {
            var records = RandomPopulation(random, multiScope: true);
            var k = run % 2 == 0 ? 5 : 3;
            var family = CohortViewFamily.Build(new CohortPopulation(records), ["company", "team", "department"], k, setMode: true);
            var rows = family.Views().Where(v => v.Visible).Select(v => v.Members).ToList();
            var scoped = new[] { "company", "team" }
                .SelectMany(d => records.Select(r => r.ValueOf(d)).Distinct().Select(v => Scope(d, v)))
                .SelectMany(scope => new List<List<CohortFilter>> { new() }.Concat(records.Select(r => r.ValueOf("department")).Distinct().Select(v => new List<CohortFilter> { F("department", v) }))
                    .Select(filters =>
                    {
                        CohortScope? resolved;
                        try { resolved = family.Resolve(scope, filters); } catch (CohortViewNotPermittedException) { return null; }
                        if (!family.Evaluate(resolved, filters).IsVisible) return null;
                        return (IReadOnlyCollection<int>)Enumerable.Range(0, records.Count)
                            .Where(i => (resolved is null || resolved.Matches(records[i])) && filters.All(f => f.Matches(records[i]))).ToList();
                    }))
                .OfType<IReadOnlyCollection<int>>().ToList();
            shown += rows.Count + scoped.Count;
            LinearSpanAudit.FindSetLeak(records.Count, k, rows.Concat(scoped)).Should().BeNull($"run {run}");
        }
        shown.Should().BeGreaterThan(400);
    }

    [Fact]
    public void ARequestCombiningMoreThanTwoDimensions_IsRefused()
    {
        var family = CohortViewFamily.Build(new CohortPopulation(Golden.Groups.Select(g => R("A1", g.Dept, g.Desig))), Dims, 5);
        var act = () => family.Evaluate(null, [F("department", "Ops"), F("designation", "Eng"), F("tenure", "1to3y")]);
        act.Should().Throw<CohortViewNotPermittedException>();
        var breakdown = () => family.Resolve(null, [F("department", "Ops"), F("designation", "Eng")], extraDimensions: 1);
        breakdown.Should().Throw<CohortViewNotPermittedException>();
    }

    [Fact]
    public void ALargeSurvey_IsDecidedQuickly()
    {
        var random = new Random(7);
        var records = Enumerable.Range(0, 3000).Select(_ =>
        {
            var dept = random.Next(40);
            return new CohortRecord(new Dictionary<string, string?>
            {
                ["department"] = $"d{dept}", ["designation"] = $"g{random.Next(60)}", ["tenure"] = $"t{random.Next(4)}",
                ["grade"] = $"b{random.Next(8)}", ["company"] = $"c{random.Next(3)}", ["team"] = $"m{dept}-{random.Next(5)}",
            });
        }).ToList();
        var watch = Stopwatch.StartNew();
        CohortViewFamily.Build(new CohortPopulation(records), Dims, 5);
        var aggregate = watch.Elapsed;
        CohortViewFamily.Build(new CohortPopulation(records), ["company", "team", "department"], 5, setMode: true);
        // ≈ 0.3–0.6 s on an idle machine. The bounds are wall clock inside a full parallel test run, where this test
        // has measured 8–11 s; they are set to catch an algorithmic regression (a quadratic R6 took ~30 s), not jitter.
        aggregate.Should().BeLessThan(TimeSpan.FromSeconds(20), "one metric's family for a 3,000-person survey");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "results and comments for a 3,000-person survey must not time out");
    }

    // ── k changes after close (review 3) ─────────────────────────────────────────────────────────────────────────

    private static List<CohortRecord> ReviewerPopulation(Random random)
    {
        var n = random.Next(15, 60);
        return Enumerable.Range(0, n).Select(_ => new CohortRecord(new Dictionary<string, string?>
        {
            ["company"] = "c" + random.Next(2), ["department"] = "d" + random.Next(3), ["team"] = "m" + random.Next(3),
            ["designation"] = "g" + random.Next(3), ["grade"] = "b1", ["tenure"] = "t" + random.Next(2),
        })).ToList();
    }

    private static readonly string[] CommentDims = ["company", "department", "team"];

    [Fact]
    public void RecomputingTheFamilyAtAnotherK_WouldLeak_SoTheAuditIsNotVacuous()
    {
        // The reviewer's engine search: the union of families decided at k and at a raised k isolates small groups.
        var random = new Random(1);
        var found = 0;
        for (var run = 0; run < 3000 && found == 0; run++)
        {
            var records = ReviewerPopulation(random);
            var population = new CohortPopulation(records);
            foreach (var k2 in new[] { 6, 7, 8 })
            {
                var union = CohortViewFamily.Build(population, Dims, 5).Views().Where(v => v.Visible).Select(v => v.Members)
                    .Concat(CohortViewFamily.Build(population, Dims, k2).Views().Where(v => v.Visible).Select(v => v.Members)).ToList();
                if (LinearSpanAudit.FindLeak(records, Dims, 5, union) is not null) { found++; break; }
            }
        }
        found.Should().BeGreaterThan(0, "two independently decided families at k and k′ must be caught by the audit");
    }

    [Fact]
    public void RaisingKAfterClose_OnlyHidesViewsOfTheFrozenFamily_NumbersAndComments()
    {
        var random = new Random(1);
        for (var run = 0; run < 300; run++)
        {
            var records = ReviewerPopulation(random);
            var population = new CohortPopulation(records);
            var k0 = run % 2 == 0 ? 5 : 3;
            foreach (var setMode in new[] { false, true })
            {
                var dims = setMode ? CommentDims : Dims;
                var frozen = CohortViewFamily.Build(population, dims, k0, setMode: setMode);
                var frozenVisible = frozen.Views().Where(v => v.Visible).Select(v => CohortViewFamily.KeyOf(v.Filters)).ToHashSet();
                var union = new List<IReadOnlyCollection<int>>();
                foreach (var k in Enumerable.Range(k0, 4))
                {
                    // A tenant raising k to k0 … k0+3 and lowering it again: every state is read.
                    var family = CohortViewFamily.BuildFrozen(population, dims, k0, k, setMode: setMode);
                    family.K.Should().Be(k);
                    var visible = family.Views().Where(v => v.Visible).ToList();
                    visible.Select(v => CohortViewFamily.KeyOf(v.Filters)).Should().BeSubsetOf(frozenVisible, $"run {run}: k {k0}→{k} may only hide");
                    union.AddRange(visible.Select(v => v.Members));
                    if (setMode) LinearSpanAudit.FindSetLeak(records.Count, k, visible.Select(v => v.Members)).Should().BeNull($"run {run} at k {k}");
                    else LinearSpanAudit.FindLeak(records, dims, k, visible.Select(v => v.Members)).Should().BeNull($"run {run} at k {k}");
                }
                if (setMode) LinearSpanAudit.FindSetLeak(records.Count, k0, union).Should().BeNull($"run {run}: comment lists across k changes");
                else LinearSpanAudit.FindLeak(records, dims, k0, union).Should().BeNull($"run {run}: numbers across k changes");
            }
        }
    }

    [Fact]
    public void ScopeNestingAndBreakdownCells_DependOnInviteesNotRespondents()
    {
        // Invitees: Ops in A1 (10) and ONE Ops invitee in A2. Respondents: the same, with or without that A2 person.
        var invitees = Enumerable.Repeat(R("A1", "Ops", "Eng"), 10).Append(R("A1", "Ops", "Sup")).Append(R("A2", "Ops", "Eng")).ToList();
        var without = Enumerable.Repeat(R("A1", "Ops", "Eng"), 10).ToList();
        var with = without.Append(R("A2", "Ops", "Eng")).ToList();
        var a1 = Scope("company", "A1");
        foreach (var respondents in new[] { without, with })
        {
            var family = CohortViewFamily.Build(new CohortPopulation(respondents), Dims, 5);
            var act = () => family.Resolve(a1, [F("department", "Ops")], extraDimensions: 1, nestingRecords: invitees);
            act.Should().Throw<CohortViewNotPermittedException>("an A2 Ops person is INVITED, whether or not they responded");
            family.Breakdown(null, [F("department", "Ops")], "designation", invitees).Select(c => c.Value)
                .Should().Equal(["Eng", "Sup"], "the A1 supervisor is listed (hidden) although they did not respond");
        }
    }
}
