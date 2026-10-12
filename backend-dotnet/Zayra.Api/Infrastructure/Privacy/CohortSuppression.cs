namespace Zayra.Api.Infrastructure.Privacy;

/// <summary>
/// THE small-cohort suppression rule — one implementation shared by every surface that shows an aggregate of
/// people: engagement dashboards, drill-down, exports, scheduled delivery, and (later) BI and the analytics pages.
/// Pure: no database, no clock, no I/O. Spec: docs/acceptance/capability-closure/REGISTER.md §3.2.4, worked examples
/// in ENG_DESIGN.md §5.
///
/// <para><b>Model.</b> A population is a list of records, one per person counted (a respondent, or the respondents
/// who answered one item). Each record carries coarse attributes (department, designation, grade band…). A VIEW is a
/// set of <see cref="CohortFilter"/>s — "department ∈ {A}", "company ∈ {the caller's companies}" — and its count is
/// the records matching every filter. The root (no filter) is the whole population.</para>
///
/// <para><b>Rule 1 — minimum cohort.</b> A view is shown only if n(view) ≥ k.</para>
///
/// <para><b>Rule 2 — complement (difference attack).</b> For EVERY view obtainable by dropping one or more of the
/// view's filters (every proper subset, the root included), the difference n(superset) − n(view) must be 0 or ≥ k.
/// Otherwise "parent minus slice" isolates fewer than k people. Applying it to every subset — not just the
/// immediate parent — is what makes intersecting filters safe: (dept A) − (dept A ∩ designation X) is checked as well
/// as (all) − (dept A ∩ designation X).</para>
///
/// <para><b>Rule 3 — secondary suppression (complementary totals).</b> In a breakdown (siblings partitioning a
/// parent), a parent total plus all-but-one visible sibling reveals the last one. So: when any sibling is
/// suppressed, the next-smallest visible sibling is suppressed too; and while the suppressed siblings together hold
/// fewer than k records (but more than 0) more are suppressed, smallest first, so the residue derivable as
/// "parent − visible" is itself a group of at least k.</para>
///
/// <para><b>What it does not claim.</b> It cannot stop inference across DIFFERENT breakdown dimensions shown side by
/// side (margins of a contingency table) or across survey cycles, and it says nothing about someone who can read
/// the database directly. Those residuals are documented in ENG_DESIGN.md §4; they are why the product never says
/// "absolute anonymity".</para>
/// </summary>
public static class CohortSuppression
{
    /// <summary>The smallest k a tenant or survey may set.</summary>
    public const int Floor = 3;

    /// <summary>The default k.</summary>
    public const int DefaultK = 5;

    /// <summary>The key that represents a missing attribute value. It is a real sibling and is suppressed like one.</summary>
    public const string Unknown = "unknown";

    /// <summary>The message every suppressed cell shows (the UI substitutes k).</summary>
    public const string SuppressedMessage = "Not enough responses to protect confidentiality (minimum {0}).";

    /// <summary>A requested k, raised to the floor; null falls back to the tenant default (itself floored).</summary>
    public static int EffectiveK(int? requested, int tenantDefault) =>
        Math.Max(Floor, requested ?? Math.Max(Floor, tenantDefault));

    /// <summary>Decision for one view, against every superset view (rules 1 and 2).</summary>
    public static SuppressionDecision EvaluateView(CohortPopulation population, IReadOnlyList<CohortFilter> view, int k)
    {
        ArgumentNullException.ThrowIfNull(population);
        k = Math.Max(Floor, k);
        var n = population.Count(view);
        if (n < k) return SuppressionDecision.Hidden(n, SuppressionReasons.BelowMinimum);
        foreach (var superset in ProperSubsets(view))
        {
            var complement = population.Count(superset) - n;
            if (complement > 0 && complement < k)
                return SuppressionDecision.Hidden(n, SuppressionReasons.ComplementTooSmall);
        }
        return SuppressionDecision.Visible(n);
    }

