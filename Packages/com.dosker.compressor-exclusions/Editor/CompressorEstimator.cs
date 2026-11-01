using System;
using System.Collections.Generic;
using System.Linq;

namespace CompressorExclusions {
    internal struct EstimatorParam {
        public string name;
        public bool isBool;
        public int control;
        public bool addDriven;
        public bool nonStandardEncoding;
    }

    internal class EstimateResult {
        public bool needsCompression;
        public bool fits;
        public HashSet<string> compressed = new HashSet<string>();
        public Dictionary<string, int> batchOf = new Dictionary<string, int>();
        public int numberSlots;
        public int boolSlots;
        public int batches;
        public int finalCost;
        public string attempt = "";
        public float MinDelay => batches * (BatchTime + (1 / 30f) * 0.5f);
        public float MaxDelay => MinDelay * 2;
        public const float BatchTime = 0.1f;
    }

    /**
     * What a build is expected to do. Saved in the snapshot before the solver runs, so it can be compared with
     * the real result afterwards. Names are normalized, because VRCFury's prefix changes between builds.
     */
    [Serializable]
    internal class Prediction {
        public bool available;
        // Built from the avatar in the scene, without the parameters VRCFury only creates during a build
        public bool partial;
        public int compressed;
        public int protectedCount;
        public int batches;
        public int cost;
        public bool fits;
        public float minDelay;
        public float maxDelay;
        public List<string> compressedNames = new List<string>();

        public static Prediction From(EstimateResult estimate, int protectedCount, Func<string, string> normalize, bool partial = false) {
            return new Prediction {
                available = true,
                partial = partial,
                compressed = estimate.compressed.Count,
                protectedCount = protectedCount,
                batches = estimate.batches,
                cost = estimate.finalCost,
                fits = estimate.fits,
                minDelay = estimate.batches > 0 ? estimate.MinDelay : 0,
                maxDelay = estimate.batches > 0 ? estimate.MaxDelay : 0,
                compressedNames = estimate.compressed.Select(normalize).OrderBy(name => name).ToList()
            };
        }
    }

    internal enum ForecastOutcome {
        NoPrediction,
        Match,
        // The saved prediction used the previous build's parameters, and those changed. The solver copy is fine.
        ParametersChanged,
        // Even with the build's own parameters the result differs, so CompressorEstimator needs an update
        SolverDiverged
    }

    internal class ForecastReport {
        public ForecastOutcome outcome;
        public List<string> unexpected = new List<string>();
        public List<string> notCompressed = new List<string>();
    }

    /**
     * A copy of the selection loop in VRCFury's ParameterCompressorSolverService and of OptimizationDecision.Optimize,
     * so the window can show the effect of a choice without running a build.
     * This is only ever an estimate for display. The real build always uses VRCFury's own solver.
     * Has no Unity dependencies, so it can be tested outside of the editor.
     */
    internal static class CompressorEstimator {
        // Values of VRCExpressionsMenu.Control.ControlType
        public const int Toggle = 102;
        public const int TwoAxis = 201;
        public const int FourAxis = 202;
        public const int Radial = 203;

        private static readonly (string label, int[] controls)[] Attempts = {
            ("Radial", new[] { Radial }),
            ("Toggle", new[] { Toggle }),
            ("Radial + Toggle", new[] { Radial, Toggle }),
            ("Puppets", new[] { TwoAxis, FourAxis }),
            ("Radial + Puppets", new[] { Radial, TwoAxis, FourAxis }),
            ("Toggle + Puppets", new[] { Toggle, TwoAxis, FourAxis }),
            ("Radial + Toggle + Puppets", new[] { Radial, Toggle, TwoAxis, FourAxis }),
        };

        /**
         * candidates must already be limited to synced parameters which are not protected
         */
        public static EstimateResult Solve(IList<EstimatorParam> candidates, int originalCost, int maxCost) {
            var output = new EstimateResult { finalCost = originalCost, fits = true };
            if (originalCost <= maxCost) return output;

            output.needsCompression = true;
            output.fits = false;
            var bestCost = originalCost;
            var bestWasSuccess = false;
            var bestTime = 0f;
            foreach (var (label, controls) in Attempts) {
                var eligible = candidates
                    .Where(p => !p.nonStandardEncoding)
                    .Where(p => controls.Contains(p.control))
                    .Where(p => !p.addDriven || controls.Contains(FourAxis))
                    .ToList();
                var decision = new Decision(eligible, originalCost, maxCost);
                decision.Optimize();
                var cost = decision.FinalCost();
                var batches = decision.Batches();
                var syncTime = batches * EstimateResult.BatchTime;
                if (bestWasSuccess) {
                    if (syncTime > bestTime / 2) continue;
                    if (cost > maxCost) continue;
                } else {
                    if (cost >= bestCost) continue;
                }

                bestCost = cost;
                bestWasSuccess = cost <= maxCost;
                bestTime = syncTime;
                output.fits = bestWasSuccess;
                output.finalCost = cost;
                output.batches = batches;
                output.numberSlots = decision.numberSlots;
                output.boolSlots = decision.boolSlots;
                output.attempt = label;
                output.compressed = new HashSet<string>(eligible.Select(p => p.name));
                output.batchOf = BatchesOf(eligible, decision);

                if (bestWasSuccess && syncTime <= 1) break;
            }
            return output;
        }

