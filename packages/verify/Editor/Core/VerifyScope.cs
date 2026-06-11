namespace UnityAgentVerify
{
    public class VerifyScope
    {
        public string[] Paths;
        public bool IncludeDependents;

        public VerifyScope(string[] paths, bool includeDependents = false)
        {
            Paths = paths;
            IncludeDependents = includeDependents;
        }
    }
}
