using System;
using System.Threading;

namespace UnityOpenMcpBridge
{
    /// <summary>Injected into async commands. Keep Unity work on the Editor context and yield
    /// between bounded steps. Cancellation is acknowledged by throwing OperationCanceledException.</summary>
    public sealed class ProjectCommandContext
    {
        public string JobId { get; }
        public CancellationToken CancellationToken { get; }
        private readonly Action<string> report;
        internal ProjectCommandContext(string jobId, CancellationToken token, Action<string> report)
        { JobId = jobId; CancellationToken = token; this.report = report; }
        public void ReportPhase(string phase)
        {
            if (string.IsNullOrWhiteSpace(phase) || phase.Length > 1024)
                throw new ArgumentException("Phase must contain 1..1024 characters.");
            report(phase);
        }
    }
}
