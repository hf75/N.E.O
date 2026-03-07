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
using System.Reflection;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.NET.HostModel.AppHost;

namespace Neo.Agents
{
    public static class NamespaceFixer
    {
        public static string FixNamespace(string code, string desiredNamespace)
        {
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetCompilationUnitRoot();

            // 1. Alle using-Direktiven sammeln (egal ob innerhalb oder außerhalb von Namespaces)
            var allUsings = root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .ToList();

            // Entferne alle bisherigen using-Direktiven
            var rootWithoutUsings = root.RemoveNodes(allUsings, SyntaxRemoveOptions.KeepNoTrivia);

            if (rootWithoutUsings == null)
                return code; // -> Kein Code da?! Lass das den Compiler handhaben!

            // 2. Bestehenden Namespace suchen
            var oldNamespace = rootWithoutUsings.Members.OfType<NamespaceDeclarationSyntax>().FirstOrDefault();

            CompilationUnitSyntax newRoot;

            if (oldNamespace != null)
            {
                // Namespace existiert, ersetze ihn durch neuen Namen
                var newNamespace = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(desiredNamespace))
                    .WithMembers(oldNamespace.Members)
                    .WithOpenBraceToken(oldNamespace.OpenBraceToken)
                    .WithCloseBraceToken(oldNamespace.CloseBraceToken)
                    .WithNamespaceKeyword(oldNamespace.NamespaceKeyword)
                    .WithSemicolonToken(oldNamespace.SemicolonToken);

                newRoot = rootWithoutUsings
                    .ReplaceNode(oldNamespace, newNamespace)
                    .WithUsings(SyntaxFactory.List(allUsings));
            }
            else
            {
                // Kein Namespace vorhanden -> alle Typen in neuen Namespace stecken
                var newNamespace = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(desiredNamespace))
                    .WithMembers(SyntaxFactory.List(rootWithoutUsings.Members));