        // Same order as OptimizationDecision.GetBatches: numbers and bools are chunked separately
        private static Dictionary<string, int> BatchesOf(IEnumerable<EstimatorParam> eligible, Decision decision) {
            var output = new Dictionary<string, int>();
            var numbers = 0;
            var bools = 0;
            foreach (var param in eligible) {
                output[param.name] = param.isBool ? bools++ / decision.boolSlots : numbers++ / decision.numberSlots;
            }
            return output;
        }

        /**
         * The smallest set of protected parameters to release so that the avatar fits, or null if it can't.
         * Numbers come first, because each one saves 8 bits against 1 for a Bool.
         */
        public static List<string> SuggestRelease(
            IList<EstimatorParam> candidates,
            IList<EstimatorParam> protectedParams,
            int originalCost,
            int maxCost
        ) {
            if (Solve(candidates, originalCost, maxCost).fits) return new List<string>();

            var compressible = Attempts.SelectMany(attempt => attempt.controls).ToHashSet();
            var released = new List<EstimatorParam>();
            var fits = false;
            foreach (var param in protectedParams.Where(p => !p.nonStandardEncoding && compressible.Contains(p.control)).OrderBy(p => p.isBool)) {
                released.Add(param);
                if (Solve(candidates.Concat(released).ToList(), originalCost, maxCost).fits) {
                    fits = true;
                    break;
                }
            }
            if (!fits) return null;

            foreach (var param in released.ToList()) {
                var without = released.Where(other => other.name != param.name);
                if (Solve(candidates.Concat(without).ToList(), originalCost, maxCost).fits) {
                    released.Remove(param);
                }
            }
            return released.Select(p => p.name).ToList();
        }

        /**
         * before: what was predicted with the previous build's parameters. Can be missing on a first build.
         * atBuild: what was predicted with this build's own parameters.
         */
        public static ForecastReport Compare(
            Prediction before,
            Prediction atBuild,
            IEnumerable<string> actualNames,
            int actualBatches,
            int actualCost,
            bool actualFits
        ) {
            var actual = new HashSet<string>(actualNames);
            bool Matches(Prediction prediction) {
                // When it doesn't fit, VRCFury gives up, so there is no compressed list to compare
                if (!prediction.fits && !actualFits) return true;
                return prediction.fits == actualFits
                       && prediction.batches == actualBatches
                       && prediction.cost == actualCost
                       && actual.SetEquals(prediction.compressedNames);
            }

            var report = new ForecastReport();
            if (!before.available && !atBuild.available) {
                report.outcome = ForecastOutcome.NoPrediction;
                return report;
            }

            var shown = before.available ? before : atBuild;
            report.unexpected = actual.Except(shown.compressedNames).OrderBy(name => name).ToList();
            report.notCompressed = shown.compressedNames.Except(actual).OrderBy(name => name).ToList();

            if (atBuild.available && !Matches(atBuild)) report.outcome = ForecastOutcome.SolverDiverged;
            else if (Matches(shown)) report.outcome = ForecastOutcome.Match;
            else report.outcome = atBuild.available ? ForecastOutcome.ParametersChanged : ForecastOutcome.SolverDiverged;
            return report;
        }

        private class Decision {
            private readonly int originalCost;
            private readonly int maxCost;
            private readonly int boolCount;
            private readonly int numberCount;
            public int numberSlots;
            public int boolSlots;

            public Decision(ICollection<EstimatorParam> compress, int originalCost, int maxCost) {
                this.originalCost = originalCost;
                this.maxCost = maxCost;
                boolCount = compress.Count(p => p.isBool);
                numberCount = compress.Count - boolCount;
            }

            private static int Ceil(int count, int slots) {
                return slots == 0 ? 0 : (int)Math.Ceiling(count / (double)slots);
            }

            private static int IndexBits(int batchCount) {
                var maxSyncId = batchCount + 1;
                var bits = 1;
                while ((1 << bits) < maxSyncId) bits++;
                return bits;
            }

            private int Batches(int numbers, int bools) {
                return Math.Max(Ceil(numberCount, numbers), Ceil(boolCount, bools));
            }

            private int Cost(int numbers, int bools) {
                return originalCost
                       + IndexBits(Batches(numbers, bools))
                       + numbers * 8
                       + bools
                       - numberCount * 8
                       - boolCount;
            }

            public int Batches() => Batches(numberSlots, boolSlots);
            public int FinalCost() => Cost(numberSlots, boolSlots);

            public void Optimize() {
                boolSlots = boolCount > 0 ? 1 : 0;
                numberSlots = numberCount > 0 ? 1 : 0;
                while (true) {
                    if (numberSlots < numberCount
                        && Cost(numberSlots + 1, boolSlots) <= maxCost
                        && (boolCount == 0 || (float)numberSlots / numberCount < (float)boolSlots / boolCount)
                    ) {
                        numberSlots++;
                    } else if (boolSlots < boolCount
                               && Cost(numberSlots, boolSlots + 1) <= maxCost
                    ) {
                        boolSlots++;
                    } else {
                        break;
                    }
                }
            }
        }
    }
}
