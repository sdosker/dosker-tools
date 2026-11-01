using System;
using System.Collections.Generic;
using System.Linq;

namespace DoskerTools.PerformanceBudget
{
    [Serializable]
    internal class BudgetEntry
    {
        public string category;
        public float value;
    }

    // Os valores do avatar em cada categoria do ranking do VRChat.
    [Serializable]
    internal class BudgetMeasurement
    {
        public List<BudgetEntry> entries = new List<BudgetEntry>();

        public float Get(string category)
        {
            return entries.FirstOrDefault(entry => entry.category == category)?.value ?? 0f;
        }

        public void Set(string category, float value)
        {
            var entry = entries.FirstOrDefault(existing => existing.category == category);
            if (entry == null)
            {
                entries.Add(new BudgetEntry { category = category, value = value });
            }
            else
            {
                entry.value = value;
            }
        }
    }

    // O que o último build fez: o avatar antes e depois de VRCFury e outros pré-processadores.
    [Serializable]
    internal class BudgetCalibration
    {
        public string avatarName;
        public string platform;
        public string builtAt;
        public string componentsFingerprint;
        public BudgetMeasurement before = new BudgetMeasurement();
        public BudgetMeasurement after = new BudgetMeasurement();
    }

    internal enum BudgetConfidence
    {
        // Só a medição da cena: o build ainda pode mudar o ranking.
        SceneOnly,
        // Cena mais a diferença do último build.
        Calibrated,
        // Os componentes que geram conteúdo mudaram, então a diferença pode ter perdido a validade.
        Stale
    }

    // Sem dependências do Unity, para ser testado fora do Editor.
    internal static class BudgetMath
    {
        public static BudgetMeasurement Predict(BudgetMeasurement scene, BudgetCalibration calibration)
        {
            var output = new BudgetMeasurement();
            foreach (var entry in scene.entries)
            {
                var change = calibration == null ? 0f : calibration.after.Get(entry.category) - calibration.before.Get(entry.category);
                output.Set(entry.category, Math.Max(0f, entry.value + change));
            }
            return output;
        }

        public static BudgetConfidence ConfidenceOf(BudgetCalibration calibration, string componentsFingerprint)
        {
            if (calibration == null)
            {
                return BudgetConfidence.SceneOnly;
            }
            return calibration.componentsFingerprint == componentsFingerprint ? BudgetConfidence.Calibrated : BudgetConfidence.Stale;
        }
    }
}
