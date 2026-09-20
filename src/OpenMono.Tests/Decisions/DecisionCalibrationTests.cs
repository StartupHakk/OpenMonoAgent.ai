using FluentAssertions;
using OpenMono.Decisions;

namespace OpenMono.Tests.Decisions;

/// <summary>
/// Phase 3 calibration tests (Kinza section 9.3). The heuristic backend is
/// exercised as the miscalibrated control: this test measures Brier/ECE
/// over the corpus, publishes docs/decision-calibration.md, and guards
/// against ECE regression. It does not tune anything.
/// </summary>
public class DecisionCalibrationTests
{
    /// <summary>
    /// ECE regression ceiling. Recorded heuristic baseline (corpus v1):
    /// Brier 0.3410, ECE 0.3467 — visibly miscalibrated, worse than a
    /// constant-0.5 predictor (Brier 0.25). That gap IS the point: this is
    /// the control number to beat. The ceiling is baseline + 0.05 tolerance.
    /// This is a tripwire, not a calibration target: any change that makes
    /// target: any change that makes the heuristic (or a future backend
    /// measured here) substantially worse must fail loudly. Loosened only
    /// with a corpus-version bump and a written reason, never to make a
    /// red build green.
    /// </summary>
    private const double EceRegressionCeiling = 0.40;

    private static DecisionOptions HeuristicOptions() =>
        new(false, 0.85, 0.6, 0.5, 64);

    [Fact]
    public void Corpus_Loads_With_Versioned_Sized_Labeled_Triples()
    {
        var corpus = DecisionCalibration.LoadCorpus();

        corpus.Entries.Should().HaveCountGreaterThanOrEqualTo(50);
        corpus.Entries.Should().HaveCountLessThanOrEqualTo(200);
        corpus.Entries.Select(e => e.Id).Should().OnlyHaveUniqueItems();
        corpus.Entries.Select(e => e.Kind).Should().OnlyContain(k =>
            k == "choice" || k == "noul" || k == "verify");
        corpus.Entries.Should().Contain(e => e.Kind == "choice");
        corpus.Entries.Should().Contain(e => e.Kind == "noul");
        corpus.Entries.Should().Contain(e => e.Kind == "verify");
    }

    [Fact]
    public void BrierScore_Perfect_Predictor_Is_Zero()
    {
        var pairs = new List<(double P, int O)> { (1.0, 1), (0.0, 0), (0.9, 1), (0.1, 0) };
        DecisionCalibration.BrierScore(pairs).Should().BeLessThan(0.02);
    }

    [Fact]
    public void BrierScore_Constant_Half_On_Balanced_Is_Quarter()
    {
        var pairs = new List<(double P, int O)> { (0.5, 1), (0.5, 0) };
        DecisionCalibration.BrierScore(pairs).Should().BeApproximately(0.25, 1e-9);
    }

    [Fact]
    public void Ece_Perfectly_Calibrated_Is_Zero()
    {
        var pairs = new List<(double P, int O)>
        {
            (0.0, 0), (0.0, 0), (1.0, 1), (1.0, 1),
        };
        var (ece, _) = DecisionCalibration.ExpectedCalibrationError(pairs);
        ece.Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void Heuristic_Is_Measured_As_Miscalibrated_Control_And_Report_Is_Published()
    {
        var backend = new HeuristicBackend(HeuristicOptions());
        var corpus = DecisionCalibration.LoadCorpus();
        var result = DecisionCalibration.Calibrate(backend, corpus);

        result.Total.Should().Be(corpus.Entries.Count);
        result.Brier.Should().BeInRange(0, 1);
        result.Ece.Should().BeGreaterThanOrEqualTo(0);
        // The control must actually show miscalibration: a perfectly
        // calibrated backend would score near zero, and the heuristic is
        // token-overlap confidence dressed as probability.
        result.Ece.Should().BeGreaterThan(0.02,
            "the heuristic control is expected to be visibly miscalibrated — near-zero ECE would mean the corpus cannot discriminate");
        // Regression tripwire (see EceRegressionCeiling comment).
        result.Ece.Should().BeLessThanOrEqualTo(EceRegressionCeiling,
            "ECE regressed past the recorded baseline + tolerance");

        var md = DecisionCalibration.RenderMarkdown(result, "local-heuristic (miscalibrated control)");
        md.Should().Contain("Brier score:").And.Contain("ECE (10 bins):");

        var path = Path.Combine(FindRepoRoot(), "docs", "decision-calibration.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, md);

        File.Exists(path).Should().BeTrue("the reliability artifact must be asserted present");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OpenMono.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root (OpenMono.sln).");
    }
}
