using System.IO;

namespace OmniSharp.Script
{
    public class ScriptOptions
    {
        public bool EnableScriptNuGetReferences { get; set; }

        public string RspFilePath { get; set; }

        public string GetNormalizedRspFilePath(IOmniSharpEnvironment env)
        {
            if (string.IsNullOrWhiteSpace(RspFilePath)) return null;
            return Path.IsPathRooted(RspFilePath)
                ? RspFilePath
                : Path.Combine(env.TargetDirectory, RspFilePath);
        }
    }
}
