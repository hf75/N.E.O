using Neo.Agents;
using Neo.AssemblyForge.Utils;

namespace Neo.App
{
    public interface ICompilationService
    {
        Task<string?> CompileToDllAsync(List<string> codes, string dllOutputPath, List<string> nugetDllPaths, List<string> additionalDlls);
        Task<string?> CompileToExeAsync(List<string> codes, string outputDirectory, List<string> nugetDllPaths, string assemblyName, string mainTypeName);
    }

    public class CompilationService : ICompilationService
    {
        private readonly string? _coreRefPath;
        private readonly string? _desktopRefPath;

        public CompilationService(string? coreRefPath, string? desktopRefPath)
        {
            _coreRefPath = coreRefPath;
            _desktopRefPath = desktopRefPath;
        }

        public async Task<string?> CompileToDllAsync(List<string> codes, string dllOutputPath, List<string> nugetDllPaths, List<string> additionalDlls)
        {
            if (string.IsNullOrEmpty(_coreRefPath))
            {
                System.Diagnostics.Debug.WriteLine("[CompilationService] _coreRefPath is null/empty — cannot compile.");
                return null;
            }

            var refPaths = new List<string> { _coreRefPath };
            if (!string.IsNullOrEmpty(_desktopRefPath))
                refPaths.Add(_desktopRefPath);

            var agent = new CSharpDllCompileAgent();

            agent.SetOption("CoreDllPath", refPaths);

            agent.SetInput("Code", codes);
            agent.SetInput("DllOutputPath", dllOutputPath);
            agent.SetInput("AssemblyName", "DynamicUserControl");
            agent.SetInput("NuGetDlls", nugetDllPaths ?? new List<string>());
            agent.SetInput("AdditionalDlls", additionalDlls ?? new List<string>());

            try
            {
                await agent.ExecuteAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CompilationService] Compilation threw: {ex.Message}");
                return null;
            }

            return agent.GetOutput<string>("CompiledDllPath");
        }

        public async Task<string?> CompileToExeAsync(List<string> codes, string outputDirectory, List<string> nugetDllPaths, string assemblyName, string mainTypeName)
        {
            if (string.IsNullOrEmpty(_coreRefPath))
            {
                return null;
            }

            var appHostPath = AppHostTemplates.EnsureExtracted(AssemblyForgeAppHostTemplate.WindowsExe);

            var agent = new CSharpCompileAgent();

            agent.SetOption("CoreDllPath", new List<string> { _coreRefPath });
            agent.SetOption("NugetPackageVersions", new Dictionary<string, string>());

            agent.SetInput("Code", codes);
            agent.SetInput("OutputPath", outputDirectory);
            agent.SetInput("AssemblyName", assemblyName);
            agent.SetInput("MainTypeName", mainTypeName);
            agent.SetInput("CompileType", "CONSOLE");
            agent.SetInput("AppHostApp", appHostPath);
            agent.SetInput("NuGetDlls", nugetDllPaths ?? new List<string>());
            agent.SetInput("AdditionalDlls", new List<string>());

            await agent.ExecuteAsync();

            return agent.GetOutput<string>("CompiledPath");
        }
    }
}
