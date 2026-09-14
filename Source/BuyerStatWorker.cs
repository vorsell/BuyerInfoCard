using System.Collections.Generic;
using RimWorld;
using Verse;

namespace BuyerInfoCard
{
    public sealed class BuyerStatWorker : StatWorker
    {
        public override bool ShouldShowFor(StatRequest request)
        {
            ThingDef thingDef = BuyerInfoService.ResolveThingDef(request);
            bool eligible = InfoCardUiPatches.FreezeSessionVisibility(
                request,
                CategoryCatalog.ShouldShow(thingDef, request.Thing, BuyerInfoCardMod.Settings));
            return !BuyerInfoCardMod.Settings.MergeIntoMarketValue && eligible;
        }

        public override float GetValueUnfinalized(StatRequest request, bool applyPostProcess = true)
        {
            return 0f;
        }

        public override float GetBaseValueFor(StatRequest request)
        {
            return 0f;
        }

        public override string GetStatDrawEntryLabel(
            StatDef statDef,
            float value,
            ToStringNumberSense numberSense,
            StatRequest optionalReq,
            bool finalized = true)
        {
            return InfoCardUiPatches.GetDisplayResult(optionalReq).Summary;
        }

        public override string ValueToString(float value, bool finalized, ToStringNumberSense numberSense)
        {
            return string.Empty;
        }

        public override string GetExplanationUnfinalized(StatRequest request, ToStringNumberSense numberSense)
        {
            return InfoCardUiPatches.GetDetailResult(request).Text;
        }

        public override string GetExplanationFinalizePart(StatRequest request, ToStringNumberSense numberSense, float finalValue)
        {
            return string.Empty;
        }

        public override void FinalizeValue(StatRequest request, ref float value, bool applyPostProcess)
        {
            value = 0f;
        }

        public override IEnumerable<Dialog_InfoCard.Hyperlink> GetInfoCardHyperlinks(StatRequest request)
        {
            return InfoCardUiPatches.GetDetailResult(request).Hyperlinks;
        }
    }
}