    /// <summary>
    /// Decisions for each value of <paramref name="dimension"/> inside <paramref name="parent"/> (rules 1–3). The
    /// cells are every value that occurs in the parent, including <see cref="Unknown"/>, so they partition it.
    /// When the parent itself is not visible every cell is hidden.
    /// </summary>
    public static IReadOnlyList<CellDecision> EvaluateBreakdown(
        CohortPopulation population, IReadOnlyList<CohortFilter> parent, string dimension, int k)
    {
        ArgumentNullException.ThrowIfNull(population);
        k = Math.Max(Floor, k);
        var parentDecision = EvaluateView(population, parent, k);
        var values = population.ValuesOf(dimension, parent);

        var cells = new List<CellDecision>(values.Count);
        foreach (var value in values)
        {
            var cellView = parent.Append(new CohortFilter(dimension, [value])).ToList();
            var decision = parentDecision.IsVisible
                ? EvaluateView(population, cellView, k)
                : SuppressionDecision.Hidden(population.Count(cellView), SuppressionReasons.ParentHidden);
            cells.Add(new CellDecision(value, decision));
        }

        return ApplySecondarySuppression(cells, k);
    }

    /// <summary>
    /// Rule 3 over an already-decided set of siblings. Public so a caller that partitions by something other than an
    /// attribute (e.g. answer options) can reuse it.
    /// </summary>
    public static IReadOnlyList<CellDecision> ApplySecondarySuppression(IReadOnlyList<CellDecision> siblings, int k)
    {
        k = Math.Max(Floor, k);
        var cells = siblings.ToList();
        bool AnyHidden() => cells.Any(c => !c.Decision.IsVisible);
        int HiddenTotal() => cells.Where(c => !c.Decision.IsVisible).Sum(c => c.Decision.Count);

        void HideNextSmallest()
        {
            var next = cells.Select((c, i) => (c, i))
                .Where(x => x.c.Decision.IsVisible)
                .OrderBy(x => x.c.Decision.Count).ThenBy(x => x.c.Value, StringComparer.Ordinal)
                .FirstOrDefault();
            if (next.c is null) return;
            cells[next.i] = next.c with { Decision = SuppressionDecision.Hidden(next.c.Decision.Count, SuppressionReasons.Secondary) };
        }

        if (!AnyHidden()) return cells;

        // Exactly one hidden sibling is always recoverable from parent − visible: hide the next-smallest.
        if (cells.Count(c => !c.Decision.IsVisible) == 1) HideNextSmallest();

        // The hidden group as a whole must not be a small group either.
        var guard = cells.Count;
        while (guard-- > 0)
        {
            var hidden = HiddenTotal();
            if (hidden == 0 || hidden >= k) break;
            if (cells.All(c => !c.Decision.IsVisible)) break;
            HideNextSmallest();
        }
        return cells;
    }

    /// <summary>Every proper subset of <paramref name="view"/> (2^n − 1 of them, the empty view included).</summary>
    internal static IEnumerable<IReadOnlyList<CohortFilter>> ProperSubsets(IReadOnlyList<CohortFilter> view)
    {
        if (view.Count > 12) throw new ArgumentException("A view may combine at most 12 filters.", nameof(view));
        var full = (1 << view.Count) - 1;
        for (var mask = 0; mask < full; mask++)
        {
            var subset = new List<CohortFilter>();
            for (var i = 0; i < view.Count; i++)
                if ((mask & (1 << i)) != 0) subset.Add(view[i]);
            yield return subset;
        }
    }
}

