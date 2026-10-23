namespace Rag.Evaluation.Tuning;

public sealed record BootstrapResult(double MeanDifference, double Lower, double Upper, int Samples);

/// <summary>
/// Paired bootstrap of the mean per-query difference (b − a). Resampling queries with replacement shows how much the
/// observed difference could move if a different set of similar queries had been drawn. The seed is fixed so a report is reproducible.
/// </summary>
public static class PairedBootstrap
{
    public const int DefaultResamples = 10_000;
    public const int DefaultSeed = 20261024;

    public static BootstrapResult MeanDifference(
        IReadOnlyList<double> a,
        IReadOnlyList<double> b,
        int resamples = DefaultResamples,
        int seed = DefaultSeed,
        double confidence = 0.95)
    {
        if (a.Count != b.Count || a.Count == 0)
        {
            throw new ArgumentException("Both series must be non-empty and the same length.");
        }

        var differences = a.Select((value, i) => b[i] - value).ToArray();
        var random = new Random(seed);
        var means = new double[resamples];
        for (var r = 0; r < resamples; r++)
        {
            double sum = 0;
            for (var i = 0; i < differences.Length; i++)
            {
                sum += differences[random.Next(differences.Length)];
            }

            means[r] = sum / differences.Length;
        }

        Array.Sort(means);
        var alpha = 1 - confidence;
        var lower = means[(int)Math.Floor(alpha / 2 * (resamples - 1))];
        var upper = means[(int)Math.Ceiling((1 - alpha / 2) * (resamples - 1))];
        return new BootstrapResult(differences.Average(), lower, upper, differences.Length);
    }
}
