using System.Collections.Generic;

namespace UnityOpenMcpVerify
{
    public interface IVerifyRule
    {
        string Id { get; }
        void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink);
    }
}
