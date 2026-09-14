using System;
using RimWorld;
using Verse;

namespace BuyerInfoCard
{
    internal static class DisplayLabelUtility
    {
        internal static bool TryGetDefLabel(Def def, out string label)
        {
            label = null;
            if (def == null)
            {
                return false;
            }

            try
            {
                label = def.LabelCap.ToString();
            }
            catch (Exception exception)
            {
                Log.WarningOnce(
                    "[Buyer Info Card] Could not read the display label for "
                    + (def.defName ?? "<unnamed>") + ": " + exception.Message,
                    Gen.HashCombineInt(0x4249434C, def.shortHash));
                label = null;
            }
            return !string.IsNullOrWhiteSpace(label);
        }

        internal static string GetDefLabel(Def def, string fallback = "?")
        {
            string label;
            if (TryGetDefLabel(def, out label))
            {
                return label;
            }
            return def != null && !string.IsNullOrEmpty(def.defName)
                ? def.defName
                : fallback;
        }
    }
}
