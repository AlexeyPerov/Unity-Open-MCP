using UnityOpenMcpVerify.Batch;

namespace UnityOpenMcpVerify.Cache
{
    public enum HealthSummaryStatus
    {
        NoData,
        Ok,
        Stale
    }

    public class HealthSummarySnapshot
    {
        public HealthSummaryStatus Status;
        public string AsOf;
        public SeveritySummary Summary;
        public string Source;

        public bool IsEmpty => Status == HealthSummaryStatus.NoData
                               && Summary == null
                               && string.IsNullOrEmpty(AsOf)
                               && string.IsNullOrEmpty(Source);
    }
}
