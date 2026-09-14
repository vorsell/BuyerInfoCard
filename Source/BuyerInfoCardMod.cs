using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace BuyerInfoCard
{
    public sealed class BuyerInfoCardMod : Mod
    {
        private enum SettingsPage
        {
            Basic,
            FactionsAndItems
        }

        private const float SidebarWidth = 170f;
        private const float PageGap = 8f;
        private const float ColumnGap = 10f;
        private const float RowHeight = 30f;
        private const float BasicActionButtonWidth = 190f;

        internal static BuyerInfoCardSettings CurrentSettings;
        private static readonly BuyerInfoCardSettings Defaults = new BuyerInfoCardSettings();

        private SettingsPage currentPage = SettingsPage.Basic;
        private Vector2 factionScrollPosition;
        private Vector2 itemScrollPosition;
        private readonly HashSet<string> expandedCategoryKeys = new HashSet<string>(StringComparer.Ordinal);
        private string ruleCacheCapacityBuffer;
        private string paintListId;
        private bool paintValue;
        private string lastPaintedKey;

        public BuyerInfoCardMod(ModContentPack content)
            : base(content)
        {
            CurrentSettings = GetSettings<BuyerInfoCardSettings>();
            ruleCacheCapacityBuffer = CurrentSettings.RuleCacheCapacity.ToString();
        }

        internal static BuyerInfoCardSettings Settings
        {
            get { return CurrentSettings ?? Defaults; }
        }

        public override string SettingsCategory()
        {
            return "BIC_SettingsCategory".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (Event.current.rawType == EventType.MouseUp || Event.current.type == EventType.Ignore)
            {
                StopPainting();
            }

            Rect sidebar = new Rect(inRect.x, inRect.y, SidebarWidth, inRect.height);
            Rect content = new Rect(
                sidebar.xMax + PageGap,
                inRect.y,
                Mathf.Max(1f, inRect.width - SidebarWidth - PageGap),
                inRect.height);
            Widgets.DrawMenuSection(sidebar);
            DrawPageButton(
                new Rect(sidebar.x + 6f, sidebar.y + 8f, sidebar.width - 12f, 42f),
                SettingsPage.Basic,
                "BIC_PageBasic".Translate());
            DrawPageButton(
                new Rect(sidebar.x + 6f, sidebar.y + 56f, sidebar.width - 12f, 42f),
                SettingsPage.FactionsAndItems,
                "BIC_PageFactionsItems".Translate());

            bool changed;
            if (currentPage == SettingsPage.Basic)
            {
                Widgets.DrawMenuSection(content);
                changed = DrawBasicSettings(content.ContractedBy(12f), Settings);
            }
            else
            {
                changed = DrawFactionAndItemSettings(content, Settings);
            }

            if (changed)
            {
                ApplySettingsChanges(Settings);
            }
        }

        private void DrawPageButton(Rect rect, SettingsPage page, string label)
        {
            if (currentPage == page)
            {
                Widgets.DrawBoxSolid(rect, new Color(1f, 1f, 1f, 0.12f));
            }
            if (Widgets.ButtonText(rect, label))
            {
                currentPage = page;
                StopPainting();
            }
        }

        private bool DrawBasicSettings(Rect rect, BuyerInfoCardSettings settings)
        {
            bool changed = false;
            float y = rect.y;
            Widgets.Label(new Rect(rect.x + 4f, y, rect.width - 8f, 28f), "BIC_GlobalRules".Translate());
            y += 32f;

            if (Widgets.ButtonText(
                new Rect(rect.x, y, Mathf.Min(BasicActionButtonWidth, rect.width), 30f),
                "BIC_Reset".Translate()))
            {
                settings.ResetBasicSettings();
                ruleCacheCapacityBuffer = settings.RuleCacheCapacity.ToString();
                changed = true;
            }
            y += 42f;

            DrawSectionHeader(new Rect(rect.x, y, rect.width, 28f), "BIC_DisplaySettings".Translate());
            y += 30f;
            changed |= DrawToggleRow(new Rect(rect.x, y, rect.width, RowHeight),
                "BIC_MergeIntoMarketValue".Translate(), ref settings.MergeIntoMarketValue);
            y += RowHeight;
            changed |= DrawToggleRow(new Rect(rect.x, y, rect.width, RowHeight),
                "BIC_SummaryUsesIcons".Translate(), ref settings.SummaryUsesIcons, settings.MergeIntoMarketValue);
            y += RowHeight;
            changed |= DrawToggleRow(new Rect(rect.x, y, rect.width, RowHeight),
                "BIC_ShowNearestSettlements".Translate(), ref settings.ShowNearestSettlements);
            y += RowHeight;
            changed |= DrawToggleRow(new Rect(rect.x, y, rect.width, RowHeight),
                "BIC_ShowTraderKinds".Translate(), ref settings.ShowTraderKinds);
            y += RowHeight;
            DrawFactionLinkDestinationRow(new Rect(rect.x, y, rect.width, RowHeight), settings);
            y += RowHeight + 12f;

            DrawSectionHeader(new Rect(rect.x, y, rect.width, 28f), "BIC_CacheSettings".Translate());
            y += 32f;
            int oldCapacity = settings.RuleCacheCapacity;
            Rect capacityRow = new Rect(rect.x, y, rect.width, RowHeight);
            Widgets.DrawHighlightIfMouseover(capacityRow);
            Widgets.Label(new Rect(capacityRow.x + 4f, capacityRow.y + 4f, capacityRow.width - 154f, 24f),
                "BIC_RuleCacheCapacity".Translate());
            Widgets.TextFieldNumeric(
                new Rect(capacityRow.xMax - 134f, capacityRow.y + 2f, 130f, 26f),
                ref settings.RuleCacheCapacity,
                ref ruleCacheCapacityBuffer,
                0f,
                1024f);
            settings.RuleCacheCapacity = Mathf.Clamp(settings.RuleCacheCapacity, 0, 1024);
            if (settings.RuleCacheCapacity != oldCapacity)
            {
                changed = true;
            }
            y += RowHeight + 4f;

            Text.Font = GameFont.Tiny;
            string help = "BIC_RuleCacheHelp".Translate();
            float helpHeight = Text.CalcHeight(help, rect.width - 8f);
            Color previousColor = GUI.color;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(rect.x + 4f, y, rect.width - 8f, helpHeight), help);
            GUI.color = previousColor;
            Text.Font = GameFont.Small;
            y += helpHeight + 8f;

            if (Widgets.ButtonText(new Rect(rect.x, y, Mathf.Min(BasicActionButtonWidth, rect.width), 30f),
                "BIC_ClearRuleCache".Translate()))
            {
                BuyerInfoService.InvalidateTraderRules("manual cache clear");
            }
            return changed;
        }

        private void DrawFactionLinkDestinationRow(Rect rect, BuyerInfoCardSettings settings)
        {
            Widgets.DrawHighlightIfMouseover(rect);
            const float buttonWidth = 150f;
            Widgets.Label(new Rect(rect.x + 4f, rect.y + 4f, rect.width - buttonWidth - 12f, 24f),
                "BIC_FactionLinkDestination".Translate());
            string currentLabel = settings.FactionLinkMode == FactionLinkDestination.InfoCard
                ? "BIC_LinkTargetInfoCard".Translate()
                : "BIC_LinkTargetFactionMenu".Translate();
            if (!Widgets.ButtonText(new Rect(rect.xMax - buttonWidth, rect.y + 1f, buttonWidth, 28f), currentLabel))
            {
                return;
            }

            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new FloatMenuOption("BIC_LinkTargetFactionMenu".Translate(), delegate
                {
                    settings.FactionLinkMode = FactionLinkDestination.FactionMenu;
                    ApplySettingsChanges(settings);
                }),
                new FloatMenuOption("BIC_LinkTargetInfoCard".Translate(), delegate
                {
                    settings.FactionLinkMode = FactionLinkDestination.InfoCard;
                    ApplySettingsChanges(settings);
                })
            }));
        }

        private bool DrawFactionAndItemSettings(Rect content, BuyerInfoCardSettings settings)
        {
            float columnWidth = Mathf.Max(1f, (content.width - ColumnGap) / 2f);
            Rect factionRect = new Rect(content.x, content.y, columnWidth, content.height);
            Rect itemRect = new Rect(factionRect.xMax + ColumnGap, content.y, columnWidth, content.height);
            Widgets.DrawMenuSection(factionRect);
            Widgets.DrawMenuSection(itemRect);
            bool changed = DrawFactionSettings(factionRect.ContractedBy(8f), settings);
            changed |= DrawItemSettings(itemRect.ContractedBy(8f), settings);
            return changed;
        }

        private bool DrawFactionSettings(Rect rect, BuyerInfoCardSettings settings)
        {
            bool changed = false;
            IReadOnlyList<FactionDef> factionDefs = BuyerInfoService.GetSelectableFactionDefs();
            Rect titleRect = new Rect(rect.x, rect.y, rect.width, 30f);
            Widgets.Label(new Rect(titleRect.x + 4f, titleRect.y + 3f, titleRect.width - 8f, 24f),
                "BIC_FactionOptions".Translate());

            float y = titleRect.yMax + 2f;
            changed |= DrawToggleRow(new Rect(rect.x, y, rect.width, RowHeight),
                "BIC_ExcludeHostileFactions".Translate(), ref settings.ExcludeHostileFactions);
            y += RowHeight + 2f;

            const float buttonGap = 5f;
            float buttonWidth = (rect.width - buttonGap * 2f) / 3f;
            if (Widgets.ButtonText(new Rect(rect.x, y, buttonWidth, 28f), "BIC_EnableAll".Translate()))
            {
                SetAllFactionSettings(settings, factionDefs, true);
                StopPainting();
                changed = true;
            }
            if (Widgets.ButtonText(new Rect(rect.x + buttonWidth + buttonGap, y, buttonWidth, 28f),
                "BIC_DisableAll".Translate()))
            {
                SetAllFactionSettings(settings, factionDefs, false);
                StopPainting();
                changed = true;
            }
            if (Widgets.ButtonText(new Rect(rect.x + (buttonWidth + buttonGap) * 2f, y, buttonWidth, 28f),
                "BIC_Reset".Translate()))
            {
                settings.ResetFactionSettings();
                StopPainting();
                changed = true;
            }
            y += 32f;

            Rect outRect = new Rect(rect.x, y, rect.width, Mathf.Max(1f, rect.yMax - y));
            float viewHeight = Mathf.Max(outRect.height - 1f, (factionDefs.Count + 1) * RowHeight);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, viewHeight);
            factionScrollPosition.x = 0f;
            Widgets.BeginScrollView(outRect, ref factionScrollPosition, viewRect);
            for (int index = 0; index < factionDefs.Count; index++)
            {
                float rowY = index * RowHeight;
                if (rowY + RowHeight < factionScrollPosition.y || rowY > factionScrollPosition.y + outRect.height)
                {
                    continue;
                }
                FactionDef factionDef = factionDefs[index];
                string defName = factionDef.defName;
                bool selected = settings.IsFactionEnabled(defName);
                changed |= DrawPaintRow(
                    new Rect(0f, rowY, viewRect.width, RowHeight - 2f),
                    "factions",
                    "faction:" + defName,
                    DisplayLabelUtility.GetDefLabel(factionDef),
                    selected,
                    false,
                    null,
                    defName,
                    delegate(bool value) { settings.SetFactionEnabled(defName, value); },
                    factionDef.FactionIcon,
                    factionDef.DefaultColor);
            }

            int factionlessIndex = factionDefs.Count;
            float factionlessRowY = factionlessIndex * RowHeight;
            if (factionlessRowY + RowHeight >= factionScrollPosition.y
                && factionlessRowY <= factionScrollPosition.y + outRect.height)
            {
                changed |= DrawPaintRow(
                    new Rect(0f, factionlessRowY, viewRect.width, RowHeight - 2f),
                    "factions",
                    "factionless-trade-ships",
                    "BIC_FactionlessTradeShips".Translate(),
                    settings.ShowFactionlessTradeShips,
                    false,
                    null,
                    "BIC_FactionlessTradeShipsHelp".Translate(),
                    delegate(bool value) { settings.ShowFactionlessTradeShips = value; });
            }
            Widgets.EndScrollView();
            return changed;
        }

        private bool DrawItemSettings(Rect rect, BuyerInfoCardSettings settings)
        {
            bool changed = false;
            Widgets.Label(new Rect(rect.x + 4f, rect.y + 3f, rect.width - 8f, 24f),
                "BIC_CategoryFilters".Translate());
            float y = rect.y + 32f;
            const float buttonGap = 5f;
            float buttonWidth = (rect.width - buttonGap * 2f) / 3f;
            if (Widgets.ButtonText(new Rect(rect.x, y, buttonWidth, 28f), "BIC_EnableAll".Translate()))
            {
                SetAllItemSettings(settings, true);
                StopPainting();
                changed = true;
            }
            if (Widgets.ButtonText(new Rect(rect.x + buttonWidth + buttonGap, y, buttonWidth, 28f),
                "BIC_DisableAll".Translate()))
            {
                SetAllItemSettings(settings, false);
                StopPainting();
                changed = true;
            }
            if (Widgets.ButtonText(new Rect(rect.x + (buttonWidth + buttonGap) * 2f, y, buttonWidth, 28f),
                "BIC_Reset".Translate()))
            {
                settings.ResetItemSettings();
                StopPainting();
                changed = true;
            }
            y += 32f;

            changed |= DrawToggleRow(new Rect(rect.x, y, rect.width, RowHeight),
                "BIC_ShowDeadmansApparel".Translate(), ref settings.ShowDeadmansApparel);
            y += RowHeight;
            changed |= DrawToggleRow(new Rect(rect.x, y, rect.width, RowHeight),
                "BIC_ShowBiocoded".Translate(), ref settings.ShowBiocoded);
            y += RowHeight;
            changed |= DrawToggleRow(new Rect(rect.x, y, rect.width, RowHeight),
                "BIC_ShowRotten".Translate(), ref settings.ShowRotten);
            y += RowHeight + 2f;

            List<CategoryRow> categories = GetVisibleSettingsCategories();
            Rect outRect = new Rect(rect.x, y, rect.width, Mathf.Max(1f, rect.yMax - y));
            float viewHeight = Mathf.Max(outRect.height - 1f, categories.Count * RowHeight);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, viewHeight);
            itemScrollPosition.x = 0f;
            itemScrollPosition.y = Mathf.Clamp(
                itemScrollPosition.y,
                0f,
                Mathf.Max(0f, viewRect.height - outRect.height));
            Widgets.BeginScrollView(outRect, ref itemScrollPosition, viewRect);
            for (int index = 0; index < categories.Count; index++)
            {
                float rowY = index * RowHeight;
                if (rowY + RowHeight < itemScrollPosition.y
                    || rowY > itemScrollPosition.y + outRect.height)
                {
                    continue;
                }
                CategoryRow category = categories[index];
                MultiCheckboxState state = GetCategoryState(settings, category);
                string key = category.Key;
                bool expanded = expandedCategoryKeys.Contains(key);
                bool hasSettingsChildren = key == CategoryKeys.HumanlikeRoot;
                changed |= DrawPaintRow(
                    new Rect(0f, rowY, viewRect.width, RowHeight - 2f),
                    "itemCategories",
                    "category:" + key,
                    category.Label,
                    state == MultiCheckboxState.On,
                    false,
                    state,
                    null,
                    delegate(bool value) { SetCategoryTree(settings, category, value); },
                    null,
                    null,
                    GetSettingsCategoryIndent(category) * 16f + 20f,
                    hasSettingsChildren,
                    expanded,
                    delegate
                    {
                        if (expanded)
                        {
                            expandedCategoryKeys.Remove(key);
                        }
                        else
                        {
                            expandedCategoryKeys.Add(key);
                        }
                        StopPainting();
                    });
            }
            Widgets.EndScrollView();
            return changed;
        }

        private bool DrawPaintRow(
            Rect rect,
            string listId,
            string key,
            string label,
            bool selected,
            bool disabled,
            MultiCheckboxState? multiState,
            string tooltip,
            Action<bool> setter,
            Texture2D icon = null,
            Color? iconColor = null,
            float labelIndent = 0f,
            bool isParent = false,
            bool expanded = false,
            Action toggleExpanded = null)
        {
            if (disabled)
            {
                Widgets.DrawBoxSolid(rect, new Color(1f, 1f, 1f, 0.04f));
            }
            else if (isParent)
            {
                Widgets.DrawHighlight(rect);
            }
            else
            {
                Widgets.DrawHighlightIfMouseover(rect);
            }

            Color previousColor = GUI.color;
            if (disabled)
            {
                GUI.color = new Color(previousColor.r * 0.65f, previousColor.g * 0.65f,
                    previousColor.b * 0.65f, previousColor.a);
            }
            Rect checkRect = new Rect(rect.xMax - 25f, rect.y + 3f, 22f, 22f);
            float labelX = rect.x + 6f + labelIndent;
            float labelWidth = rect.width - 38f - labelIndent;
            Rect expandRect = default(Rect);
            if (isParent)
            {
                expandRect = new Rect(labelX - 22f, rect.y + 3f, 20f, 22f);
                Widgets.Label(expandRect, expanded ? "▼" : "▶");
            }
            if (icon != null)
            {
                Rect iconRect = new Rect(rect.x + 5f, rect.y + 4f, 20f, 20f);
                if (Event.current.type == EventType.Repaint)
                {
                    Color drawColor = GUI.color;
                    GUI.color = iconColor ?? Color.white;
                    Widgets.DrawTextureFitted(iconRect, icon, 1f);
                    GUI.color = drawColor;
                }
                labelX += 24f;
                labelWidth -= 24f;
            }
            labelWidth = Mathf.Max(1f, labelWidth);
            Widgets.Label(new Rect(labelX, rect.y + 3f, labelWidth, 24f),
                label.Truncate(Mathf.Max(1f, labelWidth - 4f)));
            if (multiState.HasValue)
            {
                if (Event.current.type == EventType.Repaint)
                {
                    Widgets.CheckboxMulti(checkRect, multiState.Value);
                }
            }
            else
            {
                Widgets.CheckboxDraw(checkRect.x, checkRect.y, selected, disabled, 22f);
            }
            GUI.color = previousColor;
            if (!string.IsNullOrEmpty(tooltip))
            {
                TooltipHandler.TipRegion(rect, tooltip);
            }
            if (disabled)
            {
                return false;
            }

            Event current = Event.current;
            if (isParent
                && toggleExpanded != null
                && current.type == EventType.MouseDown
                && current.button == 0
                && expandRect.Contains(current.mousePosition))
            {
                toggleExpanded();
                current.Use();
                return false;
            }
            string paintKey = listId + ":" + key;
            if (current.type == EventType.MouseDown && current.button == 0 && rect.Contains(current.mousePosition))
            {
                paintListId = listId;
                paintValue = !selected;
                lastPaintedKey = paintKey;
                setter(paintValue);
                current.Use();
                return true;
            }
            if ((current.type == EventType.MouseDrag || current.rawType == EventType.ScrollWheel)
                && paintListId == listId
                && lastPaintedKey != paintKey
                && rect.Contains(current.mousePosition))
            {
                setter(paintValue);
                lastPaintedKey = paintKey;
                if (current.type == EventType.MouseDrag)
                {
                    current.Use();
                }
                return true;
            }
            return false;
        }

        private List<CategoryRow> GetVisibleSettingsCategories()
        {
            IReadOnlyList<CategoryRow> allRows = CategoryCatalog.SettingsCategoryRows;
            List<CategoryRow> visibleRows = new List<CategoryRow>();
            for (int index = 0; index < allRows.Count; index++)
            {
                CategoryRow row = allRows[index];
                if (IsSettingsCategoryVisible(row))
                {
                    visibleRows.Add(row);
                }
            }
            return visibleRows;
        }

        private bool IsSettingsCategoryVisible(CategoryRow row)
        {
            CategoryRow parent = row == null ? null : row.Parent;
            while (parent != null)
            {
                bool omittedThingRoot = parent.Parent == null
                    && parent.Key != CategoryKeys.HumanlikeRoot;
                if (omittedThingRoot)
                {
                    break;
                }
                if (!expandedCategoryKeys.Contains(parent.Key))
                {
                    return false;
                }
                parent = parent.Parent;
            }
            return row != null;
        }

        private static int GetSettingsCategoryIndent(CategoryRow row)
        {
            if (row == null)
            {
                return 0;
            }

            CategoryRow root = row;
            while (root.Parent != null)
            {
                root = root.Parent;
            }
            int omittedRootDepth = root.Key == CategoryKeys.HumanlikeRoot ? 0 : 1;
            return Math.Max(0, row.Depth - omittedRootDepth);
        }

        private static bool DrawToggleRow(Rect rect, string label, ref bool value, bool disabled = false)
        {
            if (disabled)
            {
                Widgets.DrawBoxSolid(rect, new Color(1f, 1f, 1f, 0.04f));
            }
            else
            {
                Widgets.DrawHighlightIfMouseover(rect);
            }
            Color previousColor = GUI.color;
            if (disabled)
            {
                GUI.color = new Color(previousColor.r * 0.65f, previousColor.g * 0.65f,
                    previousColor.b * 0.65f, previousColor.a);
            }
            Widgets.Label(new Rect(rect.x + 4f, rect.y + 4f, rect.width - 38f, 24f), label);
            Widgets.CheckboxDraw(rect.xMax - 25f, rect.y + 3f, value, disabled, 22f);
            GUI.color = previousColor;
            if (!disabled && Widgets.ButtonInvisible(rect))
            {
                value = !value;
                return true;
            }
            return false;
        }

        private static void DrawSectionHeader(Rect rect, string label)
        {
            Widgets.DrawHighlight(rect);
            Widgets.Label(new Rect(rect.x + 6f, rect.y + 3f, rect.width - 12f, 24f), label);
        }

        private static MultiCheckboxState GetCategoryState(BuyerInfoCardSettings settings, CategoryRow row)
        {
            if (row.Key != CategoryKeys.HumanlikeRoot)
            {
                return settings.IsCategoryEnabled(row.Key)
                    ? MultiCheckboxState.On
                    : MultiCheckboxState.Off;
            }

            bool anyEnabled = false;
            bool anyDisabled = false;
            AccumulateCategoryState(settings, row, ref anyEnabled, ref anyDisabled);
            if (anyEnabled && anyDisabled)
            {
                return MultiCheckboxState.Partial;
            }
            return anyEnabled ? MultiCheckboxState.On : MultiCheckboxState.Off;
        }

        private static void AccumulateCategoryState(
            BuyerInfoCardSettings settings,
            CategoryRow row,
            ref bool anyEnabled,
            ref bool anyDisabled)
        {
            if (settings.IsCategoryEnabled(row.Key))
            {
                anyEnabled = true;
            }
            else
            {
                anyDisabled = true;
            }
            for (int index = 0; index < row.Children.Count; index++)
            {
                AccumulateCategoryState(settings, row.Children[index], ref anyEnabled, ref anyDisabled);
            }
        }

        private static void SetCategoryTree(BuyerInfoCardSettings settings, CategoryRow row, bool enabled)
        {
            if (row.Key == CategoryKeys.HumanlikeRoot || IsHumanlikeChild(row))
            {
                SetCategorySubtree(settings, row, enabled);
            }
            else
            {
                settings.SetCategoryEnabled(row.Key, enabled);
            }
            UpdateAncestorStates(settings, row.Parent);
        }

        private static bool IsHumanlikeChild(CategoryRow row)
        {
            CategoryRow current = row;
            while (current != null)
            {
                if (current.Key == CategoryKeys.HumanlikeRoot)
                {
                    return true;
                }
                current = current.Parent;
            }
            return false;
        }

        private static void SetCategorySubtree(BuyerInfoCardSettings settings, CategoryRow row, bool enabled)
        {
            settings.SetCategoryEnabled(row.Key, enabled);
            for (int index = 0; index < row.Children.Count; index++)
            {
                SetCategorySubtree(settings, row.Children[index], enabled);
            }
        }

        private static void UpdateAncestorStates(BuyerInfoCardSettings settings, CategoryRow parent)
        {
            while (parent != null)
            {
                bool anyChildEnabled = false;
                for (int index = 0; index < parent.Children.Count; index++)
                {
                    if (AnyCategoryEnabled(settings, parent.Children[index]))
                    {
                        anyChildEnabled = true;
                        break;
                    }
                }
                settings.SetCategoryEnabled(parent.Key, anyChildEnabled);
                parent = parent.Parent;
            }
        }

        private static bool AnyCategoryEnabled(BuyerInfoCardSettings settings, CategoryRow row)
        {
            if (settings.IsCategoryEnabled(row.Key))
            {
                return true;
            }
            for (int index = 0; index < row.Children.Count; index++)
            {
                if (AnyCategoryEnabled(settings, row.Children[index]))
                {
                    return true;
                }
            }
            return false;
        }

        private static void SetAllItemSettings(BuyerInfoCardSettings settings, bool enabled)
        {
            settings.ShowDeadmansApparel = enabled;
            settings.ShowBiocoded = enabled;
            settings.ShowRotten = enabled;
            IReadOnlyList<CategoryRow> rows = CategoryCatalog.SettingsCategoryRows;
            for (int index = 0; index < rows.Count; index++)
            {
                settings.SetCategoryEnabled(rows[index].Key, enabled);
            }
        }

        private static void SetAllFactionSettings(
            BuyerInfoCardSettings settings,
            IReadOnlyList<FactionDef> factionDefs,
            bool enabled)
        {
            for (int index = 0; index < factionDefs.Count; index++)
            {
                settings.SetFactionEnabled(factionDefs[index].defName, enabled);
            }
            settings.ShowFactionlessTradeShips = enabled;
        }

        private void StopPainting()
        {
            paintListId = null;
            lastPaintedKey = null;
        }

        private static void ApplySettingsChanges(BuyerInfoCardSettings settings)
        {
            BuyerInfoService.NotifySettingsChanged(settings);
        }
    }
}
