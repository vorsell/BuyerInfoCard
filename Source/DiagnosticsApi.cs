using System;

namespace BuyerInfoCard
{
    public enum BuyerInfoDiagnosticKind
    {
        BuyerQuery,
        NearestSettlement,
        RuleAudit,
        LiveTraderRefresh
    }

    public sealed class BuyerInfoDiagnosticEvent
    {
        public BuyerInfoDiagnosticKind Kind { get; internal set; }
        public string Subject { get; internal set; }
        public long ElapsedStopwatchTicks { get; internal set; }
        public bool CacheHit { get; internal set; }
        public int TraderKindsChecked { get; internal set; }
        public int StockGeneratorsDeclared { get; internal set; }
        public int Results { get; internal set; }
        public int RuleCacheSize { get; internal set; }
        public int ProviderObjectsScanned { get; internal set; }
    }

    public static class BuyerInfoDiagnostics
    {
        public static event Action<BuyerInfoDiagnosticEvent> EventRecorded;

        internal static bool HasSubscribers
        {
            get { return EventRecorded != null; }
        }

        internal static void Record(BuyerInfoDiagnosticEvent diagnosticEvent)
        {
            Action<BuyerInfoDiagnosticEvent> handler = EventRecorded;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(diagnosticEvent);
            }
            catch
            {
                // A disposable observer must never break the production query.
            }
        }
    }
}
