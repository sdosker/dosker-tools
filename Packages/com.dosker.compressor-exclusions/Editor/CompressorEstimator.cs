using System;
using System.Collections.Generic;
using System.Linq;

namespace CompressorExclusions {
    internal struct EstimatorParam {
        public string name;
        public bool isBool;
        public int control;
        public bool addDriven;
    }

    internal class EstimateResult {
        public bool needsCompression;
        public bool fits;
        public HashSet<string> compressed = new HashSet<string>();
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

                if (bestWasSuccess && syncTime <= 1) break;
            }
            return output;
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
