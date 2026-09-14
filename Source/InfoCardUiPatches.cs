using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace BuyerInfoCard
{
    internal sealed class BuyerDetailResult
    {
        internal string Text;
        internal int WorldRevision;
        internal readonly List<Dialog_InfoCard.Hyperlink> Hyperlinks =
            new List<Dialog_InfoCard.Hyperlink>();
    }

    internal static class InfoCardUiPatches
    {
        private sealed class CardSnapshot
        {
            internal readonly Dictionary<object, BuyerDisplayResult> Displays =
                new Dictionary<object, BuyerDisplayResult>();
            internal readonly Dictionary<object, BuyerDetailResult> Details =
                new Dictionary<object, BuyerDetailResult>();
            internal readonly Dictionary<object, bool> Visibility =
                new Dictionary<object, bool>();
            internal readonly Dictionary<Dialog_InfoCard.Hyperlink, string> HyperlinkCaptions =
                new Dictionary<Dialog_InfoCard.Hyperlink, string>();
            internal readonly Dictionary<Dialog_InfoCard.Hyperlink, Action> HyperlinkActions =
                new Dictionary<Dialog_InfoCard.Hyperlink, Action>();
            internal BuyerDisplayResult LastDisplay;
        }

        private static readonly ConditionalWeakTable<Dialog_InfoCard, CardSnapshot> CardSnapshots =
            new ConditionalWeakTable<Dialog_InfoCard, CardSnapshot>();
        private static readonly FieldInfo StatField = AccessTools.Field(typeof(StatDrawEntry), "stat");
        private static readonly FieldInfo OptionalRequestField = AccessTools.Field(typeof(StatDrawEntry), "optionalReq");
        private static readonly FieldInfo HasOptionalRequestField = AccessTools.Field(typeof(StatDrawEntry), "hasOptionalReq");

        [ThreadStatic]
        private static Dialog_InfoCard activeCard;

        private static bool applied;
        private static BuyerQueryResult memoDisplayQuery;
        private static BuyerDisplayResult memoDisplay;
        private static BuyerDisplayResult memoDetailDisplay;
        private static int memoDetailWorldRevision = -1;
        private static BuyerDetailPresentation memoDetailPresentation;

        internal static void Apply(Harmony harmony)
        {
            if (applied)
            {
                return;
            }
            applied = true;

            Patch(
                harmony,
                AccessTools.Method(typeof(Dialog_InfoCard), nameof(Dialog_InfoCard.DoWindowContents), new[] { typeof(Rect) }),
                prefix: new HarmonyMethod(typeof(InfoCardUiPatches), nameof(InfoCardDrawPrefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(InfoCardUiPatches), nameof(InfoCardDrawFinalizer)) { priority = Priority.Last });
            Patch(
                harmony,
                AccessTools.Method(typeof(Dialog_InfoCard), nameof(Dialog_InfoCard.Close), new[] { typeof(bool) }),
                postfix: new HarmonyMethod(typeof(InfoCardUiPatches), nameof(InfoCardClosePostfix)));
            Patch(
                harmony,
                AccessTools.Method(typeof(StatDrawEntry), "Draw"),
                postfix: new HarmonyMethod(typeof(InfoCardUiPatches), nameof(StatDrawPostfix)) { priority = Priority.Last });
            Patch(
                harmony,
                AccessTools.PropertyGetter(typeof(Dialog_InfoCard.Hyperlink), "Label"),
                prefix: new HarmonyMethod(typeof(InfoCardUiPatches), nameof(HyperlinkLabelPrefix)));
            Patch(
                harmony,
                AccessTools.Method(typeof(Dialog_InfoCard.Hyperlink), "ActivateHyperlink"),
                prefix: new HarmonyMethod(typeof(InfoCardUiPatches), nameof(HyperlinkActivatePrefix)));
            Patch(
                harmony,
                AccessTools.Method(typeof(StatDrawEntry), "GetExplanationText"),
                postfix: new HarmonyMethod(typeof(InfoCardUiPatches), nameof(StatExplanationPostfix)));
            Patch(
                harmony,
                AccessTools.Method(typeof(StatDrawEntry), "GetHyperlinks"),
                postfix: new HarmonyMethod(typeof(InfoCardUiPatches), nameof(StatHyperlinksPostfix)));
        }

        internal static BuyerDisplayResult GetDisplayResult(StatRequest request)
        {
            BuyerDisplayResult result;
            if (TryGetSessionDisplay(request, out result))
            {
                return result;
            }

            BuyerInfoCardSettings settings = BuyerInfoCardMod.Settings;
            BuyerQueryResult query = BuyerInfoService.GetBuyerResult(request, settings);
            if (memoDisplay != null && memoDisplayQuery == query)
            {
                result = memoDisplay;
            }
            else
            {
                result = BuyerInfoPresentation.BuildDisplay(query, settings);
                memoDisplayQuery = query;
                memoDisplay = result;
            }
            StoreSessionDisplay(request, result);
            return result;
        }

        internal static BuyerDetailResult GetDetailResult(StatRequest request)
        {
            BuyerDetailResult result;
            if (TryGetSessionDetail(request, out result))
            {
                return result;
            }

            BuyerInfoCardSettings settings = BuyerInfoCardMod.Settings;
            BuyerDisplayResult display = GetDisplayResult(request);
            int worldRevision = BuyerInfoService.CurrentWorldRevision;
            BuyerDetailPresentation presentation;
            if (memoDetailPresentation != null
                && memoDetailDisplay == display
                && memoDetailWorldRevision == worldRevision)
            {
                presentation = memoDetailPresentation;
            }
            else
            {
                presentation = BuyerInfoPresentation.BuildDetail(display, settings);
                memoDetailDisplay = display;
                memoDetailWorldRevision = worldRevision;
                memoDetailPresentation = presentation;
            }
            result = new BuyerDetailResult
            {
                Text = presentation.Text,
                WorldRevision = presentation.WorldRevision
            };
            for (int index = 0; index < presentation.Links.Count; index++)
            {
                TryAddDetailHyperlink(result, presentation.Links[index], settings);
            }
            StoreSessionDetail(request, result);
            return result;
        }

        private static bool TryGetSessionDisplay(StatRequest request, out BuyerDisplayResult result)
        {
            result = null;
            CardSnapshot snapshot;
            object key = RequestKey(request);
            return activeCard != null
                && key != null
                && CardSnapshots.TryGetValue(activeCard, out snapshot)
                && snapshot.Displays.TryGetValue(key, out result);
        }

        private static void StoreSessionDisplay(StatRequest request, BuyerDisplayResult result)
        {
            object key = RequestKey(request);
            if (activeCard == null || key == null || result == null)
            {
                return;
            }
            CardSnapshot snapshot = SnapshotFor(activeCard);
            snapshot.Displays[key] = result;
            snapshot.LastDisplay = result;
        }

        private static bool TryGetSessionDetail(StatRequest request, out BuyerDetailResult result)
        {
            result = null;
            CardSnapshot snapshot;
            object key = RequestKey(request);
            if (activeCard == null
                || key == null
                || !CardSnapshots.TryGetValue(activeCard, out snapshot)
                || !snapshot.Details.TryGetValue(key, out result))
            {
                return false;
            }
            if (result.WorldRevision == BuyerInfoService.CurrentWorldRevision)
            {
                return true;
            }

            snapshot.Details.Clear();
            snapshot.HyperlinkCaptions.Clear();
            snapshot.HyperlinkActions.Clear();
            result = null;
            return false;
        }

        private static void StoreSessionDetail(StatRequest request, BuyerDetailResult result)
        {
            object key = RequestKey(request);
            if (activeCard == null || key == null || result == null)
            {
                return;
            }
            CardSnapshot snapshot = SnapshotFor(activeCard);
            snapshot.Details[key] = result;
        }

        internal static bool FreezeSessionVisibility(StatRequest request, bool currentValue)
        {
            object key = RequestKey(request);
            if (activeCard == null || key == null)
            {
                return currentValue;
            }
            CardSnapshot snapshot = SnapshotFor(activeCard);
            bool frozenValue;
            if (snapshot.Visibility.TryGetValue(key, out frozenValue))
            {
                return frozenValue;
            }
            snapshot.Visibility[key] = currentValue;
            return currentValue;
        }

        private static void TryAddDetailHyperlink(
            BuyerDetailResult detail,
            BuyerDetailLink link,
            BuyerInfoCardSettings settings)
        {
            if (detail == null
                || link == null
                || link.FallbackDef == null
                || string.IsNullOrEmpty(link.Caption))
            {
                return;
            }

            try
            {
                Dialog_InfoCard.Hyperlink hyperlink =
                    new Dialog_InfoCard.Hyperlink(link.FallbackDef, link.SelectedStatIndex);
                SetHyperlinkLabel(hyperlink, link.Caption);
                SetHyperlinkAction(hyperlink, BuildHyperlinkAction(link, settings));
                detail.Hyperlinks.Add(hyperlink);
            }
            catch (Exception exception)
            {
                Log.WarningOnce(
                    "[Buyer Info Card] Skipped an invalid detail hyperlink for "
                    + link.FallbackDef.defName + ": " + exception.Message,
                    Gen.HashCombineInt(0x424943, link.SelectedStatIndex));
            }
        }

        private static Action BuildHyperlinkAction(
            BuyerDetailLink link,
            BuyerInfoCardSettings settings)
        {
            if (link.Kind == BuyerDetailLinkKind.Settlement)
            {
                Settlement settlement = link.Settlement;
                return delegate { JumpToWorldObject(settlement); };
            }

            Faction faction = link.Faction;
            if (settings.FactionLinkMode == FactionLinkDestination.InfoCard)
            {
                return delegate
                {
                    if (faction != null)
                    {
                        Find.WindowStack.Add(new Dialog_InfoCard(faction));
                    }
                };
            }
            return delegate
            {
                if (faction == null)
                {
                    return;
                }
                Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Factions, true);
                MainTabWindow_Factions factionWindow =
                    MainButtonDefOf.Factions.TabWindow as MainTabWindow_Factions;
                if (factionWindow != null)
                {
                    factionWindow.ScrollToFaction(faction);
                }
            };
        }

        private static void SetHyperlinkLabel(Dialog_InfoCard.Hyperlink hyperlink, string label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return;
            }
            if (activeCard != null)
            {
                SnapshotFor(activeCard).HyperlinkCaptions[hyperlink] = label;
            }
        }

        private static void JumpToWorldObject(WorldObject worldObject)
        {
            if (worldObject == null || worldObject.Destroyed || !worldObject.Spawned)
            {
                return;
            }

            Dialog_InfoCard card = activeCard;
            if (card != null)
            {
                card.Close();
            }
            CameraJumper.TryJumpAndSelect(
                new GlobalTargetInfo(worldObject),
                CameraJumper.MovementMode.Pan);
        }

        private static void SetHyperlinkAction(Dialog_InfoCard.Hyperlink hyperlink, Action action)
        {
            if (activeCard != null && action != null)
            {
                SnapshotFor(activeCard).HyperlinkActions[hyperlink] = action;
            }
        }

        private static void Patch(
            Harmony harmony,
            MethodBase target,
            HarmonyMethod prefix = null,
            HarmonyMethod postfix = null,
            HarmonyMethod finalizer = null)
        {
            if (target == null)
            {
                Log.Error("[Buyer Info Card] An information-card UI patch target was not found.");
                return;
            }
            harmony.Patch(target, prefix, postfix, null, finalizer);
        }

        private static void InfoCardDrawPrefix(Dialog_InfoCard __instance, out Dialog_InfoCard __state)
        {
            __state = activeCard;
            activeCard = __instance;
            SnapshotFor(__instance);
        }

        private static Exception InfoCardDrawFinalizer(Dialog_InfoCard __state, Exception __exception)
        {
            activeCard = __state;
            return __exception;
        }

        private static void InfoCardClosePostfix(Dialog_InfoCard __instance)
        {
            CardSnapshots.Remove(__instance);
            if (activeCard == __instance)
            {
                activeCard = null;
            }
        }

        private static bool HyperlinkLabelPrefix(ref Dialog_InfoCard.Hyperlink __instance, ref string __result)
        {
            CardSnapshot snapshot;
            string caption;
            if (activeCard != null
                && CardSnapshots.TryGetValue(activeCard, out snapshot)
                && snapshot.HyperlinkCaptions.TryGetValue(__instance, out caption))
            {
                __result = caption;
                return false;
            }
            return true;
        }

        private static bool HyperlinkActivatePrefix(ref Dialog_InfoCard.Hyperlink __instance)
        {
            CardSnapshot snapshot;
            Action action;
            if (activeCard != null
                && CardSnapshots.TryGetValue(activeCard, out snapshot)
                && snapshot.HyperlinkActions.TryGetValue(__instance, out action))
            {
                action();
                return false;
            }
            return true;
        }

        private static void StatExplanationPostfix(StatDrawEntry __instance, StatRequest __0, ref string __result)
        {
            if (!ShouldMergeIntoMarketValue(__instance, __0))
            {
                return;
            }
            BuyerDetailResult detail = GetDetailResult(__0);
            string section = "BIC_BuyersSection".Translate() + "\n" + detail.Text;
            __result = string.IsNullOrEmpty(__result) ? section : __result + "\n\n" + section;
        }

        private static void StatHyperlinksPostfix(
            StatDrawEntry __instance,
            StatRequest __0,
            ref IEnumerable<Dialog_InfoCard.Hyperlink> __result)
        {
            if (!ShouldMergeIntoMarketValue(__instance, __0))
            {
                return;
            }
            List<Dialog_InfoCard.Hyperlink> combined = __result == null
                ? new List<Dialog_InfoCard.Hyperlink>()
                : new List<Dialog_InfoCard.Hyperlink>(__result);
            combined.AddRange(GetDetailResult(__0).Hyperlinks);
            __result = combined;
        }

        private static bool ShouldMergeIntoMarketValue(StatDrawEntry entry, StatRequest request)
        {
            if (!BuyerInfoCardMod.Settings.MergeIntoMarketValue || StatField == null)
            {
                return false;
            }
            StatDef stat = StatField.GetValue(entry) as StatDef;
            if (stat != StatDefOf.MarketValue)
            {
                return false;
            }
            ThingDef thingDef = BuyerInfoService.ResolveThingDef(request);
            return FreezeSessionVisibility(
                request,
                CategoryCatalog.ShouldShow(thingDef, request.Thing, BuyerInfoCardMod.Settings));
        }

        private static void StatDrawPostfix(
            StatDrawEntry __instance,
            float x,
            float y,
            float width,
            Vector2 scrollPosition,
            Rect scrollOutRect)
        {
            if (StatField == null
                || StatField.GetValue(__instance) as StatDef != CategoryCatalog.BuyerStat)
            {
                return;
            }

            BuyerDisplayResult display = CurrentDisplayFor(__instance);
            if (display == null || !display.DrawSummaryIcons || display.Parties.Count == 0)
            {
                return;
            }
            if (y - scrollPosition.y + Text.LineHeight < 0f || y - scrollPosition.y > scrollOutRect.height)
            {
                return;
            }

            float valueWidth = width * 0.45f;
            Rect valueRect = new Rect(x + width - valueWidth + 4f, y, valueWidth - 6f, Text.LineHeight);
            float cursor = valueRect.x;
            if (!string.IsNullOrEmpty(display.SummaryPrefix))
            {
                Vector2 prefixSize = Text.CalcSize(display.SummaryPrefix + " · ");
                float prefixWidth = Mathf.Min(prefixSize.x, valueRect.width * 0.55f);
                Widgets.Label(new Rect(cursor, y, prefixWidth, Text.LineHeight), display.SummaryPrefix + " · ");
                cursor += prefixWidth;
            }

            float iconSize = Mathf.Min(18f, Text.LineHeight - 2f);
            float spacing = 2f;
            float remainingWidth = Mathf.Max(0f, valueRect.xMax - cursor);
            int capacity = Mathf.FloorToInt(remainingWidth / (iconSize + spacing));
            List<BuyerParty> iconParties = new List<BuyerParty>();
            for (int index = 0; index < display.Parties.Count; index++)
            {
                if (!display.Parties[index].IsFactionlessTradeShip)
                {
                    iconParties.Add(display.Parties[index]);
                }
            }
            if (capacity <= 0)
            {
                Widgets.Label(valueRect, "BIC_BuyerCount".Translate(display.Parties.Count));
                return;
            }
            int shown = Mathf.Min(capacity, iconParties.Count);
            if (shown < display.Parties.Count && shown >= capacity && shown > 0)
            {
                shown--;
            }

            for (int index = 0; index < shown; index++)
            {
                BuyerParty party = iconParties[index];
                Faction faction = party.Faction;
                Rect iconRect = new Rect(cursor, y + 1f, iconSize, iconSize);
                Texture2D icon = faction == null || faction.def == null
                    ? null
                    : faction.def.FactionIcon;
                if (icon != null && Event.current.type == EventType.Repaint)
                {
                    Color previousColor = GUI.color;
                    GUI.color = faction.Color;
                    Widgets.DrawTextureFitted(iconRect, icon, 1f);
                    GUI.color = previousColor;
                }
                TooltipHandler.TipRegion(iconRect, party.Label);
                cursor += iconSize + spacing;
            }

            int hidden = display.Parties.Count - shown;
            if (hidden > 0)
            {
                Rect countRect = new Rect(cursor, y, Mathf.Max(0f, valueRect.xMax - cursor), Text.LineHeight);
                Widgets.Label(countRect, "+" + hidden);
                string hiddenNames = string.Empty;
                int factionIconIndex = 0;
                for (int index = 0; index < display.Parties.Count; index++)
                {
                    BuyerParty party = display.Parties[index];
                    bool isHidden = party.IsFactionlessTradeShip || factionIconIndex >= shown;
                    if (!party.IsFactionlessTradeShip)
                    {
                        factionIconIndex++;
                    }
                    if (!isHidden)
                    {
                        continue;
                    }
                    if (hiddenNames.Length > 0)
                    {
                        hiddenNames += "\n";
                    }
                    hiddenNames += party.Label;
                }
                TooltipHandler.TipRegion(countRect, hiddenNames);
            }
        }

        private static BuyerDisplayResult CurrentDisplayFor(StatDrawEntry entry)
        {
            if (OptionalRequestField != null && HasOptionalRequestField != null)
            {
                bool hasRequest = (bool)HasOptionalRequestField.GetValue(entry);
                if (hasRequest)
                {
                    StatRequest request = (StatRequest)OptionalRequestField.GetValue(entry);
                    BuyerDisplayResult requestDisplay;
                    if (TryGetSessionDisplay(request, out requestDisplay))
                    {
                        return requestDisplay;
                    }
                    return GetDisplayResult(request);
                }
            }

            CardSnapshot snapshot;
            if (activeCard != null
                && CardSnapshots.TryGetValue(activeCard, out snapshot))
            {
                return snapshot.LastDisplay;
            }
            return null;
        }

        private static CardSnapshot SnapshotFor(Dialog_InfoCard card)
        {
            return CardSnapshots.GetValue(card, delegate(Dialog_InfoCard ignored) { return new CardSnapshot(); });
        }

        private static object RequestKey(StatRequest request)
        {
            if (request.Thing != null)
            {
                return request.Thing;
            }
            return request.Def as ThingDef;
        }
    }
}
