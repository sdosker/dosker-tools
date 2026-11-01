using System;
using System.Collections.Generic;
using System.Linq;
using DoskerTools.PerformanceBudget;

namespace CompressorExclusions {
    /**
     * Expected results come from the Vernid avatar: 362 bits, 256 max, 13 radials and 41 toggle parameters,
     * of which 4 are numbers. No test framework, so that this only needs the dotnet SDK.
     */
    internal static class Program {
        private const int OriginalCost = 362;
        private const int MaxCost = 256;

        private static int failures;

        private static int Main() {
            var radials = Params("radial", 13, false, CompressorEstimator.Radial);
            var toggleNumbers = Params("toggleNumber", 4, false, CompressorEstimator.Toggle);
            var toggleBools = Params("toggleBool", 37, true, CompressorEstimator.Toggle);

            Check("Nothing protected", radials.Concat(toggleNumbers).Concat(toggleBools),
                compressed: 54, numbers: 6, bools: 17, batches: 3, cost: 256, fits: true);
            Check("All Bools protected", radials.Concat(toggleNumbers),
                compressed: 17, numbers: 3, bools: 0, batches: 6, cost: 253, fits: true);
            Check("All Bools and one number protected", radials.Concat(toggleNumbers.Skip(1)),
                compressed: 16, numbers: 2, bools: 0, batches: 8, cost: 254, fits: true);
            Check("Toggle control protected", radials,
                compressed: 13, numbers: 1, bools: 0, batches: 13, cost: 270, fits: false);

            var nonStandard = toggleNumbers.Select(param => {
                param.nonStandardEncoding = true;
                return param;
            });
            Check("Non-standard encodings are never eligible", radials.Concat(nonStandard),
                compressed: 13, numbers: 1, bools: 0, batches: 13, cost: 270, fits: false);

            var driven = radials.Select(param => {
                param.addDriven = true;
                return param;
            });
            var drivenResult = CompressorEstimator.Solve(driven.ToList(), OriginalCost, MaxCost);
            Expect("Add-driven parameters need an attempt with puppets",
                drivenResult.attempt == "Radial + Puppets" && drivenResult.compressed.Count == 13, drivenResult.attempt);

            var fits = CompressorEstimator.Solve(radials, MaxCost, MaxCost);
            Expect("Fitting avatars aren't compressed", fits.fits && !fits.needsCompression);

            CheckBatches(radials.Concat(toggleNumbers).ToList());
            CheckSuggestions(radials, toggleNumbers, toggleBools);
            CheckForecast();
            CheckBudget();

            Console.WriteLine(failures == 0 ? "All estimator tests passed" : $"{failures} estimator tests failed");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckBatches(List<EstimatorParam> candidates) {
            var result = CompressorEstimator.Solve(candidates, OriginalCost, MaxCost);
            Expect("Batches follow the slots", result.batchOf.Count == 17
                                               && result.batchOf.Values.Max() == result.batches - 1
                                               && result.batchOf.Values.GroupBy(batch => batch).All(group => group.Count() <= result.numberSlots));
        }

        private static void CheckSuggestions(List<EstimatorParam> radials, List<EstimatorParam> numbers, List<EstimatorParam> bools) {
            var protectedToggles = numbers.Concat(bools).ToList();
            var release = CompressorEstimator.SuggestRelease(radials, protectedToggles, OriginalCost, MaxCost);
            var fixedResult = CompressorEstimator.Solve(
                radials.Concat(protectedToggles.Where(param => release.Contains(param.name))).ToList(), OriginalCost, MaxCost);
            Expect("Suggestion makes the avatar fit", fixedResult.fits, $"{release.Count} released");
            Expect("Suggestion prefers numbers", release.All(name => name.StartsWith("toggleNumber")), string.Join(",", release));

            var enough = radials.Concat(numbers).ToList();
            Expect("Nothing to release when it fits",
                CompressorEstimator.SuggestRelease(enough, bools, OriginalCost, MaxCost).Count == 0);
            var unavailable = radials.Select(param => {
                param.control = -1;
                return param;
            }).ToList();
            Expect("No suggestion when it can't fit",
                CompressorEstimator.SuggestRelease(new List<EstimatorParam>(), unavailable, OriginalCost, MaxCost) == null);
        }

        private static Prediction Predicted(bool fits, int batches, int cost, params string[] names) {
            return new Prediction { available = true, fits = fits, batches = batches, cost = cost, compressedNames = names.ToList() };
        }

        private static void CheckForecast() {
            var none = new Prediction();
            var names = new[] { "a", "b" };
            var right = Predicted(true, 2, 250, names);
            var stale = Predicted(true, 3, 252, "a", "c");

            Expect("Forecast without predictions",
                CompressorEstimator.Compare(none, none, names, 2, 250, true).outcome == ForecastOutcome.NoPrediction);
            Expect("Forecast matches",
                CompressorEstimator.Compare(right, right, names, 2, 250, true).outcome == ForecastOutcome.Match);
            Expect("First build compares the prediction made at build time",
                CompressorEstimator.Compare(none, right, names, 2, 250, true).outcome == ForecastOutcome.Match);

            var changed = CompressorEstimator.Compare(stale, right, names, 2, 250, true);
            Expect("Changed parameters aren't blamed on the solver", changed.outcome == ForecastOutcome.ParametersChanged);
            Expect("Differences are listed", changed.unexpected.SequenceEqual(new[] { "b" })
                                             && changed.notCompressed.SequenceEqual(new[] { "c" }));

            Expect("Solver divergence is detected",
                CompressorEstimator.Compare(right, right, new[] { "a" }, 1, 240, true).outcome == ForecastOutcome.SolverDiverged);
            Expect("Not fitting matches a prediction that doesn't fit",
                CompressorEstimator.Compare(Predicted(false, 9, 270), Predicted(false, 9, 270), new string[0], 0, 362, false).outcome
                == ForecastOutcome.Match);
            Expect("Fitting when predicted otherwise is a divergence",
                CompressorEstimator.Compare(Predicted(false, 9, 270), Predicted(false, 9, 270), names, 2, 250, true).outcome
                == ForecastOutcome.SolverDiverged);
        }

        private static BudgetMeasurement Measured(float polys, float bones) {
            var output = new BudgetMeasurement();
            output.Set("PolyCount", polys);
            output.Set("BoneCount", bones);
            return output;
        }

        private static void CheckBudget() {
            var calibration = new BudgetCalibration {
                componentsFingerprint = "a",
                before = Measured(1000, 100),
                after = Measured(800, 150)
            };

            var scene = Measured(1200, 100);
            var predicted = BudgetMath.Predict(scene, calibration);
            Expect("Budget adds what the last build changed", predicted.Get("PolyCount") == 1000 && predicted.Get("BoneCount") == 150);
            Expect("Budget never goes below zero", BudgetMath.Predict(Measured(100, 0), calibration).Get("PolyCount") == 0);
            Expect("Budget without a build equals the scene", BudgetMath.Predict(scene, null).Get("PolyCount") == 1200);
            Expect("Budget confidence", BudgetMath.ConfidenceOf(null, "a") == BudgetConfidence.SceneOnly
                                        && BudgetMath.ConfidenceOf(calibration, "a") == BudgetConfidence.Calibrated
                                        && BudgetMath.ConfidenceOf(calibration, "b") == BudgetConfidence.Stale);
        }

        private static List<EstimatorParam> Params(string prefix, int count, bool isBool, int control) {
            return Enumerable.Range(0, count).Select(index => new EstimatorParam {
                name = prefix + index,
                isBool = isBool,
                control = control
            }).ToList();
        }

        private static void Check(
            string name,
            IEnumerable<EstimatorParam> candidates,
            int compressed,
            int numbers,
            int bools,
            int batches,
            int cost,
            bool fits
        ) {
            var result = CompressorEstimator.Solve(candidates.ToList(), OriginalCost, MaxCost);
            Expect(name, result.compressed.Count == compressed
                         && result.numberSlots == numbers
                         && result.boolSlots == bools
                         && result.batches == batches
                         && result.finalCost == cost
                         && result.fits == fits,
                $"compressed {result.compressed.Count}, {result.numberSlots} numbers, {result.boolSlots} bools, "
                + $"{result.batches} batches, cost {result.finalCost}, fits {result.fits}");
        }

        private static void Expect(string name, bool passed, string detail = "") {
            Console.WriteLine($"{(passed ? "ok  " : "FAIL")} {name} {(passed ? "" : detail)}");
            if (!passed) failures++;
        }
    }
}
