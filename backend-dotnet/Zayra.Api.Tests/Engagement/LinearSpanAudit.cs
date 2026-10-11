using Zayra.Api.Infrastructure.Privacy;

namespace Zayra.Api.Tests.Engagement;

/// <summary>
/// The LINEAR-SPAN AUDIT — the test oracle for <see cref="CohortViewFamily"/>'s aggregate mode.
///
/// <para>Atomic cells are the groups of people who share a value on EVERY dimension of the family (scope dimensions
/// included). Every view is a union of cells, so it is a 0/1 row over the cells, and every number a reader can obtain
/// — any count, sum or distribution of any visible view, plus any scoped view (a sum of visible views) — is that row
/// applied to the per-cell totals. A reader who combines results linearly can therefore learn the total of a group G
/// exactly when G's indicator lies in the row space of the visible views. The audit asserts that no group of 1..k−1
/// people (any union of atomic cells) has its indicator in that row space.</para>
///
/// <para>Method: v is in the row space of M iff N·v = 0 for a basis N of M's right kernel. The kernel is computed by
/// exact elimination modulo the Mersenne prime 2^61−1 (rational arithmetic agrees except when the prime divides one of
/// the matrix's minors — for 0/1 matrices of this size, with negligible probability). Each cell c gets the column
/// N·e_c; a union is in the row space iff its cells' columns sum to zero. Unions of up to k−1 small cells are found as
/// two disjoint halves of at most ⌈(k−1)/2⌉ cells whose columns cancel (meet in the middle).</para>
/// </summary>
internal static class LinearSpanAudit
{
    private const ulong P = (1UL << 61) - 1;

    private static ulong Mul(ulong a, ulong b)
    {
        var r = (UInt128)a * b;
        var lo = (ulong)(r & P) + (ulong)(r >> 61);
        return lo >= P ? lo - P : lo;
    }

    private static ulong Add(ulong a, ulong b) { var s = a + b; return s >= P ? s - P : s; }
    private static ulong Neg(ulong a) => a == 0 ? 0 : P - a;

    private static ulong Pow(ulong a, ulong e)
    {
        ulong r = 1;
        while (e > 0) { if ((e & 1) == 1) r = Mul(r, a); a = Mul(a, a); e >>= 1; }
        return r;
    }

    private static ulong Inv(ulong a) => Pow(a, P - 2);

