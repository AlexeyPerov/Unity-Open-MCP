using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.ShaderAnalysis
{
    public class ShaderError
    {
        public ShaderError(string message, string platform)
        {
            Message = message;
            Platform = platform;
        }

        public string Message { get; }
        public string Platform { get; }
    }

    public class ShaderData
    {
        public ShaderData(string path)
        {
            Path = path;
        }

        public string Path { get; }

        /// <summary>True when the shader asset failed to load at all.</summary>
        public bool FailedToLoad { get; set; }

        /// <summary>True when the shader loads but Unity reports it as
        /// unsupported (compile failure).</summary>
        public bool Unsupported { get; set; }

        public List<ShaderError> CompileErrors { get; } = new List<ShaderError>();
    }
}
