using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace BuyerInfoCard
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        private const string HarmonyId = "vorsel.buyerinfocard";
        private const string BuildId = "20260914-fgc-defensive-reset";
        private static readonly Harmony Harmony = new Harmony(HarmonyId);

        static Bootstrap()
        {
            PatchRequired(
                AccessTools.Method(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.Add)),
                postfix: nameof(WorldObjectListChanged));
            PatchRequired(
                AccessTools.Method(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.Remove)),
                postfix: nameof(WorldObjectListChanged));
            PatchRequired(
                AccessTools.Method(typeof(WorldObject), nameof(WorldObject.SetFaction)),
                prefix: nameof(WorldObjectSetFactionPrefix),
                postfix: nameof(WorldObjectSetFactionPostfix));
            PatchRequired(
                AccessTools.Method(typeof(WorldObject), nameof(WorldObject.Destroy)),
                prefix: nameof(WorldObjectDestroyPrefix));
            PatchRequired(
                AccessTools.Method(typeof(FactionManager), nameof(FactionManager.Add)),
                postfix: nameof(FactionListChanged));
            PatchRequired(
                AccessTools.Method(typeof(FactionManager), "Remove"),
                postfix: nameof(FactionListChanged));
            PatchRequired(
                AccessTools.Method(typeof(PlayDataLoader), nameof(PlayDataLoader.HotReloadDefs)),
                postfix: nameof(DefsHotReloaded));

            FactionGearCustomizerCompatibility.Apply(Harmony);
            InfoCardUiPatches.Apply(Harmony);
            LongEventHandler.ExecuteWhenFinished(InitializeAfterDefs);
        }

        private static void InitializeAfterDefs()
        {
            CategoryCatalog.RebuildAndInjectStat();
            BuyerInfoService.InitializeDefinitions(BuyerInfoCardMod.Settings.RuleCacheCapacity);
            Log.Message("[Buyer Info Card] Initialized for RimWorld 1.6; build=" + BuildId + ".");
        }

        private static void PatchRequired(MethodBase target, string prefix = null, string postfix = null)
        {
            if (target == null)
            {
                Log.Error("[Buyer Info Card] Required RimWorld 1.6 patch target was not found.");
                return;
            }

            HarmonyMethod prefixMethod = prefix == null
                ? null
                : new HarmonyMethod(typeof(Bootstrap), prefix);
            HarmonyMethod postfixMethod = postfix == null
                ? null
                : new HarmonyMethod(typeof(Bootstrap), postfix);
            Harmony.Patch(target, prefixMethod, postfixMethod);
        }

        private static void WorldObjectListChanged(WorldObject __0)
        {
            if (__0 is Settlement)
            {
                BuyerInfoService.NotifyWorldStateChanged();
            }
        }

        private static void WorldObjectSetFactionPrefix(WorldObject __instance, out Faction __state)
        {
            __state = __instance == null ? null : __instance.Faction;
        }

        private static void WorldObjectSetFactionPostfix(WorldObject __instance, Faction __state)
        {
            if (__instance is Settlement && __instance.Faction != __state)
            {
                BuyerInfoService.NotifyWorldStateChanged();
            }
        }

        private static void WorldObjectDestroyPrefix(WorldObject __instance)
        {
            if (__instance is Settlement)
            {
                BuyerInfoService.NotifyWorldStateChanged();
            }
        }

        private static void FactionListChanged()
        {
            BuyerInfoService.NotifyWorldStateChanged();
        }

        private static void DefsHotReloaded()
        {
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                FactionGearCustomizerCompatibility.OnDefinitionsReloaded();
                CategoryCatalog.RebuildAndInjectStat();
                BuyerInfoService.InitializeDefinitions(BuyerInfoCardMod.Settings.RuleCacheCapacity);
            });
        }
    }

    internal sealed class BuyerInfoCardGameComponent : GameComponent
    {
        public BuyerInfoCardGameComponent(Game game)
        {
        }

        public override void StartedNewGame()
        {
            BuyerInfoService.OnWorldEntered();
        }

        public override void LoadedGame()
        {
            BuyerInfoService.OnWorldEntered();
        }
    }
}
