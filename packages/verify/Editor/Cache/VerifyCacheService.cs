using System;
using UnityAgentVerify.Batch;

namespace UnityAgentVerify.Cache
{
    public static class VerifyCacheService
    {
        public const string SourceScanPaths = "scan_paths";
        public const string SourceScanAll = "scan_all";
        public const string SourceValidateEdit = "validate_edit";
        public const string SourceGate = "gate";

        public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

        static HealthSummarySnapshot _lastSnapshot;
        static DateTime _lastWriteUtc;
        static TimeSpan _ttl = DefaultTtl;
        static bool _hasData;

        public static TimeSpan Ttl
        {
            get => _ttl;
            set => _ttl = value > TimeSpan.Zero ? value : DefaultTtl;
        }

        public static bool HasData => _hasData;

        public static DateTime? LastWriteUtc => _hasData ? (DateTime?)_lastWriteUtc : null;

        public static void Record(VerifyResult result, string source)
        {
            if (result == null) return;

            int errors = 0, warnings = 0;
            foreach (var issue in result.Issues)
            {
                if (issue.Severity == VerifySeverity.Error) errors++;
                else if (issue.Severity == VerifySeverity.Warning) warnings++;
            }

            _lastSnapshot = new HealthSummarySnapshot
            {
                Status = HealthSummaryStatus.Ok,
                AsOf = DateTime.UtcNow.ToString("o"),
                Summary = new SeveritySummary(errors, warnings, 0),
                Source = string.IsNullOrEmpty(source) ? SourceValidateEdit : source
            };
            _lastWriteUtc = DateTime.UtcNow;
            _hasData = true;
        }

        public static void Clear()
        {
            _lastSnapshot = null;
            _lastWriteUtc = default;
            _hasData = false;
        }

        public static HealthSummarySnapshot GetSnapshot()
        {
            if (!_hasData || _lastSnapshot == null)
                return EmptySnapshot();

            var age = DateTime.UtcNow - _lastWriteUtc;
            if (age > _ttl)
                return EmptySnapshot();

            return _lastSnapshot;
        }

        public static bool IsStale()
        {
            if (!_hasData) return false;
            var age = DateTime.UtcNow - _lastWriteUtc;
            return age > _ttl;
        }

        static HealthSummarySnapshot EmptySnapshot()
        {
            return new HealthSummarySnapshot
            {
                Status = HealthSummaryStatus.NoData,
                AsOf = null,
                Summary = null,
                Source = null
            };
        }
    }
}