/// <summary>
/// THE view family: every view ANY reader of a survey can obtain — group HR, company HR, a company switcher, a line
/// manager — decided together over the WHOLE survey population, so no combination of readers or requests isolates a
/// small group. <see cref="CohortSuppression.EvaluateView"/> and <see cref="CohortSuppression.EvaluateBreakdown"/> are
/// its single-view building blocks.
///
/// <para><b>One family per survey (per metric population).</b> A reader's scope is not a separate population: it is a
/// set of values of the family's own scope DIMENSIONS (company; team = line manager at submission), and it counts
/// toward the two-dimension cap. A scoped view "scope ∩ F" is the sum of the family views {scope = s} ∩ F over the
/// scope's values (a <see cref="CohortScope"/> component each), shown only if every non-empty one of them is. When
/// every person F selects already lies inside the scope (a department that exists only in the reader's company), the
/// scope adds nothing: the view is F itself and the reader keeps both free dimensions.</para>
///
/// <para><b>Aggregate mode</b> (counts, means, percentages — anything a reader can add and subtract). Rules: R1 and R5
/// are applied on counts, then R6, then R3. R2 and R4 need no pass of their own: R6 implies them (a nested difference
/// or a residue is a union of atoms), and the property tests assert all of R1–R6 on every population they build.</para>
/// <list type="number">
/// <item>R1 minimum — fewer than k people: hidden.</item>
/// <item>R2 nested difference — for every view A that contains this view's people (by filters or by coincidence of the
/// data), |A| − |V| is 0 or ≥ k.</item>
/// <item>R3 inheritance — a view whose parent (one filter removed) is hidden is hidden.</item>
/// <item>R4 partition residue — for every visible view P with room for one more filter and every dimension d, the
/// people of P outside its visible children P ∩ {d = v} number 0 or ≥ k; otherwise the smallest visible child is hidden.</item>
/// <item>R5 four quadrants — a two-filter view X ∩ Y is shown only if X ∩ Y, X ∩ ¬Y, ¬X ∩ Y and ¬X ∩ ¬Y are each 0 or
/// ≥ k; otherwise root − X − Y + X∩Y recovers a small group by inclusion–exclusion.</item>
/// <item>R6 atom closure (final pass) — group the people by exactly WHICH visible views they are in (the atoms of the
/// Boolean algebra the visible views generate); every atom has 0 or ≥ k people. While one does not, a view is hidden
/// (preferably one whose hiding merges that atom with a neighbour; two-filter views before single ones; the less
/// important dimension first — the order the caller passes the dimensions; then the smaller view).</item>
/// </list>
/// <para>Why R6: R1–R5 alone fail the LINEAR-SPAN audit (EngagementViewFamilyPropertyTests) — e.g. A + Eng − root
/// isolates A ∩ Eng when nobody is outside both. Any linear combination of visible views is constant on atoms, so a
/// group in their row space is a union of atoms, and with R6 every such group has 0 or ≥ k people: R6 makes the span
/// audit hold BY CONSTRUCTION, in O(people × views) per hidden view, so it runs in the engine at production size. The
/// span audit stays as the test oracle. Hiding more views only merges atoms, so R6 survives every later hide.</para>
///
/// <para><b>Set mode</b> (comment lists — a reader can intersect and subtract LISTS, not just add numbers): R1 and R6.
/// R6 is exactly the condition for lists — every Boolean combination of visible lists is a union of atoms.</para>
/// </summary>
public sealed class CohortViewFamily
{
    public const int MaxFilters = 2;

    private int _k;
    private readonly int _maxFilters;
    private readonly bool _setMode;
    private readonly string[] _dimensions;
    private readonly List<CohortRecord> _records;
    private readonly Dictionary<string, int> _priority;
    private readonly Dictionary<string, View> _views = new(StringComparer.Ordinal);

    private sealed class View
    {
        public required string Key { get; init; }
        public required (string Dim, string Value)[] Pairs { get; init; }
        public List<int> Members { get; } = new();
        public bool Visible { get; set; } = true;
        public string? Reason { get; set; }
    }

