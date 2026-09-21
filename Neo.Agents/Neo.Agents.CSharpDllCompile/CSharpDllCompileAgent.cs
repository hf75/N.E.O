using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Neo.Agents.Core;

namespace Neo.Agents
{
    public class CSharpDllCompileAgent : AgentBase
    {
        public override string Name => "CSharpDllCompileAgent";

        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Description = "Kompiliert zur Laufzeit C#-Code in eine Dll."
            };

            // =========== Optionen (Konfiguration, ändert sich selten) ===========
            metadata.Options.Add(new Option<List<string>>(
                name: "CoreDllPath",
                isRequired: true,
                defaultValue: new List<string>(),
                description: @"Pfade zu den .NET Core Reference Assemblies / WPF/.NET Desktop Reference Assemblies etc."));

            // =========== Eingabeparameter (Per-Execution-Daten) ===========
            metadata.InputParameters.Add(new InputParameter<List<string>>(
                name: "Code",
                isRequired: true,
                description: "Die C#-Quellcode Dateien."));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "AssemblyName",
                isRequired: true,
                description: "Der interne Name der Assembly."));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "DllOutputPath",
                isRequired: true,
                description: "Vollständiger Pfad für die zu erstellende DLL."));

            metadata.InputParameters.Add(new InputParameter<List<string>>(
                name: "NuGetDlls",
                isRequired: false,
                description: @"Liste zusätzlicher DLL-Pfade, z.B. aus NuGet-Paketen."));

            metadata.InputParameters.Add(new InputParameter<List<string>>(
                name: "AdditionalDlls",
                isRequired: false,
                description: @"Liste zusätzlicher DLL-Dateien."));

            metadata.Options.Add(new Option<bool>(
                name: "HotReload",
                isRequired: false,
                defaultValue: true,
                description: "Build the DLL so that Neo.HotReload can patch it later while it is running: without " +
                             "optimizations, with the portable PDB embedded in the DLL and with a '<dll>.hotreload.json' " +
                             "that records the sources and references (see HotReloadBaselineFile)."));

            // =========== Ausgabeparameter ===========
            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "CompiledDllPath",
                isAlwaysProvided: true,
                description: "Pfad zur erfolgreich erstellten DLL."));

            metadata.OutputParameters.Add(new OutputParameter<List<string>>(
                name: "ReferencePaths",
                isAlwaysProvided: false,
                description: "Hot reload mode: the assemblies the code was compiled against."));

            return metadata;
        }

        /// <summary>
        /// Hier wird geprüft, ob alle notwendigen Optionen/Eingaben belegt sind.
        /// Wirf eine Exception oder reagiere entsprechend, wenn etwas fehlt.
        /// </summary>
        public override void ValidateOptionsAndInputs()
        {
            var code = GetInput<List<string>>("Code");

            if(code.Count == 0)
            {
                throw new InvalidOperationException("Der Quellcode (Input 'Code') darf nicht leer sein.");
            }

            foreach (var item in code)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    throw new InvalidOperationException("Der Quellcode (Input 'Code') darf nicht leer sein.");
                }
            }

            var assemblyName = GetInput<string>("AssemblyName");
            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                throw new InvalidOperationException("Der AssemblyName darf nicht null oder leer sein.");
            }

            var dllPath = GetInput<string>("DllOutputPath");
            if (string.IsNullOrWhiteSpace(dllPath))
            {
                throw new InvalidOperationException("Der Ausgabepfad (Input 'DllOutputPath') darf nicht leer sein.");
            }

            var refs = GetOption<List<string>>("CoreDllPath");
            if( refs.Count == 0)
                throw new InvalidOperationException("Der Dll Reference-Pfad (Option 'CoreDllPath') ist nicht gesetzt.");
            foreach (string path in refs) 
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                {
                    throw new InvalidOperationException($"Der Dll Reference-Pfad {path} (Option 'CoreDllPath') ist ungültig oder existiert nicht.");
                }
            }
        }

        /// <summary>
        /// Führt die eigentliche Logik aus (ähnlich wie in deinem ursprünglichen Compiler-Code).
        /// Hier wird der C#-Code mit Roslyn kompiliert und eine DLL erzeugt.
        /// </summary>
        public override async Task ExecuteAsync(CancellationToken? cancellationToken = null)
        {
            // Validierung ausführen, um sicherzustellen, dass alle benötigten Werte vorhanden sind.
            ValidateOptionsAndInputs();

            // Optionen und Inputs auslesen
            var coreDllPath = GetOption<List<string>>("CoreDllPath");

            var code = GetInput<List<string>>("Code");
            var dllOutputPath = GetInput<string>("DllOutputPath");
            var nuGetDlls = GetInput<List<string>>("NuGetDlls");
            var additionalDlls = GetInput<List<string>>("AdditionalDlls");
            var assemblyName = GetInput<string>("AssemblyName");

            // Hot reload mode: edit-and-continue needs an unoptimized build and a PDB whose document
            // names and checksums match what Neo.HotReload puts into its workspace.
            bool hotReload = true;
            try { hotReload = GetOption<bool>("HotReload"); }
            catch (KeyNotFoundException) { /* option not set: default */ }
            var hotReloadPaths = Enumerable.Range(0, code.Count).Select(HotReloadBaselineFile.SourcePathFor).ToList();

            List<SyntaxTree> syntaxTrees = new List<SyntaxTree>();
            for (int i = 0; i < code.Count; i++)
            {
                // SyntaxTree aus dem Code erstellen
                syntaxTrees.Add(hotReload
                    ? CSharpSyntaxTree.ParseText(
                        Microsoft.CodeAnalysis.Text.SourceText.From(code[i], new System.Text.UTF8Encoding(false),
                            Microsoft.CodeAnalysis.Text.SourceHashAlgorithm.Sha256),
                        path: hotReloadPaths[i])
                    : CSharpSyntaxTree.ParseText(code[i]));
            }

            // Referenzen sammeln — jede Datei einmal, auch wenn der Host sie doppelt nennt.
            var references = new List<MetadataReference>();
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddReference(string dll)
            {
                if (referenced.Add(Path.GetFullPath(dll)))
                    references.Add(MetadataReference.CreateFromFile(dll));
            }

            // .NET Core Referenz-DLLs
            foreach(string path in coreDllPath)
            {
                foreach (var dll in Directory.GetFiles(path, "*.dll"))
                {
                    if (IsManagedAssembly(dll))
                        AddReference(dll);

                }
            }

            // Evtl. zusätzliche DLLs (z.B. aus NuGet-Paketen)
            if (nuGetDlls != null)
            {
                foreach (var dllPath in nuGetDlls)
                {
                    if (!File.Exists(dllPath))
                    {
                        throw new FileNotFoundException($"NuGet DLL '{dllPath}' wurde nicht gefunden.");
                    }
                    if (!IsManagedAssembly(dllPath))
                        continue;
                    AddReference(dllPath);
                }
            }

            // Evtl. zusätzliche DLLs
            if (additionalDlls != null)
            {
                foreach (var dllPath in additionalDlls)
                {
                    if (!File.Exists(dllPath))
                    {
                        throw new FileNotFoundException($"Additional DLL '{dllPath}' wurde nicht gefunden.");
                    }
                    if (!IsManagedAssembly(dllPath))
                        continue;
                    AddReference(dllPath);
                }
            }

            OptimizationLevel optimizationLevel;
#if DEBUG
            optimizationLevel = OptimizationLevel.Debug;
#else
            optimizationLevel = OptimizationLevel.Release;
#endif
            if (hotReload)
                optimizationLevel = OptimizationLevel.Debug;

            // C#-Kompilation erstellen
            var compilation = CSharpCompilation.Create(
                assemblyName: assemblyName,
                syntaxTrees: syntaxTrees,
                references: references,
                options: new CSharpCompilationOptions(
                    outputKind: OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: optimizationLevel,
                    platform: Platform.AnyCpu,
                    allowUnsafe: true)
            );

            // Hot reload mode embeds the PDB. Roslyn's hot reload engine reads the debug information of
            // the running build from the DLL itself then (embedded is the first place it looks); with a
            // separate file it opens the PDB path recorded in the DLL — this very path, which the next
            // compile overwrites — and keeps it open for the whole session, so that compile fails.
            if (hotReload)
                TryDeleteStalePdb(Path.ChangeExtension(dllOutputPath, ".pdb"));

            // Kompilieren und DLL erzeugen
            using (var fs = new FileStream(dllOutputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var result = hotReload
                    ? compilation.Emit(fs, pdbStream: null, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(
                        debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.Embedded))
                    : compilation.Emit(fs);

                if (!result.Success)
                {
                    var failures = result.Diagnostics
                        .Where(d => d.IsWarningAsError || d.Severity == DiagnosticSeverity.Error);

                    string errorMessage = string.Join(Environment.NewLine, failures.Select(f => f.ToString()));
                    throw new Exception("Kompilierung fehlgeschlagen:\n" + errorMessage);
                }
            }

            // Erfolg: In den Output schreiben
            SetOutput("CompiledDllPath", dllOutputPath);
            if (hotReload)
            {
                var referencePaths = references.OfType<PortableExecutableReference>()
                    .Select(r => r.FilePath).Where(f => !string.IsNullOrEmpty(f)).Select(f => f!).ToList();
                SetOutput("ReferencePaths", referencePaths);

                // Hosts get the DLL path back through several layers that know nothing about hot
                // reload; whoever wants to start a session finds everything next to the DLL.
                new HotReloadBaselineFile
                {
                    AssemblyName = assemblyName,
                    SourcePaths = hotReloadPaths,
                    SourceTexts = code.ToList(),
                    ReferencePaths = referencePaths,
                }.Write(dllOutputPath);
            }

            // Asynchrone Operation abschließen
            await Task.CompletedTask;
        }

        // A separate PDB from an earlier build would contradict the embedded one — and debuggers or
        // older N.E.O. builds might pick it up. Best effort: a file still held open stays.
        private static void TryDeleteStalePdb(string pdbPath)
        {
            try { if (File.Exists(pdbPath)) File.Delete(pdbPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// Überprüft, ob eine DLL eine verwaltete Assembly ist,
        /// indem festgestellt wird, ob sie verwaltete Metadaten enthält.
        /// </summary>
        /// <param name="filePath">Pfad zur DLL</param>
        /// <returns>true, wenn die DLL verwaltete Metadaten enthält; sonst false.</returns>
        private bool IsManagedAssembly(string filePath)
        {
            try
            {
                using (var stream = File.OpenRead(filePath))
                using (var peReader = new PEReader(stream))
                {
                    return peReader.HasMetadata && peReader.GetMetadataReader().IsAssembly;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IsManagedAssembly] Failed for '{Path.GetFileName(filePath)}': {ex.Message}");
                return false;
            }
        }
    }
}
