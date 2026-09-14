using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace BuyerInfoCard
{
    internal static class FactionGearCustomizerCompatibility
    {
        private const string ModifierTypeName = "FactionGearCustomizer.TradeStock_DefModifier";
        private const string EntryTypeName = "FactionGearCustomizer.TradeStockEntry";
        private const string ManagerTypeName = "FactionGearCustomizer.Managers.FactionDefManager";

        private static readonly HashSet<string> PendingTraderKinds =
            new HashSet<string>(StringComparer.Ordinal);

        private static MethodInfo revertAllMethod;
        private static bool rollbackBridgeEnabled;
        private static bool rollbackInProgress;
        private static bool rollbackNotificationPending;
        private static bool compatibilityNoticeWritten;

        internal static void Apply(Harmony harmony)
        {
            Type modifierType;
            try
            {
                modifierType = AccessTools.TypeByName(ModifierTypeName);
            }
            catch
            {
                return;
            }

            if (modifierType == null)
            {
                return;
            }

            try
            {
                Type entryType = AccessTools.TypeByName(EntryTypeName);
                Type managerType = AccessTools.TypeByName(ManagerTypeName);
                Type entryListType = entryType == null
                    ? null
                    : typeof(List<>).MakeGenericType(entryType);

                MethodInfo applySellMethod = entryListType == null
                    ? null
                    : FindStaticVoidMethod(
                        modifierType,
                        "ApplySellStock",
                        typeof(string),
                        entryListType,
                        typeof(bool));
                MethodInfo applyBuyMethod = entryListType == null
                    ? null
                    : FindStaticVoidMethod(
                        modifierType,
                        "ApplyBuyStock",
                        typeof(string),
                        entryListType);
                MethodInfo resolvedRevertMethod = FindStaticVoidMethod(
                    modifierType,
                    "RevertAll",
                    typeof(string));
                // RevertAll in FGC 1.7.4 clears tag bookkeeping shared by every trader.
                // It is therefore safe only at a global reset boundary, not ResetFaction.
                MethodInfo resetAllMethod = FindStaticVoidMethod(
                    managerType,
                    "ResetAllFactions");

                int applyPatchCount = 0;
                if (TryPatchPostfix(harmony, applySellMethod, nameof(TradeRulesApplied)))
                {
                    applyPatchCount++;
                }
                if (TryPatchPostfix(harmony, applyBuyMethod, nameof(TradeRulesApplied)))
                {
                    applyPatchCount++;
                }

                revertAllMethod = resolvedRevertMethod;
                bool revertPatched = TryPatchPostfix(
                    harmony,
                    resolvedRevertMethod,
                    nameof(TradeRulesReverted));
                bool resetPatched = TryPatchPostfix(
                    harmony,
                    resetAllMethod,
                    nameof(AllFactionsReset));
                rollbackBridgeEnabled = applyPatchCount > 0 && revertPatched && resetPatched;

                if (applyPatchCount > 0)
                {
                    string rollbackStatus = rollbackBridgeEnabled
                        ? " Defensive global-reset rollback enabled."
                        : " Defensive rollback unavailable; definition auditing remains active.";
                    Log.Message(
                        "[Buyer Info Card] Faction Gear Customizer trade-rule compatibility enabled."
                        + rollbackStatus);
                }
                else
                {
                    WriteCompatibilityNotice(
                        "Faction Gear Customizer API was not recognized; optional compatibility was skipped.");
                }
            }
            catch
            {
                DisableRollbackBridge(
                    "Faction Gear Customizer optional compatibility could not be initialized; continuing without it.");
            }
        }

        internal static void OnDefinitionsReloaded()
        {
            PendingTraderKinds.Clear();
            rollbackNotificationPending = false;
        }

        private static MethodInfo FindStaticVoidMethod(
            Type declaringType,
            string methodName,
            params Type[] parameterTypes)
        {
            if (declaringType == null)
            {
                return null;
            }

            // Exact matching is intentional: API drift disables this optional bridge
            // instead of asking Harmony to patch a method with an incompatible shape.
            MethodInfo method = declaringType.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null,
                parameterTypes,
                null);
            return method != null && method.ReturnType == typeof(void) ? method : null;
        }

        private static bool TryPatchPostfix(
            Harmony harmony,
            MethodInfo target,
            string postfixName)
        {
            if (harmony == null || target == null)
            {
                return false;
            }

            try
            {
                harmony.Patch(
                    target,
                    postfix: new HarmonyMethod(
                        typeof(FactionGearCustomizerCompatibility),
                        postfixName));
                return true;
            }
            catch
            {
                WriteCompatibilityNotice(
                    "Faction Gear Customizer optional compatibility changed; an unsupported hook was skipped.");
                return false;
            }
        }

        private static void TradeRulesApplied(string __0)
        {
            if (rollbackBridgeEnabled && !string.IsNullOrEmpty(__0))
            {
                PendingTraderKinds.Add(__0);
            }

            BuyerInfoService.InvalidateTraderRules("Faction Gear Customizer");
        }

        private static void TradeRulesReverted(string __0)
        {
            if (!string.IsNullOrEmpty(__0))
            {
                PendingTraderKinds.Remove(__0);
            }

            if (rollbackInProgress)
            {
                rollbackNotificationPending = true;
                return;
            }

            BuyerInfoService.InvalidateTraderRules("Faction Gear Customizer");
        }

        private static void AllFactionsReset()
        {
            if (!rollbackBridgeEnabled
                || rollbackInProgress
                || revertAllMethod == null
                || PendingTraderKinds.Count == 0)
            {
                return;
            }

            string[] traderKindNames = new string[PendingTraderKinds.Count];
            PendingTraderKinds.CopyTo(traderKindNames);
            bool restoredAny = false;
            rollbackInProgress = true;
            rollbackNotificationPending = false;

            try
            {
                for (int index = 0; index < traderKindNames.Length; index++)
                {
                    string traderKindName = traderKindNames[index];
                    try
                    {
                        revertAllMethod.Invoke(null, new object[] { traderKindName });
                        PendingTraderKinds.Remove(traderKindName);
                        restoredAny = true;
                    }
                    catch
                    {
                        DisableRollbackBridge(
                            "Faction Gear Customizer rollback API changed; defensive rollback was disabled for this session.");
                        break;
                    }
                }
            }
            finally
            {
                rollbackInProgress = false;
                if (restoredAny || rollbackNotificationPending)
                {
                    BuyerInfoService.InvalidateTraderRules(
                        "Faction Gear Customizer global reset");
                }
                rollbackNotificationPending = false;
            }
        }

        private static void DisableRollbackBridge(string notice)
        {
            rollbackBridgeEnabled = false;
            rollbackInProgress = false;
            rollbackNotificationPending = false;
            revertAllMethod = null;
            PendingTraderKinds.Clear();
            WriteCompatibilityNotice(notice);
        }

        private static void WriteCompatibilityNotice(string notice)
        {
            if (compatibilityNoticeWritten)
            {
                return;
            }

            compatibilityNoticeWritten = true;
            Log.Message("[Buyer Info Card] " + notice);
        }
    }
}
