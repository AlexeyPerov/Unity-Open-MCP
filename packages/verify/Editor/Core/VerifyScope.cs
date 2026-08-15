namespace UnityOpenMcpVerify
{
    public class VerifyScope
    {
        public string[] Paths;
        public bool IncludeDependents;
        // Platform profile the scan should assume ("desktop" | "mobile" |
        // "console"). Consumed by profile-sensitive rules (today:
        // shader_analysis, whose expensive_feature_platform /
        // platform_keyword_mismatch detections are mobile-gated). Defaults to
        // "desktop" — the gate and scan_paths default — while the batch entry
        // threads the caller's --platform-profile through so scan_all with a
        // mobile profile actually runs the mobile detections instead of only
        // echoing the value in the result metadata.
        public string PlatformProfile;

        public VerifyScope(string[] paths, bool includeDependents = false, string platformProfile = null)
        {
            Paths = paths;
            IncludeDependents = includeDependents;
            PlatformProfile = string.IsNullOrEmpty(platformProfile) ? "desktop" : platformProfile;
        }
    }
}