    /// <summary>The atomic cell of each record, and each cell's size.</summary>
    public static (int[] CellOf, int[] Sizes) Cells(IReadOnlyList<CohortRecord> records, IReadOnlyList<string> dimensions)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var cellOf = new int[records.Count];
        var sizes = new List<int>();
        for (var i = 0; i < records.Count; i++)
        {
            var key = string.Join("\u001f", dimensions.Select(d => records[i].ValueOf(d)));
            if (!index.TryGetValue(key, out var c)) { index[key] = c = sizes.Count; sizes.Add(0); }
            cellOf[i] = c;
            sizes[c]++;
        }
        return (cellOf, sizes.ToArray());
    }

    /// <summary>
    /// The first group of 1..k−1 people whose total a reader can derive from <paramref name="rows"/> (each a set of
    /// record indices: a visible view), described as its cells; null when there is none.
    /// </summary>
    public static string? FindLeak(IReadOnlyList<CohortRecord> records, IReadOnlyList<string> dimensions, int k, IEnumerable<IReadOnlyCollection<int>> rows)
    {
        var (cellOf, sizes) = Cells(records, dimensions);
        var cells = sizes.Length;
        // Rows over cells (a view holds whole cells, so marking any member's cell is enough).
        var matrix = rows.Select(members =>
        {
            var row = new ulong[cells];
            foreach (var m in members) row[cellOf[m]] = 1;
            return row;
        }).ToList();

        // Reduced row echelon form modulo P.
        var pivots = new List<int>();
        var rank = 0;
        for (var col = 0; col < cells && rank < matrix.Count; col++)
        {
            var pivot = -1;
            for (var r = rank; r < matrix.Count; r++) if (matrix[r][col] != 0) { pivot = r; break; }
            if (pivot < 0) continue;
            (matrix[rank], matrix[pivot]) = (matrix[pivot], matrix[rank]);
            var inv = Inv(matrix[rank][col]);
            for (var c = col; c < cells; c++) matrix[rank][c] = Mul(matrix[rank][c], inv);
            for (var r = 0; r < matrix.Count; r++)
            {
                if (r == rank || matrix[r][col] == 0) continue;
                var factor = matrix[r][col];
                for (var c = col; c < cells; c++)
                    if (matrix[rank][c] != 0) matrix[r][c] = Add(matrix[r][c], Neg(Mul(factor, matrix[rank][c])));
            }
            pivots.Add(col);
            rank++;
        }
        var pivotRow = new Dictionary<int, int>();
        for (var r = 0; r < pivots.Count; r++) pivotRow[pivots[r]] = r;
        var free = Enumerable.Range(0, cells).Where(c => !pivotRow.ContainsKey(c)).ToArray();

        // Kernel column of each cell: free cell j → e_j; pivot cell of row r → (−R[r][f]) over the free columns.
        ulong[] Column(int c)
        {
            var v = new ulong[free.Length];
            if (pivotRow.TryGetValue(c, out var r)) { for (var j = 0; j < free.Length; j++) v[j] = Neg(matrix[r][free[j]]); }
            else v[Array.IndexOf(free, c)] = 1;
            return v;
        }

        var small = Enumerable.Range(0, cells).Where(c => sizes[c] < k).OrderBy(c => sizes[c]).ToArray();
        var columns = small.ToDictionary(c => c, Column);
        var half = (k - 1 + 1) / 2;

        // Every subset of ≤ half small cells with fewer than k people, with its column sum.
        var halves = new List<(int[] Cells, int Size, ulong[] Sum)> { (Array.Empty<int>(), 0, new ulong[free.Length]) };
        void Grow(int start, List<int> chosen, int size, ulong[] sum)
        {
            for (var i = start; i < small.Length; i++)
            {
                var c = small[i];
                if (size + sizes[c] >= k) break;
                var next = new ulong[sum.Length];
                for (var j = 0; j < sum.Length; j++) next[j] = Add(sum[j], columns[c][j]);
                chosen.Add(c);
                halves.Add((chosen.ToArray(), size + sizes[c], next));
                if (chosen.Count < half) Grow(i + 1, chosen, size + sizes[c], next);
                chosen.RemoveAt(chosen.Count - 1);
            }
        }
        Grow(0, new List<int>(), 0, new ulong[free.Length]);

        string Key(ulong[] v) => string.Join(",", v);
        var byNegation = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var h = 0; h < halves.Count; h++)
        {
            var key = Key(halves[h].Sum.Select(Neg).ToArray());
            if (!byNegation.TryGetValue(key, out var list)) byNegation[key] = list = new List<int>();
            list.Add(h);
        }
        for (var h = 1; h < halves.Count; h++)
        {
            if (!byNegation.TryGetValue(Key(halves[h].Sum), out var partners)) continue;
            foreach (var p in partners)
            {
                var other = halves[p];
                if (halves[h].Size + other.Size >= k || other.Cells.Intersect(halves[h].Cells).Any()) continue;
                var union = halves[h].Cells.Concat(other.Cells).ToArray();
                var size = halves[h].Size + other.Size;
                var example = union.Select(c => string.Join("/", dimensions.Select(d => records[Array.IndexOf(cellOf, c)].ValueOf(d))));
                return $"a group of {size} (cells {string.Join(" + ", example)}) is a linear combination of visible views";
            }
        }
        return null;
    }

    /// <summary>The audit of a family: its visible views plus any extra rows (scoped views a reader obtained).</summary>
    public static string? FindLeak(CohortViewFamily family, IEnumerable<IReadOnlyCollection<int>>? extraRows = null) =>
        FindLeak(family.Records, family.Dimensions, family.K,
            family.Views().Where(v => v.Visible).Select(v => v.Members).Concat(extraRows ?? []));

    /// <summary>
    /// The SET audit for lists (comments): every atom of the Boolean algebra the rows generate — people grouped by
    /// exactly which rows they are in — has 0 or ≥ k people. Null when it holds.
    /// </summary>
    public static string? FindSetLeak(int records, int k, IEnumerable<IReadOnlyCollection<int>> rows)
    {
        var rowList = rows.Select(r => r.ToHashSet()).ToList();
        var atoms = Enumerable.Range(0, records)
            .GroupBy(i => string.Join(",", Enumerable.Range(0, rowList.Count).Where(r => rowList[r].Contains(i))))
            .Where(g => g.Key.Length > 0 && g.Count() < k).ToList(); // people in no list cannot be singled out
        return atoms.Count == 0 ? null : $"{atoms.Count} groups of fewer than {k} people can be singled out by combining the lists, e.g. one of {atoms[0].Count()}";
    }
}
