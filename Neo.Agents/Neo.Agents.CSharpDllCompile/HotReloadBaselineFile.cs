using System.Text.Json;

namespace Neo.Agents
{
    /// <summary>
    /// Written next to every hot-reloadable DLL as <c>&lt;dll&gt;.hotreload.json</c>: what Neo.HotReload
    /// needs later to compute edit-and-continue deltas against exactly this build — the sources
    /// as they were compiled (with the document paths that went into the embedded PDB) and the
    /// references. A file, because the DLL path travels back to the hosts through layers that know
    /// nothing about hot reload, and because the next compile overwrites the DLL the running app was
    /// loaded from. The debug information itself is embedded in the DLL.
    /// </summary>
    public sealed class HotReloadBaselineFile
    {
        public const string Extension = ".hotreload.json";

        public string AssemblyName { get; set; } = "";
        public List<string> SourcePaths { get; set; } = new();
        public List<string> SourceTexts { get; set; } = new();
        public List<string> ReferencePaths { get; set; } = new();

        /// <summary>
        /// Document name of the n-th source file. Nothing is ever stored there; the path only has
        /// to be the same in the PDB and in the hot reload workspace, and stable per file index.
        /// </summary>
        public static string SourcePathFor(int index)
            => Path.Combine(Path.GetTempPath(), "NeoHotReload", $"Source{index}.cs");

        public static string PathFor(string dllPath) => dllPath + Extension;

        public void Write(string dllPath)
            => File.WriteAllText(PathFor(dllPath), JsonSerializer.Serialize(this));

        public static HotReloadBaselineFile? TryRead(string dllPath)
        {
            try
            {
                var path = PathFor(dllPath);
                return File.Exists(path) ? JsonSerializer.Deserialize<HotReloadBaselineFile>(File.ReadAllText(path)) : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