    private CohortViewFamily(int k, int maxFilters, bool setMode, string[] dimensions, List<CohortRecord> records, IReadOnlyList<string> priority)
    {
        _k = Math.Max(CohortSuppression.Floor, k);
        _maxFilters = maxFilters;
        _setMode = setMode;
        _dimensions = dimensions;
        _records = records;
        _priority = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in priority) _priority.TryAdd(d, _priority.Count);
    }

    /// <param name="population">Everyone counted for this metric in the WHOLE survey.</param>
    /// <param name="dimensions">Every dimension any reader can see the data by: the filterable ones AND the scope
    /// dimensions (company, team), MOST IMPORTANT FIRST — R6 hides views of later dimensions first.</param>
    /// <param name="maxFilters">Filters per view; a scope and a breakdown each count as one.</param>
    /// <param name="setMode">True for member LISTS (comments): see the class remarks.</param>
    public static CohortViewFamily Build(CohortPopulation population, IReadOnlyList<string> dimensions, int k,
        int maxFilters = MaxFilters, bool setMode = false)
    {
        ArgumentNullException.ThrowIfNull(population);
        var dims = dimensions.Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        var family = new CohortViewFamily(k, Math.Clamp(maxFilters, 0, MaxFilters), setMode, dims, population.Matching([]).ToList(), dimensions);
        family.Index();
        if (setMode)
        {
            foreach (var v in family._views.Values.Where(v => v.Members.Count < family._k)) Hide(v, SuppressionReasons.BelowMinimum);
            family.CloseAtoms();
        }
        else
        {
            family.Decide();
            family.CloseAtoms();
            family.Inherit();
        }
        return family;
    }

    /// <summary>
    /// A family decided at the survey's FROZEN k (fixed when it closed), further restricted by a later, larger k: a view
    /// is shown only if it is shown at both. The visible set is therefore always a subset of the frozen one — raising k
    /// and lowering it again can only ever reveal views of the frozen family, whose atoms are all 0 or ≥ the frozen k —
    /// and every atom of the result is also 0 or ≥ the larger k (a subset of that family's visible views too).
    /// </summary>
    public static CohortViewFamily BuildFrozen(CohortPopulation population, IReadOnlyList<string> dimensions, int frozenK, int currentK,
        int maxFilters = MaxFilters, bool setMode = false)
    {
        var family = Build(population, dimensions, frozenK, maxFilters, setMode);
        if (currentK <= family._k) return family;
        var stricter = Build(population, dimensions, currentK, maxFilters, setMode);
        foreach (var (key, view) in family._views)
            if (view.Visible && !stricter._views[key].Visible) Hide(view, stricter._views[key].Reason!);
        family._k = stricter._k;
        return family;
    }

    internal static string KeyOf(IEnumerable<(string Dim, string Value)> pairs) =>
        string.Join("|", pairs.OrderBy(p => p.Dim, StringComparer.Ordinal).Select(p => p.Dim + "=" + p.Value));

    private IEnumerable<(string Dim, string Value)[]> PairSets(CohortRecord r)
    {
        yield return [];
        if (_maxFilters < 1) yield break;
        for (var i = 0; i < _dimensions.Length; i++)
        {
            yield return [(_dimensions[i], r.ValueOf(_dimensions[i]))];
            if (_maxFilters < 2) continue;
            for (var j = i + 1; j < _dimensions.Length; j++)
                yield return [(_dimensions[i], r.ValueOf(_dimensions[i])), (_dimensions[j], r.ValueOf(_dimensions[j]))];
        }
    }

    private void Index()
    {
        for (var i = 0; i < _records.Count; i++)
        {
            foreach (var pairs in PairSets(_records[i]))
            {
                var key = KeyOf(pairs);
                if (!_views.TryGetValue(key, out var view))
                    _views[key] = view = new View { Key = key, Pairs = pairs.OrderBy(p => p.Dim, StringComparer.Ordinal).ToArray() };
                view.Members.Add(i);
            }
        }
        if (!_views.ContainsKey(string.Empty)) _views[string.Empty] = new View { Key = string.Empty, Pairs = [] };
    }

    private static void Hide(View v, string reason)
    {
        if (!v.Visible) return;
        v.Visible = false;
        v.Reason = reason;
    }

    private int CountOf((string Dim, string Value)[] pairs) => _views.TryGetValue(KeyOf(pairs), out var v) ? v.Members.Count : 0;

    private bool Safe(int n) => n == 0 || n >= _k;

    /// <summary>R1 and R5 (count rules). R2–R4 then hold as consequences of R6 (see the class remarks); the property
    /// tests assert them.</summary>
    private void Decide()
    {
        var total = _records.Count;
        foreach (var v in _views.Values)
        {
            var n = v.Members.Count;
            if (n < _k) { Hide(v, SuppressionReasons.BelowMinimum); continue; }
            if (v.Pairs.Length == 2)
            {
                var nx = CountOf([v.Pairs[0]]);
                var ny = CountOf([v.Pairs[1]]);
                if (!Safe(nx - n) || !Safe(ny - n) || !Safe(total - nx - ny + n)) Hide(v, SuppressionReasons.QuadrantTooSmall);
            }
        }
    }

    /// <summary>R3 alone, after R6: a two-filter view whose parent is hidden is hidden too (presentation only — R6
    /// already holds and hiding more cannot break it).</summary>
    private void Inherit()
    {
        foreach (var v in _views.Values.Where(v => v.Visible && v.Pairs.Length == 2))
            if (v.Pairs.Any(drop => !_views[KeyOf(v.Pairs.Where(p => p != drop))].Visible)) Hide(v, SuppressionReasons.ParentHidden);
    }

    /// <summary>
    /// R6: every atom of the algebra the visible views generate has 0 or ≥ k people. A small atom is merged with the
    /// neighbouring atom that is cheapest to merge it with — by hiding every visible view that tells the two apart —
    /// where hiding a two-filter view is cheapest, then a view of a less important dimension, then a smaller view.
    /// </summary>
    private void CloseAtoms()
    {
        var root = _views[string.Empty];
        if (!root.Visible) { foreach (var v in _views.Values) Hide(v, SuppressionReasons.ParentHidden); return; }
        var views = _views.Values.Where(v => v.Pairs.Length > 0).OrderBy(v => v.Key, StringComparer.Ordinal).ToArray();
        var memberships = new List<int>[_records.Count];
        for (var i = 0; i < _records.Count; i++) memberships[i] = new List<int>();
        for (var v = 0; v < views.Length; v++)
            foreach (var m in views[v].Members) memberships[m].Add(v);

        // The cost of hiding a set of views, compared lexicographically: how many single views of the most important
        // dimension, then the next, …, then how many two-filter views, then how many people they hold. Each view adds
        // to exactly one level, so a partial sum only grows and a search can stop once it exceeds the best.
        var levels = _priority.Count + 2;
        var level = views.Select(v => v.Pairs.Length == 2 ? _priority.Count : Math.Min(_priority.Count - 1, _priority.GetValueOrDefault(v.Pairs[0].Dim, _priority.Count - 1))).ToArray();
        static int Compare(long[] a, long[] b)
        {
            for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return 0;
        }

        // A person's atom is identified by the XOR of random 64-bit ids of the visible views they are in. Atoms are
        // kept incrementally: hiding a view moves only its members.
        var random = new Random(20261011);
        var id = views.Select(_ => (ulong)random.NextInt64() ^ ((ulong)random.NextInt64() << 32)).ToArray();
        var signature = new ulong[_records.Count];
        for (var i = 0; i < _records.Count; i++)
            foreach (var v in memberships[i]) if (views[v].Visible) signature[i] ^= id[v];
        var atoms = new Dictionary<ulong, HashSet<int>>();
        for (var i = 0; i < _records.Count; i++)
        {
            if (!atoms.TryGetValue(signature[i], out var set)) atoms[signature[i]] = set = new HashSet<int>();
            set.Add(i);
        }
        // The visible views of an atom's people, by signature (stable: hiding a view changes its members' signature).
        var visibleBySignature = new Dictionary<ulong, int[]>();
        int[] VisibleOf(ulong sig, int person)
        {
            if (!visibleBySignature.TryGetValue(sig, out var list))
                visibleBySignature[sig] = list = memberships[person].Where(v => views[v].Visible).ToArray();
            return list;
        }

        while (true)
        {
            var small = atoms.Where(a => a.Value.Count < _k).OrderBy(a => a.Value.Count).ThenBy(a => a.Key).FirstOrDefault();
            if (small.Value is null) break;
            var mine = VisibleOf(small.Key, small.Value.First());
            int[]? best = null;
            long[]? bestCost = null;
            var differ = new List<int>();
            var total = new long[levels];
            foreach (var (sig, people) in atoms)
            {
                if (sig == small.Key) continue;
                var theirs = VisibleOf(sig, people.First());
                // Symmetric difference of two sorted lists, abandoned once it costs more than the best so far.
                differ.Clear();
                Array.Clear(total);
                int x = 0, y = 0;
                var worse = false;
                while (x < mine.Length || y < theirs.Length)
                {
                    int v;
                    if (y >= theirs.Length || (x < mine.Length && mine[x] < theirs[y])) v = mine[x++];
                    else if (x >= mine.Length || theirs[y] < mine[x]) v = theirs[y++];
                    else { x++; y++; continue; }
                    differ.Add(v);
                    total[level[v]]++;
                    total[levels - 1] += views[v].Members.Count;
                    if (bestCost is not null && Compare(total, bestCost) >= 0) { worse = true; break; }
                }
                if (!worse) { bestCost = (long[])total.Clone(); best = differ.ToArray(); }
            }
            // A single atom is the whole survey (≥ k, the root is visible).
            if (best is null || best.Length == 0) break;
            foreach (var v in best)
            {
                Hide(views[v], SuppressionReasons.Secondary);
                foreach (var m in views[v].Members)
                {
                    var set = atoms[signature[m]];
                    set.Remove(m);
                    if (set.Count == 0) atoms.Remove(signature[m]);
                    signature[m] ^= id[v];
                    if (!atoms.TryGetValue(signature[m], out set)) atoms[signature[m]] = set = new HashSet<int>();
                    set.Add(m);
                }
            }
        }

        // Exact verification of the hashed atoms (a 64-bit collision is astronomically unlikely; if it ever happens,
        // fail safe: nothing below the whole survey is shown).
        var exact = Enumerable.Range(0, _records.Count)
            .GroupBy(i => string.Join(",", memberships[i].Where(v => views[v].Visible)), StringComparer.Ordinal);
        if (exact.Any(g => g.Count() < _k))
            foreach (var v in views) Hide(v, SuppressionReasons.Secondary);
    }

    private static (string Dim, string Value)[] PairsOf(IReadOnlyList<CohortFilter> filters)
    {
        var pairs = new List<(string, string)>();
        foreach (var f in filters)
        {
            if (f.Values.Count != 1) throw new ArgumentException("A view filter takes exactly one value.", nameof(filters));
            pairs.Add((f.Dimension, f.Values.First()));
        }
        if (pairs.Select(p => p.Item1).Distinct(StringComparer.Ordinal).Count() != pairs.Count)
            throw new ArgumentException("A dimension may be filtered once.", nameof(filters));
        return pairs.ToArray();
    }

    /// <summary>A scope component merged with the filters; null when they contradict (the component adds no one).</summary>
    private static (string Dim, string Value)[]? Merge((string Dim, string Value)[] component, (string Dim, string Value)[] pairs)
    {
        var merged = pairs.ToList();
        foreach (var c in component)
        {
            var same = merged.FirstOrDefault(p => p.Dim == c.Dim);
            if (same.Dim is null) merged.Add(c);
            else if (!string.Equals(same.Value, c.Value, StringComparison.Ordinal)) return null;
        }
        return merged.ToArray();
    }

    /// <summary>
    /// The scope a request is evaluated under: null when there is none or when every person the filters select lies
    /// inside it already (the scope then adds nothing). Throws <see cref="CohortViewNotPermittedException"/> when the
    /// scope, the filters and <paramref name="extraDimensions"/> (a breakdown) together exceed the cap. Decide this ONCE
    /// per request and pass the result to every metric's family.
    /// </summary>
    /// <param name="nestingRecords">Whom "the filters select" is decided over. Pass the survey's INVITEES (who is invited
    /// is not secret, who responded is): deciding it on respondents would let the allow/refuse answer reveal whether
    /// someone outside the scope responded. Null = this family's own records (pure tests).</param>
    public CohortScope? Resolve(CohortScope? scope, IReadOnlyList<CohortFilter> filters, int extraDimensions = 0,
        IReadOnlyList<CohortRecord>? nestingRecords = null)
    {
        var pairs = PairsOf(filters);
        if (pairs.Any(p => !_dimensions.Contains(p.Dim, StringComparer.Ordinal))) throw new CohortViewNotPermittedException(_maxFilters);
        if (scope is not null && pairs.Length > 0)
        {
            var selected = (nestingRecords ?? _records).Where(r => pairs.All(p => r.ValueOf(p.Dim) == p.Value)).ToList();
            if (selected.Count > 0 && selected.All(scope.Matches)) scope = null;
        }
        if (scope is null)
        {
            if (pairs.Length + extraDimensions > _maxFilters) throw new CohortViewNotPermittedException(_maxFilters);
            return null;
        }
        foreach (var component in scope.Components)
        {
            var merged = Merge(component, pairs);
            if (merged is not null && merged.Length + extraDimensions > _maxFilters) throw new CohortViewNotPermittedException(_maxFilters);
        }
        return scope;
    }

    private SuppressionDecision Decision((string Dim, string Value)[] pairs)
    {
        if (!_views.TryGetValue(KeyOf(pairs), out var view)) return SuppressionDecision.Hidden(0, SuppressionReasons.BelowMinimum);
        return view.Visible ? SuppressionDecision.Visible(view.Members.Count) : SuppressionDecision.Hidden(view.Members.Count, view.Reason!);
    }

    /// <summary>
    /// The decision for "scope ∩ filters", where <paramref name="scope"/> came from <see cref="Resolve"/> (null = the
    /// filters alone). A scoped view is the sum of its components' family views and is shown only if each is.
    /// </summary>
    public SuppressionDecision Evaluate(CohortScope? scope, IReadOnlyList<CohortFilter> filters)
    {
        var pairs = PairsOf(filters);
        if (scope is null)
        {
            if (pairs.Length > _maxFilters) throw new CohortViewNotPermittedException(_maxFilters);
            return Decision(pairs);
        }
        var total = 0;
        foreach (var component in scope.Components)
        {
            var merged = Merge(component, pairs);
            if (merged is null) continue;
            if (merged.Length > _maxFilters) throw new CohortViewNotPermittedException(_maxFilters);
            var part = Decision(merged);
            if (part.Count == 0) continue;
            if (!part.IsVisible) return SuppressionDecision.Hidden(total + part.Count, part.Reason!);
            total += part.Count;
        }
        return total == 0 ? SuppressionDecision.Hidden(0, SuppressionReasons.BelowMinimum) : SuppressionDecision.Visible(total);
    }

    /// <summary>The cells of a breakdown of "scope ∩ filters" by <paramref name="dimension"/>; scope from
    /// <see cref="Resolve"/> with one extra dimension.</summary>
    /// <param name="valueRecords">Whose values make the cells. Pass the survey's INVITEES, so the list of cells — hidden
    /// ones included — never reveals that someone with a rare value responded. Null = this family's records.</param>
    public IReadOnlyList<CellDecision> Breakdown(CohortScope? scope, IReadOnlyList<CohortFilter> filters, string dimension,
        IReadOnlyList<CohortRecord>? valueRecords = null)
    {
        var pairs = PairsOf(filters);
        if (pairs.Any(p => p.Dim == dimension) || !_dimensions.Contains(dimension, StringComparer.Ordinal))
            throw new CohortViewNotPermittedException(_maxFilters);
        var parent = Evaluate(scope, filters);
        var values = (valueRecords ?? _records).Where(r => (scope is null || scope.Matches(r)) && pairs.All(p => r.ValueOf(p.Dim) == p.Value))
            .Select(r => r.ValueOf(dimension)).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var cells = new List<CellDecision>(values.Count);
        foreach (var value in values)
        {
            var decision = Evaluate(scope, filters.Append(new CohortFilter(dimension, [value])).ToList());
            if (!parent.IsVisible && decision.IsVisible) decision = SuppressionDecision.Hidden(decision.Count, SuppressionReasons.ParentHidden);
            cells.Add(new CellDecision(value, decision));
        }
        return cells;
    }

    /// <summary>Every family view with its members (record indices) — for audits and tests.</summary>
    public IEnumerable<(IReadOnlyList<(string Dim, string Value)> Filters, IReadOnlyCollection<int> Members, bool Visible)> Views() =>
        _views.Values.Select(v => ((IReadOnlyList<(string, string)>)v.Pairs, (IReadOnlyCollection<int>)v.Members, v.Visible));

    public IReadOnlyList<CohortRecord> Records => _records;
    public IReadOnlyList<string> Dimensions => _dimensions;
    public int K => _k;
    public int MaxFiltersPermitted => _maxFilters;
    public bool SetMode => _setMode;
}

