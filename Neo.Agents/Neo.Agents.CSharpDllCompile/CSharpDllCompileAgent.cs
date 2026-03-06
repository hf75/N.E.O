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

            // =========== Ausgabeparameter ===========
            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "CompiledDllPath",
                isAlwaysProvided: true,
                description: "Pfad zur erfolgreich erstellten DLL."));

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

            List<SyntaxTree> syntaxTrees = new List<SyntaxTree>();
            foreach (var c in code)
            {
                // SyntaxTree aus dem Code erstellen
                 syntaxTrees.Add(CSharpSyntaxTree.ParseText(c));
            }

            // Referenzen sammeln
            var references = new List<MetadataReference>();

            // .NET Core Referenz-DLLs
            foreach(string path in coreDllPath)
            {
                foreach (var dll in Directory.GetFiles(path, "*.dll"))
                {
                    if (IsManagedAssembly(dll))
                        references.Add(MetadataReference.CreateFromFile(dll));

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
                    references.Add(MetadataReference.CreateFromFile(dllPath));
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
                    references.Add(MetadataReference.CreateFromFile(dllPath));
                }
            }

            OptimizationLevel optimizationLevel;
#if DEBUG
            optimizationLevel = OptimizationLevel.Debug;
#else
            optimizationLevel = OptimizationLevel.Release;
#endif

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

            // Kompilieren und DLL erzeugen
            using (var fs = new FileStream(dllOutputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var result = compilation.Emit(fs);

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

            // Asynchrone Operation abschließen
            await Task.CompletedTask;
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
