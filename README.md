# Buyer Info Card / 收购方信息卡

RimWorld 1.6 Mod。它把 `BuyerInfoCard_Buyers` 作为普通 `StatDef` 注入有效的 `ThingDef`，所以原版 InfoCard 与沿用统计条目的信息卡 Mod 都能读取同一结果。

## 规则

- 先用 `TradeUtility.EverPlayerSellable` 排除蓝图、框架、投射物、零价值且非尸体的定义，以及原本不可交易的定义。
- 对每个定义调用当前 `TraderKindDef.WillTrade`，再通过 `FactionDef` 的 caravan/orbital/visitor/base 商人列表与 `TraderKindDef.faction` 反查派系。
- `orbital=true` 且没有上述任何派系关联的商人类型统一归入“无派系商船”；若运行时观察到实际派系，则以实际派系为准。
- 额外按需刷新当前世界中的据点交易者、来访商人和轨道商人关联；600 tick 只是下次信息卡查询时的刷新门槛，不存在后台扫描器。
- 收购规则使用可配置容量的 LRU，默认 64 个 ThingDef，可在 0–1024 间直接输入；0 表示关闭规则 LRU。条目在 60000 tick 后于下一次查询时失效。
- 每 2500 tick 在下一次查询时低频检查商人列表结构。Faction Gear Customizer 的买卖清单修改会立即清空规则缓存，所需物品在下一次信息卡查询时重算。
- 最近据点按当前地图、任一玩家主地图、玩家商队的顺序选择起点。有效结果缓存 12000 tick，无据点结果缓存 2500 tick；缓存命中时仍会确认据点未摧毁、仍在世界列表且派系一致。据点新增、移除、摧毁或换派系会使缓存失效，已打开的 InfoCard 也会在下一帧重建据点文本与超链接。
- 每个 InfoCard 窗口第一次生成的收购结果会冻结到窗口关闭；规则、派系关系或设置在此期间变化，也只会反映在下一次打开的信息卡中。

## 结构与缓存失效

信息卡与 Harmony UI 只负责窗口会话、图标和超链接行为；展示层把查询数据转换为摘要、详情文字与链接描述；查询层负责收购规则、派系解析和世界数据缓存。依赖方向为 `InfoCard / Harmony UI → Presentation → Query`，查询层不反向操作信息卡窗口。

| 事件 | 立即失效或更新 | 有意保留 |
| --- | --- | --- |
| 显示、派系过滤或分类设置变化 | 当前帧查询 memo、统计报告；缓存容量同步并按需淘汰 | 已打开 InfoCard 的冻结结果、收购规则、实时商人、最近据点 |
| 进入新游戏或载入存档 | 世界版本、收购规则、实时商人、最近据点、当前帧查询 memo | 无跨世界查询结果 |
| 据点新增、移除、摧毁、换派系，或派系列表变化 | 世界版本、实时商人、当前帧查询 memo；最近据点条目通过版本号失效；已打开 InfoCard 的详情与链接在下一帧重建 | 收购规则；已冻结的派系列表摘要 |
| Def 热重载 | 分类、静态关联、派系列表、收购规则、实时商人、当前帧查询 memo | 最近据点；已打开 InfoCard 的冻结结果 |
| 运行时 TraderKind 结构指纹变化 | 静态关联、派系列表、收购规则、实时商人、当前帧查询 memo | 最近据点；已打开 InfoCard 的冻结结果 |
| Faction Gear Customizer 修改规则或手动清空 | 收购规则、规则审计时点、当前帧查询 memo、统计报告 | 实时商人、最近据点、已打开 InfoCard 的冻结结果 |

600 tick、2,500 tick、12,000 tick、60,000 tick 均只是下一次查询时的刷新或失效门槛，不会创建后台扫描任务。`CardSnapshot` 与本帧 memo 生命周期不同，前者服务于窗口冻结，后者只消除同一帧内的重复计算，因此有意保持独立。

## 分类与实例过滤

设置窗口分为“基础设置”和“派系与物品”两页。“派系与物品”页左右各占一半：左侧按新建游戏的 `configurationListOrderPriority` 从小到大、由上到下列出当前加载的派系定义，其他派系按名称正序追加，最后放置没有图标且默认启用的“无派系商船”；右侧的死者衣物、生物编码与腐坏开关固定位于批量按钮下方，分类列表在其下方单独滚动。分类列表从上到下依次为类人种族、动物，然后是按原版存储区顺序排列的其他顶层 `ThingCategoryDef`。只有类人种族保留殖民者、囚犯、奴隶和其他子项，并按 How Did This Entity Die 的风格使用高亮底色和 `▶/▼` 展开标记；普通物品大类不显示子项，它们的开关直接控制整个大类，不再受旧版隐藏子项设置影响。派系和除“殖民者”外的分类默认开启，“殖民者”默认关闭。两栏均提供“全部启用”“全部取消”“重置”，列表复选框统一位于行末并支持拖动涂选。类人种族按 `RaceProps.Humanlike` 判断，因此不硬依赖 HAR。