/// <summary>
/// A reader's scope inside a view family: a union of COMPONENTS, each a set of (scope dimension = value) pairs —
/// {company = A1}, {team = 42}, or {company = A1, team = 42} when a manager is also cut by company.
/// </summary>
public sealed class CohortScope
{
    public IReadOnlyList<(string Dim, string Value)[]> Components { get; }

    public CohortScope(IEnumerable<(string Dim, string Value)[]> components) =>
        Components = components.Select(c => c.OrderBy(p => p.Dim, StringComparer.Ordinal).ToArray())
            .DistinctBy(CohortViewFamily.KeyOf, StringComparer.Ordinal)
            .OrderBy(CohortViewFamily.KeyOf, StringComparer.Ordinal).ToList();

    /// <summary>The cartesian product of the filters' values; null when there are no filters (no scope).</summary>
    public static CohortScope? From(IReadOnlyList<CohortFilter> filters)
    {
        if (filters.Count == 0) return null;
        IEnumerable<(string Dim, string Value)[]> product = [[]];
        foreach (var f in filters)
        {
            var values = f.Values.ToList();
            product = product.SelectMany(c => values.Select(v => c.Append((f.Dimension, v)).ToArray())).ToList();
        }
        return new CohortScope(product);
    }

    public bool Matches(CohortRecord record) =>
        Components.Any(c => c.All(p => string.Equals(record.ValueOf(p.Dim), p.Value, StringComparison.Ordinal)));
}