                newRoot = SyntaxFactory.CompilationUnit()
                    .WithUsings(SyntaxFactory.List(allUsings))
                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(newNamespace));
            }

            return newRoot.NormalizeWhitespace().ToFullString();
        }
    }

    public class CSharpCompileAgent : AgentBase
    {
        public override string Name => "CSharpCompileAgent";

        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Description = "Kompiliert zur Laufzeit C#-Code."
            };

            // =========== Optionen (Konfiguration, ändert sich selten) ===========
            metadata.Options.Add(new Option<List<string>>(
                name: "CoreDllPath",
                isRequired: true,
                defaultValue: new List<string>(),
                description: @"Pfade zu den .NET Core Reference Assemblies / WPF/.NET Desktop Reference Assemblies etc."));

            metadata.Options.Add(new Option<Dictionary<string, string>>(
                name: "NugetPackageVersions",
                isRequired: false,
                defaultValue: new Dictionary<string, string>(),
                description: @"Dictionary mit Informationen zu den Nuget-Paketen (Name und Version)."));

            // =========== Eingabeparameter (Per-Execution-Daten) ===========
            metadata.InputParameters.Add(new InputParameter<List<string>>(
                name: "Code",
                isRequired: true,
                description: "Die C#-Quellcode Dateien."));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "ForceNamespace",
                isRequired: false,
                description: "Ersetzt wenn gewünscht den Namespace im Code durch den Angegebenen."));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "AssemblyName",
                isRequired: true,
                description: "Der interne Name der Assembly."));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "OutputPath",
                isRequired: true,
                description: "Vollständiger Pfad für die zu erstellende Assembly."));

            metadata.InputParameters.Add(new InputParameter<List<string>>(
                name: "NuGetDlls",
                isRequired: false,
                description: @"Liste zusätzlicher DLL-Pfade, z.B. aus NuGet-Paketen."));

            metadata.InputParameters.Add(new InputParameter<List<string>>(
                name: "AdditionalDlls",
                isRequired: false,
                description: @"Liste zusätzlicher DLL-Dateien."));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "CompileType",
                isRequired: true,
                description: @"Erlaubte Werte sind: DLL, CONSOLE, WINDOWS"));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "AppHostApp",
                isRequired: false,
                description: @"The AppHostApp used for Executable compilation."));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "MainTypeName",
                isRequired: true,
                description: @"The name of the entrypoint including namespace name."));

            // =========== Ausgabeparameter ===========
            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "CompiledPath",
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

            if (code.Count == 0)
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

            var mainTpeName = GetInput<string>("MainTypeName");
            if (string.IsNullOrWhiteSpace(mainTpeName))
            {
                throw new InvalidOperationException("The mainTypeName is required to be not null or empty.");
            }

            var compileType = GetInput<string>("CompileType");
            if (string.IsNullOrWhiteSpace(compileType))
            {
                throw new InvalidOperationException("Der CompileType muss entweder: DLL, CONSOLE oder WINDOWS sein.");
            }

            var outputPath = GetInput<string>("OutputPath");
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new InvalidOperationException("Der Ausgabepfad (Input 'OutputPath') darf nicht leer sein.");
            }

            var refs = GetOption<List<string>>("CoreDllPath");
            if (refs.Count == 0)
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
        /// Hier wird der C#-Code mit Roslyn kompiliert.
        /// </summary>

        public override async Task ExecuteAsync(CancellationToken? cancellationToken = null)
        {
            // Validierung ausführen, um sicherzustellen, dass alle benötigten Werte vorhanden sind.
            ValidateOptionsAndInputs();

            // Optionen und Inputs auslesen
            var coreDllPath = GetOption<List<string>>("CoreDllPath");
            var nugetInfos = GetOption<Dictionary<string, string>>("NugetPackageVersions");

            var code = GetInput<List<string>>("Code");
            var forceNamespace = GetInput<string>("ForceNamespace");
            var outputPath = GetInput<string>("OutputPath");
            var nuGetDlls = GetInput<List<string>>("NuGetDlls");
            var additionalDlls = GetInput<List<string>>("AdditionalDlls");
            var assemblyName = GetInput<string>("AssemblyName");
            var compileType = GetInput<string>("CompileType");
            var appHostApp = GetInput<string>("AppHostApp");
            var mainTpeName = GetInput<string>("MainTypeName");

            if (!string.IsNullOrWhiteSpace(forceNamespace))
            {
                for( int i = 0; i < code.Count; i++ )
                {
                    code[i] = NamespaceFixer.FixNamespace(code[i], forceNamespace);
                }
            }

            List<SyntaxTree> syntaxTrees = new List<SyntaxTree>();
            foreach (var c in code)
            {
                // SyntaxTree aus dem Code erstellen
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(c));
            }

            var references = new List<MetadataReference>();

            // .NET Core Referenz-DLLs
            foreach (string path in coreDllPath)
            {
                foreach (var dll in Directory.GetFiles(path, "*.dll"))
                {
                    if( IsManagedAssembly(dll) )
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

            OutputKind kind = OutputKind.DynamicallyLinkedLibrary;
            if (compileType == "CONSOLE")
                kind = OutputKind.ConsoleApplication;
            else if (compileType == "WINDOWS")
                kind = OutputKind.WindowsApplication;

            string combinedCode = CreateCombinedCode(syntaxTrees);

            string baseName = assemblyName;
            string outputDir = outputPath;
            // Die kompilierte Assembly (enthält IL-Code und EntryPoint-Metadaten)
            string outputDllPath = Path.Combine(outputDir, $"{baseName}.dll");
            // Der native AppHost (wird erstellt und an die DLL gebunden)
            string outputExePath = Path.Combine(outputDir, $"{baseName}.exe");

            if (Directory.Exists(outputPath))
            {
                try
                {
                    Directory.Delete(outputPath, recursive: true);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Fehler beim Löschen des Verzeichnisses: {ex.Message}");
                }
            }

            if (!Directory.Exists(outputPath))
            {
                Directory.CreateDirectory(outputPath);
            }

            OptimizationLevel optimizationLevel;
#if DEBUG
            optimizationLevel = OptimizationLevel.Debug;
#else
            optimizationLevel = OptimizationLevel.Release;
#endif

            CSharpCompilationOptions compilationOptions = new CSharpCompilationOptions(
                kind, // DLL, ConsoleApplication oder WindowsApplication je nach CompileType
                optimizationLevel: optimizationLevel,
                platform: Platform.AnyCpu,
                allowUnsafe: true,
                mainTypeName: mainTpeName // Wichtig: Gibt den Einstiegspunkt an
                );

            // 4. Kompilierung durchführen
            CSharpCompilation compilation = CSharpCompilation.Create(
               baseName, // Assembly-Name
               syntaxTrees: syntaxTrees,
               references: references,
               options: compilationOptions
            );

            // Emit in die .dll-Datei (obwohl OutputKind WindowsApplication ist)
            EmitResult result = compilation.Emit(outputDllPath);

            string dbg = BuildDiagnosticsMessage(result);

            var entryPoint = compilation.GetEntryPoint(CancellationToken.None);
            if ((kind == OutputKind.WindowsApplication || kind == OutputKind.ConsoleApplication)
                && entryPoint == null)
            {
                throw new Exception("Kein Einstiegspunkt gefunden!");
            }

            bool isWindows = appHostApp.EndsWith("windows.exe") && compileType != "CONSOLE";

            CreateRuntimeConfigFile(outputPath, assemblyName, isWindows);

            // Only generate deps.json for GUI apps. For console apps, omitting deps.json
            // lets the .NET host probe the app base directory for all assemblies (including NuGet DLLs).
            if (compileType != "CONSOLE")
                CreateDepsJsonFile(outputPath, assemblyName, isWindows, nuGetDlls);

            if (result.Success)
            {
                try
                {
                    // Vollständiger Pfad zur lokalen AppHost-Vorlage
                    string appHostSourcePath = appHostApp;

                    // Prüfen, ob die Vorlage existiert
                    if (!File.Exists(appHostSourcePath))
                    {
                        throw new FileNotFoundException($"Benötigte AppHost-Vorlage '{appHostSourcePath}' nicht gefunden");
                    }

                    string appBinaryFileName = Path.GetFileName(outputDllPath);

                    // Zusätzliche Sicherheitsprüfung vor dem Aufruf
                    if (string.IsNullOrEmpty(appHostSourcePath)) throw new InvalidOperationException("Interner Fehler: appHostSourcePath ist null oder leer vor dem Aufruf von CreateAppHost.");
                    if (string.IsNullOrEmpty(outputExePath)) throw new InvalidOperationException("Interner Fehler: outputExePath ist null oder leer vor dem Aufruf von CreateAppHost.");
                    if (string.IsNullOrEmpty(appBinaryFileName)) throw new InvalidOperationException($"Interner Fehler: Path.GetFileName für '{outputDllPath}' ist null oder leer.");

                    // Binde den AppHost an die gerade erstellte DLL
                    HostWriter.CreateAppHost(
                            appHostSourceFilePath: appHostSourcePath,       // Die Vorlage 'apphost.exe'
                            appHostDestinationFilePath: outputExePath,      // Die fertige 'CompiledWpfApp.exe'
                            appBinaryFilePath: appBinaryFileName,            // Der Name der DLL ('CompiledWpfApp.dll')
                                                                             // Optional: windowsGraphicalUserInterface: true (oft nicht nötig bei OutputKind.WindowsApplication)
                            windowsGraphicalUserInterface: isWindows
                        );
                }
                catch (Exception ex)
                {
                    throw new Exception(ex.Message);
                }
                // --- Ende AppHost Binden ---
            }
            else
            {
                var failures = result.Diagnostics
                    .Where(d => d.IsWarningAsError || d.Severity == DiagnosticSeverity.Error);

                string errorMessage = string.Join(Environment.NewLine, failures.Select(f => f.ToString()));
                throw new Exception("Kompilierung fehlgeschlagen:\n" + errorMessage);
            }

            if (nuGetDlls != null)
            {
                foreach (var dllPath in nuGetDlls)
                {
                    var fileName = Path.GetFileName(dllPath);
                    var targetPath = Path.Combine(outputDir, fileName);

                    File.Copy(dllPath, targetPath, true);   
                }
            }

            if (additionalDlls != null)
            {
                foreach (var dllPath in additionalDlls)
                {
                    var fileName = Path.GetFileName(dllPath);
                    var targetPath = Path.Combine(outputDir, fileName);

                    File.Copy(dllPath, targetPath, true);
                }
            }

            // Erfolg: In den Output schreiben
            SetOutput("CompiledPath", Path.Combine(outputPath, assemblyName + ".exe"));

            // Asynchrone Operation abschließen
            await Task.CompletedTask;
        }

        string BuildDiagnosticsMessage(EmitResult emitResult)
        {
            var sb = new System.Text.StringBuilder();

            foreach (Diagnostic diag in emitResult.Diagnostics)
            {
                sb.AppendLine($"{diag.Severity} {diag.Id}: {diag.GetMessage()}");
            }

            return sb.ToString();
        }

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

        void CreateRuntimeConfigFile(string outputDirectory, string projectName, bool isWindows)
        {
            string runtimeConfigPath = Path.Combine(outputDirectory, $"{projectName}.runtimeconfig.json");

            var major = Environment.Version.Major;
            var tfm = $"net{major}.0";
            var fwVersion = $"{major}.0.0";
            var fwName = isWindows ? "Microsoft.WindowsDesktop.App" : "Microsoft.NETCore.App";

            var runtimeConfigContent = $@"{{
                  ""runtimeOptions"": {{
                    ""tfm"": ""{tfm}"",
                    ""framework"": {{
                      ""name"": ""{fwName}"",
                      ""version"": ""{fwVersion}""
                    }},
                    ""configProperties"": {{
                      ""System.Runtime.TieredCompilation"": false
                    }}
                  }}
                }}";

            File.WriteAllText(runtimeConfigPath, runtimeConfigContent);
        }

        void CreateDepsJsonFile(string outputDirectory, string projectName, bool isWindows, List<string>? nuGetDlls)
        {
            var major = Environment.Version.Major;
            var versionTag = $"v{major}.0";
            var fwVersion = $"{major}.0.0";
            var fwName = isWindows ? "Microsoft.WindowsDesktop.App.WPF" : "Microsoft.NETCore.App";

            // Build runtime entries for NuGet DLLs so the host can resolve them at runtime
            var runtimeLines = new List<string>();
            runtimeLines.Add($"\"{projectName}.dll\": {{}}");

            if (nuGetDlls != null)
            {
                foreach (var dllPath in nuGetDlls)
                {
                    var fileName = Path.GetFileName(dllPath);
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        runtimeLines.Add($"\"{fileName}\": {{}}");
                    }
                }
            }

            var runtimeBlock = string.Join(",\n                          ", runtimeLines);

            string depsJsonPath = Path.Combine(outputDirectory, $"{projectName}.deps.json");
            string depsJsonContent = @"{
                  ""runtimeTarget"": {
                    ""name"": "".NETCoreApp,Version=" + versionTag + @""",
                    ""signature"": """"
                  },
                  ""compilationOptions"": {},
                  ""targets"": {
                    "".NETCoreApp,Version=" + versionTag + @""": {
                      """ + projectName + @"/1.0.0"": {
                        ""dependencies"": {
                          """ + fwName + @""": """ + fwVersion + @"""
                        },
                        ""runtime"": {
                          " + runtimeBlock + @"
                        }
                      }
                    }
                  }
                }";

            File.WriteAllText(depsJsonPath, depsJsonContent);
        }

        public string CreateCombinedCode(List<SyntaxTree> syntaxTrees)
        {
            var combinedRoot = SyntaxFactory.CompilationUnit();
            foreach (SyntaxTree syntaxTree in syntaxTrees)
            {
                var root = syntaxTree.GetRoot();
                combinedRoot = combinedRoot.AddUsings(((CompilationUnitSyntax)root).Usings.ToArray());
                combinedRoot = combinedRoot.AddMembers(((CompilationUnitSyntax)root).Members.ToArray());
            }

            // Kombinierten Code formatieren und ausgeben
            var combinedCode = combinedRoot.NormalizeWhitespace().ToFullString();
            return combinedCode;
        }
    }
}

