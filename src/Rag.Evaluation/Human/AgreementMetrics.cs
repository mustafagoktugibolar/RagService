namespace Rag.Evaluation.Human;

public sealed record KappaInterval(double Observed, double Lower, double Upper);

/// <summary>
/// Inter-annotator agreement for graded relevance. Grades are integers 0..k−1. Kappa is <c>null</c> (undefined) when the raters'
/// marginals make chance agreement certain — e.g. both gave every item the same grade — because "perfect agreement" there carries no information.
/// </summary>
public static class AgreementMetrics
{
    /// <summary>Cohen's kappa with quadratic weights: w(i,j) = 1 − ((i−j)/(k−1))². Large disagreements cost far more than adjacent ones.</summary>
    public static double? WeightedKappa(IReadOnlyList<int> a, IReadOnlyList<int> b, int grades) =>
        Kappa(a, b, grades, (i, j) => 1.0 - Math.Pow((i - j) / (double)(grades - 1), 2));

    /// <summary>Unweighted Cohen's kappa: only exact agreement counts.</summary>
    public static double? Kappa(IReadOnlyList<int> a, IReadOnlyList<int> b, int grades) =>
        Kappa(a, b, grades, (i, j) => i == j ? 1.0 : 0.0);

    /// <summary>Kappa on "relevant or not" at a grade threshold (default practice here: grade ≥ 2 is relevant).</summary>
    public static double? BinaryKappa(IReadOnlyList<int> a, IReadOnlyList<int> b, int threshold)
    {
        Validate(a, b, int.MaxValue);
        return Kappa(a.Select(x => x >= threshold ? 1 : 0).ToArray(), b.Select(x => x >= threshold ? 1 : 0).ToArray(), 2, (i, j) => i == j ? 1.0 : 0.0);
    }

    public static double ExactAgreement(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        Validate(a, b, int.MaxValue);
        return a.Zip(b).Count(p => p.First == p.Second) / (double)a.Count;
    }

    /// <summary>Fraction of items whose grades differ by at most one step.</summary>
    public static double AdjacentAgreement(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        Validate(a, b, int.MaxValue);
        return a.Zip(b).Count(p => Math.Abs(p.First - p.Second) <= 1) / (double)a.Count;
    }

    /// <summary>Counts of (grade given by A, grade given by B).</summary>
    public static int[,] Confusion(IReadOnlyList<int> a, IReadOnlyList<int> b, int grades)
    {
        Validate(a, b, grades);
        var matrix = new int[grades, grades];
        for (var i = 0; i < a.Count; i++)
        {
            matrix[a[i], b[i]]++;
        }

        return matrix;
    }

    /// <summary>
    /// Percentile bootstrap of weighted kappa that resamples whole <b>queries</b> (the items of one query are not independent),
    /// so a small pilot gets an honestly wide interval.
    /// </summary>
    public static KappaInterval WeightedKappaInterval(
        IReadOnlyList<string> queryIds, IReadOnlyList<int> a, IReadOnlyList<int> b, int grades, int resamples = 10_000, int seed = 20261024)
    {
        Validate(a, b, grades);
        if (queryIds.Count != a.Count)
        {
            throw new ArgumentException("queryIds must align with the grades.");
        }

        var observed = WeightedKappa(a, b, grades) ?? double.NaN;
        var groups = Enumerable.Range(0, a.Count).GroupBy(i => queryIds[i]).Select(g => g.ToArray()).ToArray();
        var random = new Random(seed);
        var values = new List<double>(resamples);

        for (var r = 0; r < resamples; r++)
        {
            var ra = new List<int>();
            var rb = new List<int>();
            for (var g = 0; g < groups.Length; g++)
            {
                foreach (var index in groups[random.Next(groups.Length)])
                {
                    ra.Add(a[index]);
                    rb.Add(b[index]);
                }
            }

            if (WeightedKappa(ra, rb, grades) is { } value)
            {
                values.Add(value);
            }
        }

        if (values.Count == 0)
        {
            return new KappaInterval(observed, double.NaN, double.NaN);
        }

        values.Sort();
        return new KappaInterval(observed, values[(int)Math.Floor(0.025 * (values.Count - 1))], values[(int)Math.Ceiling(0.975 * (values.Count - 1))]);
    }

    private static double? Kappa(IReadOnlyList<int> a, IReadOnlyList<int> b, int grades, Func<int, int, double> weight)
    {
        Validate(a, b, grades);
        var n = a.Count;
        var observed = new double[grades, grades];
        var rowTotals = new double[grades];
        var columnTotals = new double[grades];
        for (var i = 0; i < n; i++)
        {
            observed[a[i], b[i]]++;
            rowTotals[a[i]]++;
            columnTotals[b[i]]++;
        }

        double po = 0, pe = 0;
        for (var i = 0; i < grades; i++)
        {
            for (var j = 0; j < grades; j++)
            {
                po += weight(i, j) * observed[i, j];
                pe += weight(i, j) * rowTotals[i] * columnTotals[j];
            }
        }

        po /= n;
        pe /= (double)n * n;
        return Math.Abs(1 - pe) < 1e-12 ? null : (po - pe) / (1 - pe);
    }

    private static void Validate(IReadOnlyList<int> a, IReadOnlyList<int> b, int grades)
    {
        if (a.Count != b.Count || a.Count == 0)
        {
            throw new ArgumentException("Both raters must grade the same, non-empty list of items.");
        }

        if (a.Concat(b).Any(g => g < 0 || g >= grades))
        {
            throw new ArgumentOutOfRangeException(nameof(a), $"Grades must be integers in 0..{grades - 1}.");
        }
    }
}
