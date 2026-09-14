using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace BuyerInfoCard
{
    public enum FactionLinkDestination
    {
        FactionMenu,
        InfoCard
    }

    public sealed class BuyerInfoCardSettings : ModSettings
    {
        private const int CurrentSettingsVersion = 4;

        public bool SummaryUsesIcons = true;
        public bool MergeIntoMarketValue;
        public bool ShowNearestSettlements = true;
        public bool ShowTraderKinds = true;
        public FactionLinkDestination FactionLinkMode = FactionLinkDestination.FactionMenu;
        public bool ExcludeHostileFactions;
        public bool ShowFactionlessTradeShips = true;
        public bool ShowDeadmansApparel;
        public bool ShowBiocoded;
        public bool ShowRotten;
        public int RuleCacheCapacity = 64;

        // These lists are the serialized source of truth. The sets below are
        // rebuildable lookup indexes and cannot be mutated outside this class.
        private List<string> disabledCategoryKeys = new List<string> { CategoryKeys.HumanlikeColonist };
        private List<string> disabledFactionDefNames = new List<string>();
        private HashSet<string> disabledCategoryIndex;
        private HashSet<string> disabledFactionIndex;

        private int settingsVersion;
        private bool legacyShowCurrentlyUnsellable;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref SummaryUsesIcons, "summaryUsesIcons", true);
            Scribe_Values.Look(ref MergeIntoMarketValue, "mergeIntoMarketValue", false);
            Scribe_Values.Look(ref ShowNearestSettlements, "showNearestSettlements", true);
            Scribe_Values.Look(ref ShowTraderKinds, "showTraderKinds", true);
            Scribe_Values.Look(ref FactionLinkMode, "factionLinkMode", FactionLinkDestination.FactionMenu);
            Scribe_Values.Look(ref ExcludeHostileFactions, "excludeHostileFactions", false);
            Scribe_Values.Look(ref ShowFactionlessTradeShips, "showFactionlessTradeShips", true);
            if (Scribe.mode != LoadSaveMode.Saving)
            {
                Scribe_Values.Look(ref legacyShowCurrentlyUnsellable, "showCurrentlyUnsellable", false);
            }
            Scribe_Values.Look(ref ShowDeadmansApparel, "showDeadmansApparel", false);
            Scribe_Values.Look(ref ShowBiocoded, "showBiocoded", false);
            Scribe_Values.Look(ref ShowRotten, "showRotten", false);
            Scribe_Values.Look(ref RuleCacheCapacity, "ruleCacheCapacity", 64);
            Scribe_Collections.Look(ref disabledCategoryKeys, "disabledCategoryKeys", LookMode.Value);
            Scribe_Collections.Look(ref disabledFactionDefNames, "disabledFactionDefNames", LookMode.Value);
            Scribe_Values.Look(ref settingsVersion, "settingsVersion", 0);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                disabledCategoryKeys = disabledCategoryKeys ?? new List<string>();
                disabledFactionDefNames = disabledFactionDefNames ?? new List<string>();
                if (settingsVersion < 1 && RuleCacheCapacity == 1024)
                {
                    RuleCacheCapacity = 64;
                }
                if (settingsVersion < 2)
                {
                    ShowNearestSettlements = true;
                    ShowTraderKinds = true;
                    FactionLinkMode = FactionLinkDestination.FactionMenu;
                }
                if (settingsVersion < 3 && !legacyShowCurrentlyUnsellable)
                {
                    // These switches were previously disabled by the removed master switch.
                    ShowDeadmansApparel = false;
                    ShowBiocoded = false;
                    ShowRotten = false;
                }
                if (settingsVersion < 4
                    && !disabledCategoryKeys.Contains(CategoryKeys.HumanlikeColonist))
                {
                    disabledCategoryKeys.Add(CategoryKeys.HumanlikeColonist);
                }
                RuleCacheCapacity = Mathf.Clamp(RuleCacheCapacity, 0, 1024);
                settingsVersion = CurrentSettingsVersion;
                legacyShowCurrentlyUnsellable = false;
                RebuildLookupIndexes();
            }
        }

        public bool IsCategoryEnabled(string key)
        {
            EnsureCategoryIndex();
            return !disabledCategoryIndex.Contains(key);
        }

        public void SetCategoryEnabled(string key, bool enabled)
        {
            EnsureCategoryIndex();
            if (enabled)
            {
                if (disabledCategoryIndex.Remove(key))
                {
                    disabledCategoryKeys.RemoveAll(value => string.Equals(value, key, StringComparison.Ordinal));
                }
            }
            else if (disabledCategoryIndex.Add(key))
            {
                disabledCategoryKeys.Add(key);
            }
        }

        public bool IsFactionEnabled(string defName)
        {
            EnsureFactionIndex();
            return !disabledFactionIndex.Contains(defName);
        }

        public void SetFactionEnabled(string defName, bool enabled)
        {
            EnsureFactionIndex();
            if (enabled)
            {
                if (disabledFactionIndex.Remove(defName))
                {
                    disabledFactionDefNames.RemoveAll(value => string.Equals(value, defName, StringComparison.Ordinal));
                }
            }
            else if (disabledFactionIndex.Add(defName))
            {
                disabledFactionDefNames.Add(defName);
            }
        }

        public void ResetBasicSettings()
        {
            SummaryUsesIcons = true;
            MergeIntoMarketValue = false;
            ShowNearestSettlements = true;
            ShowTraderKinds = true;
            FactionLinkMode = FactionLinkDestination.FactionMenu;
            RuleCacheCapacity = 64;
        }

        public void ResetFactionSettings()
        {
            ExcludeHostileFactions = false;
            ShowFactionlessTradeShips = true;
            disabledFactionDefNames.Clear();
            disabledFactionIndex = null;
        }

        public void ResetItemSettings()
        {
            ShowDeadmansApparel = false;
            ShowBiocoded = false;
            ShowRotten = false;
            disabledCategoryKeys.Clear();
            disabledCategoryKeys.Add(CategoryKeys.HumanlikeColonist);
            disabledCategoryIndex = null;
        }

        private void RebuildLookupIndexes()
        {
            disabledCategoryIndex = new HashSet<string>(disabledCategoryKeys, StringComparer.Ordinal);
            disabledFactionIndex = new HashSet<string>(disabledFactionDefNames, StringComparer.Ordinal);
        }

        private void EnsureCategoryIndex()
        {
            if (disabledCategoryIndex == null)
            {
                disabledCategoryIndex = new HashSet<string>(
                    disabledCategoryKeys ?? new List<string>(),
                    StringComparer.Ordinal);
            }
        }

        private void EnsureFactionIndex()
        {
            if (disabledFactionIndex == null)
            {
                disabledFactionIndex = new HashSet<string>(
                    disabledFactionDefNames ?? new List<string>(),
                    StringComparer.Ordinal);
            }
        }
    }
}
