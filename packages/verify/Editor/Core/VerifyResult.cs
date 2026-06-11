using System.Collections.Generic;

namespace UnityAgentVerify
{
    public class VerifyResult
    {
        public List<VerifyIssue> Issues { get; }
        public string[] CategoriesRun { get; }
        public long DurationMs { get; }

        public VerifyResult(List<VerifyIssue> issues, string[] categoriesRun, long durationMs)
        {
            Issues = issues;
            CategoriesRun = categoriesRun;
            DurationMs = durationMs;
        }
    }
}
