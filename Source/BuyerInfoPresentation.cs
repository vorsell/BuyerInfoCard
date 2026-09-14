using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace BuyerInfoCard
{
    internal sealed class BuyerDisplayResult
    {
        internal ThingDef ThingDef;
        internal Thing Thing;
        internal CurrentSaleRestriction Restrictions;
        internal List<BuyerParty> Parties = new List<BuyerParty>();
        internal string Summary;
        internal bool DrawSummaryIcons;
        internal string SummaryPrefix;
    }

    internal enum BuyerDetailLinkKind
    {
        Faction,
        Settlement
    }

    internal sealed class BuyerDetailLink
    {
        internal BuyerDetailLinkKind Kind;
        internal Def FallbackDef;
        internal int SelectedStatIndex;
        internal string Caption;
        internal Faction Faction;
        internal Settlement Settlement;
    }

    internal sealed class BuyerDetailPresentation
    {
        internal string Text;
        internal int WorldRevision;
        internal readonly List<BuyerDetailLink> Links = new List<BuyerDetailLink>();
    }

    internal static class BuyerInfoPresentation
    {
        internal static BuyerDisplayResult BuildDisplay(
            BuyerQueryResult query,
            BuyerInfoCardSettings settings)
        {
            bool hasFactionBuyer = false;
            for (int index = 0; index < query.Parties.Count; index++)
            {
                if (!query.Parties[index].IsFactionlessTradeShip)
                {
                    hasFactionBuyer = true;
                    break;
                }
            }
            bool drawSummaryIcons = settings.SummaryUsesIcons && hasFactionBuyer;
            string summaryPrefix = query.Restrictions == CurrentSaleRestriction.None
                ? string.Empty
                : "BIC_CurrentlyUnsellable".Translate().ToString();
            string summary;
            if (query.Parties.Count == 0)
            {
                summary = "BIC_NoKnownBuyer".Translate();
            }
            else if (drawSummaryIcons)
            {
                summary = " ";
            }
            else
            {
                summary = query.Parties[0].Label;
                if (query.Parties.Count > 1)
                {
                    summary += " +" + (query.Parties.Count - 1);
                }
            }

            if (query.Restrictions != CurrentSaleRestriction.None && !drawSummaryIcons)
            {
                summary = "BIC_CurrentlyUnsellable".Translate() + " · " + summary;
            }

            return new BuyerDisplayResult
            {
                ThingDef = query.ThingDef,
                Thing = query.Thing,
                Restrictions = query.Restrictions,
                Parties = query.Parties,
                Summary = summary,
                DrawSummaryIcons = drawSummaryIcons,
                SummaryPrefix = summaryPrefix
            };
        }

        internal static BuyerDetailPresentation BuildDetail(
            BuyerDisplayResult display,
            BuyerInfoCardSettings settings)
        {
            List<string> lines = new List<string>();
            BuyerDetailPresentation detail = new BuyerDetailPresentation();
            if (display.Restrictions != CurrentSaleRestriction.None)
            {
                lines.Add("BIC_CurrentReasons".Translate(RestrictionText(display.Restrictions)));
                lines.Add(string.Empty);
            }

            if (display.Parties.Count == 0)
            {
                lines.Add("BIC_NoKnownBuyer".Translate());
            }
            else
            {
                PlanetTile origin = BuyerInfoService.CurrentOriginTile();
                for (int index = 0; index < display.Parties.Count; index++)
                {
                    BuyerParty party = display.Parties[index];
                    Settlement settlement = null;
                    int tiles = 0;
                    if (settings.ShowNearestSettlements && !party.IsFactionlessTradeShip)
                    {
                        float distance;
                        settlement = BuyerInfoService.FindNearestSettlement(party.Faction, origin, out distance);
                        if (settlement != null)
                        {
                            tiles = Math.Max(0, (int)Math.Round(distance));
                        }
                    }

                    lines.Add(settlement == null
                        ? party.Label
                        : "BIC_FactionWithNearestSettlement".Translate(
                            party.Label,
                            settlement.LabelCap,
                            tiles).ToString());
                    if (settings.ShowTraderKinds)
                    {
                        for (int kindIndex = 0; kindIndex < party.TraderKinds.Count; kindIndex++)
                        {
                            string traderLabel;
                            if (DisplayLabelUtility.TryGetDefLabel(party.TraderKinds[kindIndex], out traderLabel))
                            {
                                lines.Add("• " + traderLabel.Trim());
                            }
                        }
                    }

                    if (!party.IsFactionlessTradeShip)
                    {
                        detail.Links.Add(new BuyerDetailLink
                        {
                            Kind = BuyerDetailLinkKind.Faction,
                            FallbackDef = party.FactionDef,
                            SelectedStatIndex = -1000 - index * 2,
                            Caption = "BIC_FactionHyperlink".Translate(party.Label),
                            Faction = party.Faction
                        });
                    }
                    if (settlement != null)
                    {
                        detail.Links.Add(new BuyerDetailLink
                        {
                            Kind = BuyerDetailLinkKind.Settlement,
                            FallbackDef = settlement.def,
                            SelectedStatIndex = -1001 - index * 2,
                            Caption = "BIC_SettlementHyperlink".Translate(settlement.LabelCap),
                            Settlement = settlement
                        });
                    }
                }
            }

            detail.Text = string.Join("\n", lines.ToArray());
            detail.WorldRevision = BuyerInfoService.CurrentWorldRevision;
            return detail;
        }

        private static string RestrictionText(CurrentSaleRestriction restrictions)
        {
            List<string> labels = new List<string>();
            if ((restrictions & CurrentSaleRestriction.DeadmansApparel) != 0)
            {
                labels.Add("BIC_ReasonDeadmans".Translate());
            }
            if ((restrictions & CurrentSaleRestriction.Biocoded) != 0)
            {
                labels.Add("BIC_ReasonBiocoded".Translate());
            }
            if ((restrictions & CurrentSaleRestriction.Rotten) != 0)
            {
                labels.Add("BIC_ReasonRotten".Translate());
            }
            CurrentSaleRestriction explicitReasons = CurrentSaleRestriction.DeadmansApparel
                | CurrentSaleRestriction.Biocoded
                | CurrentSaleRestriction.Rotten;
            if ((restrictions & CurrentSaleRestriction.OtherCurrentRestriction) != 0
                && (restrictions & explicitReasons) == 0)
            {
                labels.Add("BIC_ReasonOther".Translate());
            }
            return string.Join(", ", labels.ToArray());
        }
    }
}
