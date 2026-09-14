using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace BuyerInfoCard
{
    internal static class CategoryKeys
    {
        internal const string HumanlikeRoot = "BIC:Humanlike";
        internal const string HumanlikeColonist = "BIC:Humanlike:Colonist";
        internal const string HumanlikePrisoner = "BIC:Humanlike:Prisoner";
        internal const string HumanlikeSlave = "BIC:Humanlike:Slave";
        internal const string HumanlikeOther = "BIC:Humanlike:Other";
    }

    internal sealed class CategoryRow
    {
        internal string Key;
        internal string Label;
        internal int Depth;
        internal CategoryRow Parent;
        internal readonly List<CategoryRow> Children = new List<CategoryRow>();

        internal bool IsGroup
        {
            get { return Children.Count > 0; }
        }
    }

    [Flags]
    internal enum CurrentSaleRestriction
    {
        None = 0,
        DeadmansApparel = 1,
        Biocoded = 2,
        Rotten = 4,
        OtherCurrentRestriction = 8
    }

    internal static class CategoryCatalog
    {
        private static readonly List<CategoryRow> RootRows = new List<CategoryRow>();
        private static readonly List<CategoryRow> AllRows = new List<CategoryRow>();
        private static readonly List<CategoryRow> SettingsRows = new List<CategoryRow>();
        private static readonly HashSet<ThingDef> SupportedDefs = new HashSet<ThingDef>();
        private static readonly HashSet<ThingCategoryDef> VisibleCategories = new HashSet<ThingCategoryDef>();
        private static StatDef buyerStat;

        internal static IReadOnlyList<CategoryRow> CategoryRoots
        {
            get { return RootRows; }
        }

        internal static IReadOnlyList<CategoryRow> AllCategoryRows
        {
            get { return AllRows; }
        }

        internal static IReadOnlyList<CategoryRow> SettingsCategoryRows
        {
            get { return SettingsRows; }
        }

        internal static StatDef BuyerStat
        {
            get { return buyerStat; }
        }

        internal static void RebuildAndInjectStat()
        {
            RootRows.Clear();
            AllRows.Clear();
            SettingsRows.Clear();
            SupportedDefs.Clear();
            VisibleCategories.Clear();
            buyerStat = DefDatabase<StatDef>.GetNamedSilentFail("BuyerInfoCard_Buyers");
            if (buyerStat == null)
            {
                Log.Error("[Buyer Info Card] BuyerInfoCard_Buyers StatDef was not loaded.");
                return;
            }

            List<ThingDef> allThingDefs = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int index = 0; index < allThingDefs.Count; index++)
            {
                ThingDef thingDef = allThingDefs[index];
                if (IsDefinitionSupported(thingDef))
                {
                    SupportedDefs.Add(thingDef);
                }
            }

            List<ThingCategoryDef> allCategories = DefDatabase<ThingCategoryDef>.AllDefsListForReading;
            for (int index = 0; index < allCategories.Count; index++)
            {
                ThingCategoryDef category = allCategories[index];
                if (CategoryContainsSupportedDef(category))
                {
                    AddCategoryAndParents(category);
                }
            }

            if (SupportedDefs.Any(def => def.race != null && !def.race.Humanlike) && ThingCategoryDefOf.Animals != null)
            {
                AddCategoryAndParents(ThingCategoryDefOf.Animals);
            }

            CategoryRow humanlike = AddRoot(CategoryKeys.HumanlikeRoot, "BIC_Humanlikes".Translate());
            AddChild(humanlike, CategoryKeys.HumanlikeColonist, "BIC_Colonists".Translate());
            AddChild(humanlike, CategoryKeys.HumanlikePrisoner, "BIC_Prisoners".Translate());
            AddChild(humanlike, CategoryKeys.HumanlikeSlave, "BIC_Slaves".Translate());
            AddChild(humanlike, CategoryKeys.HumanlikeOther, "BIC_Other".Translate());
            AddSettingsRowsDepthFirst(humanlike);

            ThingCategoryDef root = ThingCategoryDefOf.Root;
            if (root != null)
            {
                CategoryRow rootRow = BuildCategoryTree(root, null, 0);
                if (rootRow != null)
                {
                    RootRows.Add(rootRow);
                    CategoryRow animals = null;
                    for (int index = 0; index < rootRow.Children.Count; index++)
                    {
                        CategoryRow child = rootRow.Children[index];
                        if (ThingCategoryDefOf.Animals != null
                            && child.Key == CategoryKey(ThingCategoryDefOf.Animals))
                        {
                            animals = child;
                            break;
                        }
                    }
                    if (animals != null)
                    {
                        SettingsRows.Add(animals);
                    }
                    for (int index = 0; index < rootRow.Children.Count; index++)
                    {
                        CategoryRow child = rootRow.Children[index];
                        if (child != animals)
                        {
                            SettingsRows.Add(child);
                        }
                    }
                }
            }

            for (int index = 0; index < allThingDefs.Count; index++)
            {
                ThingDef thingDef = allThingDefs[index];
                if (thingDef.statBases != null)
                {
                    thingDef.statBases.RemoveAll(modifier => modifier.stat == buyerStat);
                }

                if (!SupportedDefs.Contains(thingDef))
                {
                    continue;
                }

                if (thingDef.statBases == null)
                {
                    thingDef.statBases = new List<StatModifier>();
                }

                thingDef.statBases.Add(new StatModifier { stat = buyerStat, value = 0f });
            }
        }

        internal static bool IsSupported(ThingDef thingDef)
        {
            return thingDef != null && SupportedDefs.Contains(thingDef);
        }

        internal static bool ShouldShow(
            ThingDef thingDef,
            Thing thing,
            BuyerInfoCardSettings settings)
        {
            if (!IsSupported(thingDef) || !CategoryEnabledFor(thingDef, thing, settings))
            {
                return false;
            }

            CurrentSaleRestriction restrictions = GetCurrentRestrictions(thing);
            if (restrictions == CurrentSaleRestriction.None)
            {
                return true;
            }

            if ((restrictions & CurrentSaleRestriction.OtherCurrentRestriction) != 0)
            {
                return false;
            }

            if ((restrictions & CurrentSaleRestriction.DeadmansApparel) != 0 && !settings.ShowDeadmansApparel)
            {
                return false;
            }
            if ((restrictions & CurrentSaleRestriction.Biocoded) != 0 && !settings.ShowBiocoded)
            {
                return false;
            }
            if ((restrictions & CurrentSaleRestriction.Rotten) != 0 && !settings.ShowRotten)
            {
                return false;
            }

            return true;
        }

        internal static CurrentSaleRestriction GetCurrentRestrictions(Thing thing)
        {
            if (thing == null)
            {
                return CurrentSaleRestriction.None;
            }

            CurrentSaleRestriction result = CurrentSaleRestriction.None;
            bool playerSellableNow = true;
            // In RimWorld 1.6 PlayerSellableNow's ITrader parameter is not read;
            // it performs the universal current-instance checks we need here.
            try
            {
                playerSellableNow = TradeUtility.PlayerSellableNow(thing, null);
            }
            catch
            {
                // Keep the three explicit, stable checks below if another mod
                // replaces PlayerSellableNow with code that requires a trader.
            }

            Apparel apparel = thing as Apparel;
            if (apparel != null && apparel.WornByCorpse)
            {
                result |= CurrentSaleRestriction.DeadmansApparel;
            }

            ThingWithComps thingWithComps = thing as ThingWithComps;
            if (thingWithComps != null)
            {
                CompBiocodable biocodable = thingWithComps.GetComp<CompBiocodable>();
                if (biocodable != null && biocodable.Biocoded)
                {
                    result |= CurrentSaleRestriction.Biocoded;
                }

                CompRottable rottable = thingWithComps.GetComp<CompRottable>();
                if (rottable != null && rottable.Stage != RotStage.Fresh)
                {
                    result |= CurrentSaleRestriction.Rotten;
                }
            }

            if (!playerSellableNow && result == CurrentSaleRestriction.None)
            {
                result |= CurrentSaleRestriction.OtherCurrentRestriction;
            }

            return result;
        }

        private static bool IsDefinitionSupported(ThingDef thingDef)
        {
            if (thingDef == null || thingDef.IsBlueprint || thingDef.IsFrame || thingDef.projectile != null)
            {
                return false;
            }

            if (thingDef.BaseMarketValue <= 0f && !thingDef.IsCorpse)
            {
                return false;
            }

            try
            {
                return TradeUtility.EverPlayerSellable(thingDef);
            }
            catch (Exception exception)
            {
                Log.ErrorOnce(
                    "[Buyer Info Card] EverPlayerSellable failed for " + thingDef.defName + ": " + exception.Message,
                    Gen.HashCombineInt(0x424943, thingDef.shortHash));
                return false;
            }
        }

        private static bool CategoryContainsSupportedDef(ThingCategoryDef category)
        {
            try
            {
                return category.DescendantThingDefs.Any(SupportedDefs.Contains);
            }
            catch
            {
                return false;
            }
        }

        private static void AddCategoryAndParents(ThingCategoryDef category)
        {
            ThingCategoryDef current = category;
            while (current != null && VisibleCategories.Add(current))
            {
                current = current.parent;
            }
        }

        private static CategoryRow AddRoot(string key, string label)
        {
            CategoryRow row = new CategoryRow
            {
                Key = key,
                Label = label,
                Depth = 0
            };
            RootRows.Add(row);
            AllRows.Add(row);
            return row;
        }

        private static CategoryRow AddChild(CategoryRow parent, string key, string label)
        {
            CategoryRow row = new CategoryRow
            {
                Key = key,
                Label = label,
                Depth = parent.Depth + 1,
                Parent = parent
            };
            parent.Children.Add(row);
            AllRows.Add(row);
            return row;
        }

        private static void AddSettingsRowsDepthFirst(CategoryRow row)
        {
            SettingsRows.Add(row);
            for (int index = 0; index < row.Children.Count; index++)
            {
                AddSettingsRowsDepthFirst(row.Children[index]);
            }
        }

        private static CategoryRow BuildCategoryTree(ThingCategoryDef category, CategoryRow parent, int depth)
        {
            if (!VisibleCategories.Contains(category))
            {
                return null;
            }

            CategoryRow row = new CategoryRow
            {
                Key = CategoryKey(category),
                Label = DisplayLabelUtility.GetDefLabel(category),
                Depth = depth,
                Parent = parent
            };
            AllRows.Add(row);

            if (category.childCategories == null)
            {
                return row;
            }

            List<ThingCategoryDef> children = category.childCategories
                .Where(VisibleCategories.Contains)
                .ToList();
            for (int index = 0; index < children.Count; index++)
            {
                CategoryRow child = BuildCategoryTree(children[index], row, depth + 1);
                if (child != null)
                {
                    row.Children.Add(child);
                }
            }
            return row;
        }

        private static bool CategoryEnabledFor(
            ThingDef thingDef,
            Thing thing,
            BuyerInfoCardSettings settings)
        {
            if (thingDef.race != null && thingDef.race.Humanlike)
            {
                if (!settings.IsCategoryEnabled(CategoryKeys.HumanlikeRoot))
                {
                    return false;
                }

                Pawn pawn = thing as Pawn;
                if (pawn == null)
                {
                    return true;
                }

                string subgroup = pawn.IsSlave
                    ? CategoryKeys.HumanlikeSlave
                    : pawn.IsPrisoner
                        ? CategoryKeys.HumanlikePrisoner
                        : pawn.IsColonist
                            ? CategoryKeys.HumanlikeColonist
                            : CategoryKeys.HumanlikeOther;
                return settings.IsCategoryEnabled(subgroup);
            }

            if (thingDef.race != null && ThingCategoryDefOf.Animals != null)
            {
                return IsTopLevelCategoryEnabled(ThingCategoryDefOf.Animals, settings);
            }

            if (thingDef.thingCategories == null || thingDef.thingCategories.Count == 0)
            {
                return true;
            }

            for (int index = 0; index < thingDef.thingCategories.Count; index++)
            {
                if (IsTopLevelCategoryEnabled(thingDef.thingCategories[index], settings))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsTopLevelCategoryEnabled(
            ThingCategoryDef category,
            BuyerInfoCardSettings settings)
        {
            ThingCategoryDef current = category;
            ThingCategoryDef root = ThingCategoryDefOf.Root;
            while (current != null && current.parent != null && current.parent != root)
            {
                current = current.parent;
            }
            return current == null || settings.IsCategoryEnabled(CategoryKey(current));
        }

        private static string CategoryKey(ThingCategoryDef category)
        {
            return "ThingCategoryDef:" + category.defName;
        }
    }
}