/// <summary>A view combining more filters (a scope and a breakdown each counting as one) than the family permits.</summary>
public sealed class CohortViewNotPermittedException(int max)
    : InvalidOperationException($"Combine at most {max} of: your scope, filters and a breakdown.")
{
    public int Max { get; } = max;
}

/// <summary>"dimension ∈ values". Equality is a single value.</summary>
public sealed record CohortFilter(string Dimension, IReadOnlyCollection<string> Values)
{
    public bool Matches(CohortRecord record) => Values.Contains(record.ValueOf(Dimension), StringComparer.Ordinal);
}

/// <summary>One person counted: dimension → value (missing ⇒ <see cref="CohortSuppression.Unknown"/>).</summary>
public sealed class CohortRecord
{
    private readonly IReadOnlyDictionary<string, string?> _attributes;
    public CohortRecord(IReadOnlyDictionary<string, string?> attributes) => _attributes = attributes;

    public string ValueOf(string dimension) =>
        _attributes.TryGetValue(dimension, out var value) && !string.IsNullOrWhiteSpace(value) ? value! : CohortSuppression.Unknown;
}

/// <summary>A population of records with a memoised counter.</summary>
public sealed class CohortPopulation
{
    private readonly IReadOnlyList<CohortRecord> _records;
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    public CohortPopulation(IEnumerable<CohortRecord> records) => _records = records.ToList();

