using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace BuyerInfoCard
{
    internal sealed class BuyerParty
    {
        internal Faction Faction;
        internal FactionDef FactionDef;
        internal bool IsFactionlessTradeShip;
        internal readonly List<TraderKindDef> TraderKinds = new List<TraderKindDef>();

        internal string Label
        {
            get
            {
                if (IsFactionlessTradeShip)
                {
                    return "BIC_FactionlessTradeShips".Translate();
                }
                if (Faction != null)
                {
                    if (!string.IsNullOrEmpty(Faction.Name))
                    {
                        return Faction.Name;
                    }
                    return DisplayLabelUtility.GetDefLabel(Faction.def);
                }
                return DisplayLabelUtility.GetDefLabel(FactionDef);
            }
        }
    }

    internal sealed class BuyerQueryResult
    {
        internal ThingDef ThingDef;
        internal Thing Thing;
        internal CurrentSaleRestriction Restrictions;
        internal List<BuyerParty> Parties = new List<BuyerParty>();
    }

    internal static class BuyerInfoService
    {
        private const int RuleAuditIntervalTicks = 2500;
        private const int RuleCacheEntryLifetimeTicks = 60000;
        private const int LiveTraderRefreshIntervalTicks = 600;
        private const int SettlementCacheTicks = 12000;
        private const int EmptySettlementCacheTicks = 2500;

        private static readonly Dictionary<ThingDef, LinkedListNode<RuleCacheEntry>> RuleCache =
            new Dictionary<ThingDef, LinkedListNode<RuleCacheEntry>>();
        private static readonly LinkedList<RuleCacheEntry> RuleLru = new LinkedList<RuleCacheEntry>();
        private static readonly Dictionary<TraderKindDef, HashSet<FactionDef>> StaticAssociations =
            new Dictionary<TraderKindDef, HashSet<FactionDef>>();
        private static readonly Dictionary<TraderKindDef, HashSet<Faction>> LiveTraderAssociations =
            new Dictionary<TraderKindDef, HashSet<Faction>>();
        private static readonly HashSet<TraderKindDef> FactionlessOrbitalTraderKinds =
            new HashSet<TraderKindDef>();
        private static readonly Dictionary<string, NearestSettlementEntry> NearestSettlements =
            new Dictionary<string, NearestSettlementEntry>(StringComparer.Ordinal);
        private static readonly List<FactionDef> SelectableFactionDefs = new List<FactionDef>();
        private static readonly Dictionary<FactionDef, int> FactionDisplayOrder =
            new Dictionary<FactionDef, int>();

        private static readonly FieldInfo PassingShipsField =
            AccessTools.Field(typeof(PassingShipManager), "passingShips");

        private static List<TraderKindDef> allTraderKinds = new List<TraderKindDef>();
        private static int ruleCacheCapacity = 64;
        private static long definitionsFingerprint;
        private static int lastRuleAuditTick = int.MinValue;
        private static int lastLiveTraderRefreshTick = int.MinValue;
        private static int worldRevision;

        private static int memoQueryFrame = -1;
        private static Thing memoQueryThing;
        private static ThingDef memoQueryDef;
        private static BuyerQueryResult memoQuery;

        internal static void InitializeDefinitions(int cacheCapacity)
        {
            allTraderKinds = DefDatabase<TraderKindDef>.AllDefsListForReading.ToList();
            RebuildStaticAssociations();
            RebuildSelectableFactionDefs();
            definitionsFingerprint = CalculateDefinitionsFingerprint();
            lastRuleAuditTick = CurrentTick();
            ClearRuleCache();
            LiveTraderAssociations.Clear();
            lastLiveTraderRefreshTick = int.MinValue;
            ClearQueryMemo();
            SetRuleCacheCapacity(cacheCapacity);
        }

        internal static IReadOnlyList<FactionDef> GetSelectableFactionDefs()
        {
            return SelectableFactionDefs;
        }

        internal static int CurrentWorldRevision
        {
            get { return worldRevision; }
        }

        internal static void OnWorldEntered()
        {
            worldRevision++;
            ClearRuleCache();
            NearestSettlements.Clear();
            LiveTraderAssociations.Clear();
            lastLiveTraderRefreshTick = int.MinValue;
            ClearQueryMemo();
        }

        internal static void SetRuleCacheCapacity(int capacity)
        {
            ruleCacheCapacity = Mathf.Clamp(capacity, 0, 1024);
            while (RuleCache.Count > ruleCacheCapacity)
            {
                RemoveLeastRecentlyUsed();
            }
        }

        internal static void InvalidateTraderRules(string source)
        {
            ClearRuleCache();
            lastRuleAuditTick = int.MinValue;
            ClearQueryMemo();
            StatsReportUtility.Reset();
            Log.Message("[Buyer Info Card] Buyer-rule cache invalidated by " + source + ".");
        }

        internal static void NotifyWorldStateChanged()
        {
            worldRevision++;
            lastLiveTraderRefreshTick = int.MinValue;
            LiveTraderAssociations.Clear();
            ClearQueryMemo();
        }

        internal static void NotifySettingsChanged(BuyerInfoCardSettings settings)
        {
            SetRuleCacheCapacity(settings.RuleCacheCapacity);
            ClearQueryMemo();
            StatsReportUtility.Reset();
        }

        internal static ThingDef ResolveThingDef(StatRequest request)
        {
            Thing thing = request.Thing;
            ThingDef thingDef = request.Def as ThingDef;
            if (thingDef == null && thing != null)
            {
                thingDef = thing.def;
            }
            return thingDef;
        }

        internal static BuyerQueryResult GetBuyerResult(
            StatRequest request,
            BuyerInfoCardSettings settings)
        {
            Thing thing = request.Thing;
            ThingDef thingDef = ResolveThingDef(request);

            int frame = Time.frameCount;
            if (memoQuery != null
                && memoQueryFrame == frame
                && memoQueryThing == thing
                && memoQueryDef == thingDef)
            {
                return memoQuery;
            }

            bool diagnose = BuyerInfoDiagnostics.HasSubscribers;
            long started = diagnose ? Stopwatch.GetTimestamp() : 0L;
            RuleLookupMetrics metrics;
            TraderKindDef[] acceptingKinds = GetAcceptingTraderKinds(thingDef, out metrics);
            RefreshLiveTraderAssociations(false);
            List<BuyerParty> parties = ResolveBuyerParties(acceptingKinds, settings);
            CurrentSaleRestriction restrictions = CategoryCatalog.GetCurrentRestrictions(thing);

            BuyerQueryResult result = new BuyerQueryResult
            {
                ThingDef = thingDef,
                Thing = thing,
                Restrictions = restrictions,
                Parties = parties
            };

            memoQueryFrame = frame;
            memoQueryThing = thing;
            memoQueryDef = thingDef;
            memoQuery = result;

            if (diagnose)
            {
                BuyerInfoDiagnostics.Record(new BuyerInfoDiagnosticEvent
                {
                    Kind = BuyerInfoDiagnosticKind.BuyerQuery,
                    Subject = thingDef == null ? "<null>" : thingDef.defName,
                    ElapsedStopwatchTicks = Stopwatch.GetTimestamp() - started,
                    CacheHit = metrics.CacheHit,
                    TraderKindsChecked = metrics.TraderKindsChecked,
                    StockGeneratorsDeclared = metrics.StockGeneratorsDeclared,
                    Results = parties.Count,
                    RuleCacheSize = RuleCache.Count,
                    ProviderObjectsScanned = 0
                });
            }

            return result;
        }

        private static TraderKindDef[] GetAcceptingTraderKinds(ThingDef thingDef, out RuleLookupMetrics metrics)
        {
            metrics = new RuleLookupMetrics();
            if (thingDef == null || !CategoryCatalog.IsSupported(thingDef))
            {
                return Array.Empty<TraderKindDef>();
            }

            AuditDefinitionsIfDue();

            LinkedListNode<RuleCacheEntry> node;
            if (RuleCache.TryGetValue(thingDef, out node))
            {
                int age = CurrentTick() - node.Value.CreatedTick;
                if (age >= 0 && age <= RuleCacheEntryLifetimeTicks)
                {
                    RuleLru.Remove(node);
                    RuleLru.AddFirst(node);
                    metrics.CacheHit = true;
                    return node.Value.AcceptingKinds;
                }

                RuleCache.Remove(thingDef);
                RuleLru.Remove(node);
            }

            List<TraderKindDef> accepting = new List<TraderKindDef>();
            for (int index = 0; index < allTraderKinds.Count; index++)
            {
                TraderKindDef traderKind = allTraderKinds[index];
                metrics.TraderKindsChecked++;
                if (traderKind.stockGenerators != null)
                {
                    metrics.StockGeneratorsDeclared += traderKind.stockGenerators.Count;
                }

                try
                {
                    if (traderKind.WillTrade(thingDef))
                    {
                        accepting.Add(traderKind);
                    }
                }
                catch (Exception exception)
                {
                    Log.ErrorOnce(
                        "[Buyer Info Card] TraderKindDef.WillTrade failed for " + traderKind.defName + " / " + thingDef.defName + ": " + exception.Message,
                        Gen.HashCombineInt(traderKind.shortHash, thingDef.shortHash));
                }
            }

            TraderKindDef[] acceptingArray = accepting.ToArray();
            if (ruleCacheCapacity <= 0)
            {
                return acceptingArray;
            }

            RuleCacheEntry entry = new RuleCacheEntry
            {
                ThingDef = thingDef,
                AcceptingKinds = acceptingArray,
                CreatedTick = CurrentTick()
            };
            node = RuleLru.AddFirst(entry);
            RuleCache[thingDef] = node;
            while (RuleCache.Count > ruleCacheCapacity)
            {
                RemoveLeastRecentlyUsed();
            }

            return entry.AcceptingKinds;
        }

        private static List<BuyerParty> ResolveBuyerParties(
            TraderKindDef[] acceptingKinds,
            BuyerInfoCardSettings settings)
        {
            Dictionary<FactionDef, List<Faction>> liveByDef = new Dictionary<FactionDef, List<Faction>>();
            if (Find.FactionManager != null)
            {
                List<Faction> factions = Find.FactionManager.AllFactionsListForReading;
                for (int index = 0; index < factions.Count; index++)
                {
                    Faction faction = factions[index];
                    if (!FactionAllowed(faction, settings))
                    {
                        continue;
                    }

                    List<Faction> list;
                    if (!liveByDef.TryGetValue(faction.def, out list))
                    {
                        list = new List<Faction>();
                        liveByDef.Add(faction.def, list);
                    }
                    list.Add(faction);
                }
            }

            Dictionary<string, BuyerParty> parties = new Dictionary<string, BuyerParty>(StringComparer.Ordinal);
            for (int index = 0; index < acceptingKinds.Length; index++)
            {
                TraderKindDef kind = acceptingKinds[index];
                HashSet<FactionDef> factionDefs;
                if (StaticAssociations.TryGetValue(kind, out factionDefs))
                {
                    foreach (FactionDef factionDef in factionDefs)
                    {
                        List<Faction> live;
                        if (liveByDef.TryGetValue(factionDef, out live) && live.Count > 0)
                        {
                            for (int liveIndex = 0; liveIndex < live.Count; liveIndex++)
                            {
                                AddLiveParty(parties, live[liveIndex], kind, settings);
                            }
                        }
                    }
                }

                HashSet<Faction> providerFactions;
                bool hasLiveFactionAssociation = false;
                if (LiveTraderAssociations.TryGetValue(kind, out providerFactions))
                {
                    hasLiveFactionAssociation = providerFactions.Count > 0;
                    foreach (Faction faction in providerFactions)
                    {
                        AddLiveParty(parties, faction, kind, settings);
                    }
                }

                if (!hasLiveFactionAssociation
                    && settings.ShowFactionlessTradeShips
                    && FactionlessOrbitalTraderKinds.Contains(kind))
                {
                    AddFactionlessTradeShipParty(parties, kind);
                }
            }

            List<BuyerParty> result = parties.Values
                .OrderBy(GetFactionDisplayOrder)
                .ThenBy(party => party.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            for (int index = 0; index < result.Count; index++)
            {
                result[index].TraderKinds.Sort(delegate(TraderKindDef left, TraderKindDef right)
                {
                    return StringComparer.CurrentCultureIgnoreCase.Compare(
                        DisplayLabelUtility.GetDefLabel(left, string.Empty),
                        DisplayLabelUtility.GetDefLabel(right, string.Empty));
                });
            }
            return result;
        }

        private static int GetFactionDisplayOrder(BuyerParty party)
        {
            int order;
            return party != null
                && party.FactionDef != null
                && FactionDisplayOrder.TryGetValue(party.FactionDef, out order)
                    ? order
                    : int.MaxValue;
        }

        private static void AddFactionlessTradeShipParty(
            Dictionary<string, BuyerParty> parties,
            TraderKindDef traderKind)
        {
            const string key = "factionless-orbital-traders";
            BuyerParty party;
            if (!parties.TryGetValue(key, out party))
            {
                party = new BuyerParty { IsFactionlessTradeShip = true };
                parties.Add(key, party);
            }
            if (traderKind != null && !party.TraderKinds.Contains(traderKind))
            {
                party.TraderKinds.Add(traderKind);
            }
        }

        private static void AddLiveParty(
            Dictionary<string, BuyerParty> parties,
            Faction faction,
            TraderKindDef traderKind,
            BuyerInfoCardSettings settings)
        {
            if (!FactionAllowed(faction, settings))
            {
                return;
            }

            string key = "faction:" + faction.GetUniqueLoadID();
            BuyerParty party;
            if (!parties.TryGetValue(key, out party))
            {
                party = new BuyerParty { Faction = faction, FactionDef = faction.def };
                parties.Add(key, party);
            }
            if (traderKind != null && !party.TraderKinds.Contains(traderKind))
            {
                party.TraderKinds.Add(traderKind);
            }

            string defKey = faction.def == null ? null : "def:" + faction.def.defName;
            if (defKey != null)
            {
                parties.Remove(defKey);
            }
        }

        private static bool FactionAllowed(Faction faction, BuyerInfoCardSettings settings)
        {
            if (faction == null || faction.IsPlayer || faction.def == null || faction.def.hidden)
            {
                return false;
            }
            if (!settings.IsFactionEnabled(faction.def.defName))
            {
                return false;
            }
            Faction player = Faction.OfPlayerSilentFail;
            return !settings.ExcludeHostileFactions
                || player == null
                || !faction.HostileTo(player);
        }

        private static void RebuildStaticAssociations()
        {
            StaticAssociations.Clear();
            FactionlessOrbitalTraderKinds.Clear();
            List<FactionDef> factionDefs = DefDatabase<FactionDef>.AllDefsListForReading;
            for (int index = 0; index < factionDefs.Count; index++)
            {
                FactionDef factionDef = factionDefs[index];
                AddAssociationList(factionDef, factionDef.caravanTraderKinds);
                AddAssociationList(factionDef, factionDef.orbitalTraderKinds);
                AddAssociationList(factionDef, factionDef.visitorTraderKinds);
                AddAssociationList(factionDef, factionDef.baseTraderKinds);
            }

            for (int index = 0; index < allTraderKinds.Count; index++)
            {
                TraderKindDef traderKind = allTraderKinds[index];
                if (traderKind.faction != null)
                {
                    AddAssociation(traderKind, traderKind.faction);
                }
            }

            for (int index = 0; index < allTraderKinds.Count; index++)
            {
                TraderKindDef traderKind = allTraderKinds[index];
                if (traderKind != null
                    && traderKind.orbital
                    && !StaticAssociations.ContainsKey(traderKind))
                {
                    FactionlessOrbitalTraderKinds.Add(traderKind);
                }
            }
        }

        private static void RebuildSelectableFactionDefs()
        {
            SelectableFactionDefs.Clear();
            FactionDisplayOrder.Clear();
            HashSet<FactionDef> remaining = new HashSet<FactionDef>();
            List<FactionDef> factionDefs = DefDatabase<FactionDef>.AllDefsListForReading;
            for (int index = 0; index < factionDefs.Count; index++)
            {
                FactionDef factionDef = factionDefs[index];
                if (factionDef != null && !factionDef.isPlayer && !factionDef.hidden)
                {
                    remaining.Add(factionDef);
                }
            }

            List<FactionDef> configurable = FactionGenerator.ConfigurableFactions
                .Where(remaining.Contains)
                .OrderBy(factionDef => factionDef.configurationListOrderPriority)
                .ThenBy(factionDef => DisplayLabelUtility.GetDefLabel(factionDef), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(factionDef => factionDef.defName, StringComparer.Ordinal)
                .ToList();
            for (int index = 0; index < configurable.Count; index++)
            {
                FactionDef factionDef = configurable[index];
                if (remaining.Remove(factionDef))
                {
                    SelectableFactionDefs.Add(factionDef);
                }
            }

            List<FactionDef> additional = remaining.ToList();
            additional.Sort(delegate(FactionDef left, FactionDef right)
            {
                int labelOrder = StringComparer.CurrentCultureIgnoreCase.Compare(
                    DisplayLabelUtility.GetDefLabel(left),
                    DisplayLabelUtility.GetDefLabel(right));
                return labelOrder != 0
                    ? labelOrder
                    : StringComparer.Ordinal.Compare(left.defName, right.defName);
            });
            SelectableFactionDefs.AddRange(additional);
            for (int index = 0; index < SelectableFactionDefs.Count; index++)
            {
                FactionDisplayOrder[SelectableFactionDefs[index]] = index;
            }
        }

        private static void AddAssociationList(FactionDef factionDef, List<TraderKindDef> traderKinds)
        {
            if (traderKinds == null)
            {
                return;
            }

            for (int index = 0; index < traderKinds.Count; index++)
            {
                AddAssociation(traderKinds[index], factionDef);
            }
        }

        private static void AddAssociation(TraderKindDef traderKind, FactionDef factionDef)
        {
            if (traderKind == null || factionDef == null)
            {
                return;
            }

            HashSet<FactionDef> factions;
            if (!StaticAssociations.TryGetValue(traderKind, out factions))
            {
                factions = new HashSet<FactionDef>();
                StaticAssociations.Add(traderKind, factions);
            }
            factions.Add(factionDef);
        }

        private static void RefreshLiveTraderAssociations(bool force)
        {
            if (Current.Game == null || Find.World == null)
            {
                LiveTraderAssociations.Clear();
                lastLiveTraderRefreshTick = CurrentTick();
                return;
            }

            int tick = CurrentTick();
            if (!force && lastLiveTraderRefreshTick != int.MinValue && tick - lastLiveTraderRefreshTick < LiveTraderRefreshIntervalTicks)
            {
                return;
            }

            bool diagnose = BuyerInfoDiagnostics.HasSubscribers;
            long started = diagnose ? Stopwatch.GetTimestamp() : 0L;
            int scanned = 0;
            LiveTraderAssociations.Clear();

            if (Find.WorldObjects != null)
            {
                List<Settlement> settlements = Find.WorldObjects.Settlements;
                for (int index = 0; index < settlements.Count; index++)
                {
                    Settlement settlement = settlements[index];
                    scanned++;
                    AddLiveTrader(settlement, settlement == null ? null : settlement.Faction);
                }
            }

            List<Map> maps = Find.Maps;
            if (maps != null)
            {
                for (int mapIndex = 0; mapIndex < maps.Count; mapIndex++)
                {
                    Map map = maps[mapIndex];
                    if (map == null)
                    {
                        continue;
                    }

                    IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
                    for (int pawnIndex = 0; pawnIndex < pawns.Count; pawnIndex++)
                    {
                        Pawn pawn = pawns[pawnIndex];
                        scanned++;
                        if (pawn != null && pawn.TraderKind != null)
                        {
                            AddLiveTrader(pawn, pawn.Faction);
                        }
                    }

                    if (PassingShipsField != null && map.passingShipManager != null)
                    {
                        List<PassingShip> ships = PassingShipsField.GetValue(map.passingShipManager) as List<PassingShip>;
                        if (ships != null)
                        {
                            for (int shipIndex = 0; shipIndex < ships.Count; shipIndex++)
                            {
                                PassingShip ship = ships[shipIndex];
                                scanned++;
                                ITrader trader = ship as ITrader;
                                if (trader != null)
                                {
                                    AddLiveTrader(trader, ship.Faction);
                                }
                            }
                        }
                    }
                }
            }

            lastLiveTraderRefreshTick = tick;
            if (diagnose)
            {
                BuyerInfoDiagnostics.Record(new BuyerInfoDiagnosticEvent
                {
                    Kind = BuyerInfoDiagnosticKind.LiveTraderRefresh,
                    Subject = "world",
                    ElapsedStopwatchTicks = Stopwatch.GetTimestamp() - started,
                    Results = LiveTraderAssociations.Count,
                    RuleCacheSize = RuleCache.Count,
                    ProviderObjectsScanned = scanned
                });
            }
        }

        private static void AddLiveTrader(ITrader trader, Faction faction)
        {
            if (trader == null || faction == null || faction.IsPlayer)
            {
                return;
            }

            TraderKindDef kind;
            try
            {
                kind = trader.TraderKind;
            }
            catch
            {
                return;
            }

            if (kind == null)
            {
                return;
            }

            HashSet<Faction> factions;
            if (!LiveTraderAssociations.TryGetValue(kind, out factions))
            {
                factions = new HashSet<Faction>();
                LiveTraderAssociations.Add(kind, factions);
            }
            factions.Add(faction);
        }

        internal static Settlement FindNearestSettlement(Faction faction, PlanetTile origin, out float distance)
        {
            distance = 0f;
            if (faction == null || !origin.Valid || Find.WorldObjects == null || Find.WorldGrid == null)
            {
                return null;
            }

            bool diagnose = BuyerInfoDiagnostics.HasSubscribers;
            long started = diagnose ? Stopwatch.GetTimestamp() : 0L;
            int tick = CurrentTick();
            string key = faction.GetUniqueLoadID();
            NearestSettlementEntry cached;
            if (NearestSettlements.TryGetValue(key, out cached)
                && cached.Origin == origin
                && cached.WorldRevision == worldRevision
                && tick <= cached.ExpiresTick)
            {
                if (cached.Settlement == null || CachedSettlementStillValid(cached.Settlement, faction))
                {
                    distance = cached.Distance;
                    RecordNearestDiagnostic(diagnose, started, key, true, cached.Settlement == null ? 0 : 1, 0);
                    return cached.Settlement;
                }
            }

            Settlement nearest = null;
            float nearestDistance = float.MaxValue;
            int scanned = 0;
            List<Settlement> settlements = Find.WorldObjects.Settlements;
            for (int index = 0; index < settlements.Count; index++)
            {
                Settlement candidate = settlements[index];
                scanned++;
                if (!SettlementStillValid(candidate, faction))
                {
                    continue;
                }

                float candidateDistance;
                try
                {
                    candidateDistance = Find.WorldGrid.ApproxDistanceInTiles(origin, candidate.Tile);
                }
                catch
                {
                    continue;
                }

                if (candidateDistance < nearestDistance)
                {
                    nearestDistance = candidateDistance;
                    nearest = candidate;
                }
            }

            distance = nearest == null ? 0f : nearestDistance;
            NearestSettlements[key] = new NearestSettlementEntry
            {
                Origin = origin,
                Settlement = nearest,
                Distance = distance,
                WorldRevision = worldRevision,
                ExpiresTick = tick + (nearest == null ? EmptySettlementCacheTicks : SettlementCacheTicks)
            };
            RecordNearestDiagnostic(diagnose, started, key, false, nearest == null ? 0 : 1, scanned);
            return nearest;
        }

        private static bool SettlementStillValid(Settlement settlement, Faction faction)
        {
            return settlement != null
                && !settlement.Destroyed
                && settlement.Spawned
                && settlement.Faction == faction;
        }

        private static bool CachedSettlementStillValid(Settlement settlement, Faction faction)
        {
            return SettlementStillValid(settlement, faction)
                && Find.WorldObjects != null
                && Find.WorldObjects.Settlements != null
                && Find.WorldObjects.Settlements.Contains(settlement);
        }

        private static void RecordNearestDiagnostic(bool diagnose, long started, string subject, bool hit, int results, int scanned)
        {
            if (!diagnose)
            {
                return;
            }
            BuyerInfoDiagnostics.Record(new BuyerInfoDiagnosticEvent
            {
                Kind = BuyerInfoDiagnosticKind.NearestSettlement,
                Subject = subject,
                ElapsedStopwatchTicks = Stopwatch.GetTimestamp() - started,
                CacheHit = hit,
                Results = results,
                RuleCacheSize = RuleCache.Count,
                ProviderObjectsScanned = scanned
            });
        }

        internal static PlanetTile CurrentOriginTile()
        {
            if (Find.CurrentMap != null)
            {
                return Find.CurrentMap.Tile;
            }
            if (Find.AnyPlayerHomeMap != null)
            {
                return Find.AnyPlayerHomeMap.Tile;
            }
            if (Find.WorldObjects != null)
            {
                List<Caravan> caravans = Find.WorldObjects.Caravans;
                for (int index = 0; index < caravans.Count; index++)
                {
                    Caravan caravan = caravans[index];
                    if (caravan != null && caravan.Faction != null && caravan.Faction.IsPlayer)
                    {
                        return caravan.Tile;
                    }
                }
            }
            return PlanetTile.Invalid;
        }

        private static void AuditDefinitionsIfDue()
        {
            int tick = CurrentTick();
            if (lastRuleAuditTick != int.MinValue && tick - lastRuleAuditTick < RuleAuditIntervalTicks)
            {
                return;
            }

            bool diagnose = BuyerInfoDiagnostics.HasSubscribers;
            long started = diagnose ? Stopwatch.GetTimestamp() : 0L;
            long currentFingerprint = CalculateDefinitionsFingerprint();
            bool changed = currentFingerprint != definitionsFingerprint;
            if (changed)
            {
                allTraderKinds = DefDatabase<TraderKindDef>.AllDefsListForReading.ToList();
                RebuildStaticAssociations();
                RebuildSelectableFactionDefs();
                ClearRuleCache();
                LiveTraderAssociations.Clear();
                lastLiveTraderRefreshTick = int.MinValue;
                definitionsFingerprint = CalculateDefinitionsFingerprint();
                ClearQueryMemo();
                Log.Message("[Buyer Info Card] Runtime trader definitions changed; caches rebuilt.");
            }
            lastRuleAuditTick = tick;

            if (diagnose)
            {
                BuyerInfoDiagnostics.Record(new BuyerInfoDiagnosticEvent
                {
                    Kind = BuyerInfoDiagnosticKind.RuleAudit,
                    Subject = changed ? "changed" : "unchanged",
                    ElapsedStopwatchTicks = Stopwatch.GetTimestamp() - started,
                    TraderKindsChecked = allTraderKinds.Count,
                    RuleCacheSize = RuleCache.Count
                });
            }
        }

        private static long CalculateDefinitionsFingerprint()
        {
            unchecked
            {
                long hash = 1469598103934665603L;
                List<TraderKindDef> kinds = DefDatabase<TraderKindDef>.AllDefsListForReading;
                for (int index = 0; index < kinds.Count; index++)
                {
                    TraderKindDef kind = kinds[index];
                    hash = (hash ^ RuntimeHelpers.GetHashCode(kind)) * 1099511628211L;
                    hash = (hash ^ (kind.stockGenerators == null ? 0 : kind.stockGenerators.Count)) * 1099511628211L;
                    if (kind.stockGenerators != null)
                    {
                        for (int generatorIndex = 0; generatorIndex < kind.stockGenerators.Count; generatorIndex++)
                        {
                            StockGenerator generator = kind.stockGenerators[generatorIndex];
                            hash = (hash ^ (generator == null ? 0 : RuntimeHelpers.GetHashCode(generator))) * 1099511628211L;
                        }
                    }
                    hash = (hash ^ (kind.faction == null ? 0 : RuntimeHelpers.GetHashCode(kind.faction))) * 1099511628211L;
                    hash = (hash ^ (kind.orbital ? 1 : 0)) * 1099511628211L;
                }

                List<FactionDef> factionDefs = DefDatabase<FactionDef>.AllDefsListForReading;
                for (int index = 0; index < factionDefs.Count; index++)
                {
                    FactionDef factionDef = factionDefs[index];
                    hash = HashTraderList(hash, factionDef.caravanTraderKinds);
                    hash = HashTraderList(hash, factionDef.orbitalTraderKinds);
                    hash = HashTraderList(hash, factionDef.visitorTraderKinds);
                    hash = HashTraderList(hash, factionDef.baseTraderKinds);
                }
                return hash;
            }
        }

        private static long HashTraderList(long hash, List<TraderKindDef> list)
        {
            unchecked
            {
                hash = (hash ^ (list == null ? 0 : list.Count)) * 1099511628211L;
                if (list == null)
                {
                    return hash;
                }
                for (int index = 0; index < list.Count; index++)
                {
                    TraderKindDef kind = list[index];
                    hash = (hash ^ (kind == null ? 0 : RuntimeHelpers.GetHashCode(kind))) * 1099511628211L;
                }
                return hash;
            }
        }

        private static int CurrentTick()
        {
            return Current.Game == null || Current.Game.tickManager == null
                ? 0
                : Current.Game.tickManager.TicksGame;
        }

        private static void ClearRuleCache()
        {
            RuleCache.Clear();
            RuleLru.Clear();
        }

        private static void RemoveLeastRecentlyUsed()
        {
            LinkedListNode<RuleCacheEntry> last = RuleLru.Last;
            if (last == null)
            {
                return;
            }
            RuleCache.Remove(last.Value.ThingDef);
            RuleLru.RemoveLast();
        }

        private static void ClearQueryMemo()
        {
            memoQueryFrame = -1;
            memoQueryThing = null;
            memoQueryDef = null;
            memoQuery = null;
        }

        private sealed class RuleCacheEntry
        {
            internal ThingDef ThingDef;
            internal TraderKindDef[] AcceptingKinds;
            internal int CreatedTick;
        }

        private struct RuleLookupMetrics
        {
            internal bool CacheHit;
            internal int TraderKindsChecked;
            internal int StockGeneratorsDeclared;
        }

        private sealed class NearestSettlementEntry
        {
            internal PlanetTile Origin;
            internal Settlement Settlement;
            internal float Distance;
            internal int WorldRevision;
            internal int ExpiresTick;
        }
    }
}