死者衣物、生物编码和腐坏三个实例过滤默认关闭并各自独立生效；无法归入这三类的其他当前出售限制始终隐藏。允许显示时，行内会标注“当前不可出售”，但仍展示底层定义对应的潜在收购方。定义上永久不可出售的物品仍由 `TradeUtility.EverPlayerSellable` 排除。派系列表使用通用定义名称，不依赖当前存档是否生成了该派系；真正显示结果时只保留存档中已经生成且被设置允许的派系。“自动排除敌对派系”默认关闭。

摘要默认使用实际派系图标并按可用宽度显示多个，空间不足时使用 `+N`；“无派系商船”从不绘制图标，混合结果中始终折叠进 `+N`，只有它一个收购方时回退为纯文字。摘要与详情的派系顺序均跟随设置页中的派系顺序。详情使用纯文本连续显示“派系名称（最近据点：名称，距离：格数）→ `•` 商人类型”，空名称的商人类型会被忽略；无派系商船只显示名称和商人类型，不提供据点或派系链接。可在基础设置中分别隐藏最近据点或具体商人类型。链接显示为“派系-名称”与“最近定居点-名称”；派系链接可在“只打开派系菜单”和“只创建新 InfoCard”之间选择，默认打开派系菜单；最近据点链接会关闭当前信息卡并跳转、选中世界地图上的据点。当前世界没有实际 `Faction` 实例的定义级派系不会显示；实际派系仍存在但没有据点时，只省略括号中的据点信息及定居点链接。

## 语言

简体中文 | 繁體中文 | English | 日本語 | 한국어 | Русский

## 游戏内验证清单

1. 以 Harmony → 本 Mod → 可选监控 Mod 的顺序完整重启游戏。
2. 打开普通物品、动物、殖民者、囚犯、奴隶与 HAR 类人的 InfoCard，确认“收购方”行与分类设置一致。
3. 选择“收购方”行，确认每个当前派系可打开链接；有据点时，确认最近据点链接跳到世界地图并选中目标。
4. 分别测试死者衣物、生物编码武器和腐坏食物的三个独立实例过滤开关。
5. 在 Faction Gear Customizer 应用一项收购清单修改后重新打开 InfoCard，确认结果无需重启即可变化。
6. 启用 Gene Trader 或 MoeLotl: Rigor Mortis，确认无派系轨道商人出现在“无派系商船”下；混合结果只在 `+N` 提示中列出该组，只有该组时显示纯文字。

这些步骤需要真实游戏会话；仅编译成功不能替代它们。

---

# Buyer Info Card

A RimWorld 1.6 mod. It injects `BuyerInfoCard_Buyers` into eligible `ThingDef`s as a regular `StatDef`, allowing both the vanilla InfoCard and InfoCard mods that use stat entries to read the same results.

## Rules

- Definitions that are blueprints, frames, projectiles, valueless non-corpses, or otherwise never tradable are first excluded through `TradeUtility.EverPlayerSellable`.
- For each definition, the mod calls the current `TraderKindDef.WillTrade`, then resolves factions from each `FactionDef`'s caravan, orbital, visitor, and base trader lists, as well as from `TraderKindDef.faction`.
- Orbital trader kinds with `orbital=true` and no faction association found through those sources are grouped under “Factionless trade ships.” If a faction is observed at runtime, the actual faction takes precedence.
- Associations for settlement traders, visiting traders, and orbital traders in the current world are refreshed on demand. The 600-tick interval is only a refresh threshold checked by the next InfoCard query; there is no background scanner.
- Buyer rules use an LRU with configurable capacity. The default is 64 `ThingDef`s and accepts direct input from 0 to 1024; 0 disables the rule LRU. Entries expire after 60,000 ticks when next queried.
- Trader-list structure is checked infrequently, every 2,500 ticks on the next query. Changes to trade lists made by Faction Gear Customizer immediately clear the rule cache, so affected items are recalculated on their next InfoCard query.
- The nearest settlement is resolved using the current map, any player-owned primary map, then a player caravan as the starting point. Valid results are cached for 12,000 ticks and no-settlement results for 2,500 ticks. A cache hit still verifies that the settlement has not been destroyed, remains in the world list, and belongs to the same faction. Adding, removing, destroying, or transferring a settlement invalidates the cache; an open InfoCard rebuilds its settlement text and hyperlinks on the next frame.
- Buyer results generated for an InfoCard are frozen until that window closes. Changes to rules, faction relations, or settings during that time appear the next time the InfoCard is opened.

## Structure and cache invalidation

The InfoCard and Harmony UI layers handle only window sessions, icons, and hyperlink behavior. The presentation layer converts query data into summaries, detail text, and link descriptions. The query layer owns buyer rules, faction resolution, and world-data caches. Dependencies flow in one direction: `InfoCard / Harmony UI → Presentation → Query`; the query layer never manipulates the InfoCard UI.