    public int Total => _records.Count;

    public int Count(IReadOnlyList<CohortFilter> view)
    {
        if (view.Count == 0) return _records.Count;
        var key = string.Join("|", view
            .Select(f => f.Dimension + "=" + string.Join(",", f.Values.OrderBy(v => v, StringComparer.Ordinal)))
            .OrderBy(s => s, StringComparer.Ordinal));
        if (_counts.TryGetValue(key, out var cached)) return cached;
        var n = _records.Count(r => view.All(f => f.Matches(r)));
        _counts[key] = n;
        return n;
    }

    public IEnumerable<CohortRecord> Matching(IReadOnlyList<CohortFilter> view) =>
        _records.Where(r => view.All(f => f.Matches(r)));

    /// <summary>The distinct values of a dimension inside a view, in ordinal order.</summary>
    public IReadOnlyList<string> ValuesOf(string dimension, IReadOnlyList<CohortFilter> within) =>
        Matching(within).Select(r => r.ValueOf(dimension)).Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal).ToList();
}

public static class SuppressionReasons
{
    /// <summary>Fewer than k people.</summary>
    public const string BelowMinimum = "below_minimum";
    /// <summary>A superset view minus this one would isolate fewer than k people.</summary>
    public const string ComplementTooSmall = "complement_too_small";
    /// <summary>Hidden so that a hidden sibling cannot be recovered from the parent total.</summary>
    public const string Secondary = "secondary";
    /// <summary>The view this cell sits in is itself hidden.</summary>
    public const string ParentHidden = "parent_hidden";
    /// <summary>A two-filter view whose four quadrants within the survey are not all 0 or ≥ k (inclusion–exclusion).</summary>
    public const string QuadrantTooSmall = "quadrant_too_small";
    /// <summary>
    /// The ONLY reason an API response gives for a hidden value. The detailed reasons above stay inside the engine:
    /// telling "fewer than k" apart from "a neighbouring group is small" would itself say something about the counts.
    /// </summary>
    public const string Suppressed = "suppressed";
}

/// <summary>Whether to show a view, and why not. <see cref="Count"/> is for the engine; never render it when hidden.</summary>
public sealed record SuppressionDecision(bool IsVisible, int Count, string? Reason)
{
    public static SuppressionDecision Visible(int n) => new(true, n, null);
    public static SuppressionDecision Hidden(int n, string reason) => new(false, n, reason);
}

public sealed record CellDecision(string Value, SuppressionDecision Decision);