| Event | Invalidated or updated immediately | Intentionally preserved |
| --- | --- | --- |
| Display, faction-filter, or category-setting change | Current-frame query memo, stat report; cache capacity is synchronized and entries evicted as needed | Frozen results in open InfoCards, buyer rules, live traders, nearest settlements |
| Starting a new game or loading a save | World version, buyer rules, live traders, nearest settlements, current-frame query memo | No query results are preserved across worlds |
| Settlement added, removed, destroyed, or transferred; faction list changed | World version, live traders, current-frame query memo; nearest-settlement entries expire through the version number; detail text and links in open InfoCards rebuild on the next frame | Buyer rules; faction-list summaries already frozen in an open InfoCard |
| Def hot reload | Categories, static associations, faction list, buyer rules, live traders, current-frame query memo | Nearest settlements; frozen results in open InfoCards |
| Runtime `TraderKind` structure fingerprint changed | Static associations, faction list, buyer rules, live traders, current-frame query memo | Nearest settlements; frozen results in open InfoCards |
| Faction Gear Customizer changes rules or is manually cleared | Buyer rules, rule-audit schedule, current-frame query memo, stat report | Live traders, nearest settlements, frozen results in open InfoCards |

The 600-, 2,500-, 12,000-, and 60,000-tick intervals are only refresh or expiry thresholds evaluated on the next query; they do not create background scanning jobs. `CardSnapshot` and the current-frame memo have distinct lifetimes: the former freezes a window's results, while the latter only removes duplicate work within one frame, so they intentionally remain separate.

## Category and instance filters

The settings window has “Basic” and “Factions & Items” pages. The latter is split evenly into two columns. The left column lists faction definitions from top to bottom by ascending new-game `configurationListOrderPriority`, followed by all remaining factions in alphabetical order, and finally the iconless, enabled-by-default “Factionless trade ships” entry. In the right column, the tainted-apparel, biocoded, and rotten-item toggles remain directly below the bulk-action buttons, with the independently scrolling category list beneath them. Categories are ordered as humanlike races, animals, then all other top-level `ThingCategoryDef`s in vanilla stockpile order. Only humanlike races keep the Colonists, Prisoners, Slaves, and Others children, using a highlighted background and `▶/▼` expansion markers in the style of How Did This Entity Die. Ordinary item categories show no children; their switches control the entire category directly and are no longer affected by hidden legacy child settings. Factions and all categories except Colonists are enabled by default; Colonists are disabled by default. Both columns provide Enable all, Disable all, and Reset actions. Checkboxes are consistently placed at the end of each row and support click-drag painting. Humanlike races are detected through `RaceProps.Humanlike`, so there is no hard dependency on HAR.

The tainted-apparel, biocoded, and rotten-item instance filters are disabled by default and work independently. Other current-instance selling restrictions that do not belong to those three groups always remain hidden. When one of these groups is allowed, its row is marked “Currently unsellable” while still showing the potential buyers for the underlying definition. Items whose definitions are permanently unsellable remain excluded through `TradeUtility.EverPlayerSellable`. The faction list uses generic definition names and does not depend on whether the current save has generated those factions. Results themselves retain only generated factions that are allowed by the settings. “Automatically exclude hostile factions” is disabled by default.

The summary uses actual faction icons by default and displays as many as fit, falling back to `+N` when space runs out. “Factionless trade ships” never draws an icon: it is always folded into `+N` when mixed with other results and falls back to plain text when it is the only buyer. Factions follow the settings-page order in both the summary and details. Details continuously display plain text in the form “Faction name (nearest settlement: name, distance: tiles) → `•` trader kind”; unnamed trader kinds are ignored. Factionless trade ships show only their group name and trader kinds, with no settlement or faction links. The nearest settlement and specific trader kinds can be hidden independently in Basic settings. Links appear as “Faction — Name” and “Nearest settlement — Name.” Faction links can either open only the faction menu or create only a new InfoCard; the faction menu is the default. A nearest-settlement link closes the current InfoCard, switches to the world map, and selects that settlement. Definition-level factions without an actual `Faction` instance in the current world are not shown. If a faction still exists but has no settlement, only the parenthetical settlement details and settlement link are omitted.

## Languages

简体中文 | 繁體中文 | English | 日本語 | 한국어 | Русский

## In-game validation checklist

1. Fully restart the game with the load order Harmony → this mod → optional monitoring mod.
2. Open the InfoCards for ordinary items, animals, colonists, prisoners, slaves, and HAR humanlikes; confirm that the Buyers row matches the category settings.
3. Select the Buyers row and confirm that every current faction link opens correctly. Where settlements exist, confirm that the nearest-settlement link switches to the world map and selects the destination.
4. Test the three independent instance-filter toggles for tainted apparel, biocoded weapons, and rotten food.
5. Apply a trade-list change in Faction Gear Customizer, reopen the InfoCard, and confirm that the results change without restarting the game.
6. Enable Gene Trader or MoeLotl: Rigor Mortis and confirm that factionless orbital traders appear under “Factionless trade ships.” In mixed results, this group should appear only within `+N`; when it is the only group, it should appear as plain text.

These checks require a real game session; a successful build alone cannot replace them.
