using Neo.Agents;
using Neo.Agents.Core;
using Neo.IPC;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json;
using Neo.AssemblyForge;
using Neo.AssemblyForge.Completion;
using Neo.App.Services;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Neo.App
{
    public partial class AppController
    {
        public async Task PreloadMandatoryNugetPacks()
        {
            var packs = ConvertPackageListToDictionary(DefaultNugets);
            await LoadAndApplyNugetPackages(packs);

            // Preload ist Teil der "Baseline" und soll die aktuelle Root-Snapshot konsistent halten.
            if (!_undoRedo.CanUndo && !_undoRedo.CanRedo)
                _undoRedo.ResetToCurrentState(title: "Session start", description: "Preloaded mandatory NuGet packages");
        }

        public Dictionary<string, string> ConvertPackageListToDictionary(List<string> packageList)
        {
            var result = new Dictionary<string, string>();

            foreach (var package in packageList)
            {
                // Überspringe leere oder nur aus Leerzeichen bestehende Einträge.
                if (string.IsNullOrWhiteSpace(package))
                    continue;

                // Teile den String anhand des Trennzeichens '|'
                var parts = package.Split('|');

                // Wenn sowohl Name als auch Version vorhanden sind:
                if (parts.Length >= 2)
                {
                    var name = parts[0].Trim();
                    var version = parts[1].Trim();
                    result[name] = version;
                }
                // Falls nur der Name vorhanden ist, setze Version auf "default"
                else if (parts.Length == 1)
                {
                    var name = parts[0].Trim();
                    result[name] = "default";
                }
            }

            return result;
        }

        public async Task LoadAndApplyNugetPackages(Dictionary<string, string> packages)
        {
            if (packages == null || packages.Count == 0) return;

            var result = await NugetPackageService.LoadPackagesAsync(packages, AppState.NuGetDlls);

            AppState.NuGetDlls = result.DllPaths;
            foreach (var package in result.PackageVersions)
            {
                AppState.PackageVersions.TryAdd(package.Key, package.Value);
            }
        }

        private async Task<string?> CompileToDllAsync(List<string> codes)
        {
            // Filter: nur Code-Files die nicht leer sind!
            List<string> filteredList = new();
            foreach (var code in codes)
            {
                if( !string.IsNullOrEmpty(code) && !string.IsNullOrWhiteSpace(code) )
                    filteredList.Add(code);
            }

            string? compiledUserControlDllPath = await CompilationService.CompileToDllAsync(
                filteredList,
                DllPath,
                AppState.NuGetDlls,
                AdditionalDlls
            );

            if (string.IsNullOrEmpty(compiledUserControlDllPath))
            {
                Logger.LogMessage("Compilation failed or returned no DLL path. Aborting.", BubbleType.CompletionError);
                LastErrorMsg = "Compilation failed.";
                return null;
            }

            return compiledUserControlDllPath;
        }

        private async Task<bool> DisplayCompiledDllAsync(string compiledUserControlDllPath)
        {
            // Unload old control right before loading the new one
            await ChildProcessService.UnloadControlAsync();

            bool success = await ChildProcessService.DisplayControlAsync(
                mainDllPath: compiledUserControlDllPath,
                nugetDlls: AppState.NuGetDlls ?? Enumerable.Empty<string>(),
                additionalDlls: AdditionalDlls
            );

            if (!success)
            {
                Logger.LogMessage("The compiled code could not be activated in the child process.", BubbleType.CompletionError);
                LastErrorMsg = "Failed to load the control in the child process.";
                return false;
            }

            await _view.HideFrostedSnapshotAsync();
            ChildProcessService.ShowChild();
            ChildProcessService.UpdatePosition();

            LastErrorMsg = "";
            return true;
        }

        private async Task<bool> CompileAndShowAsync(List<string> codes)
        {
            string? compiledUserControlDllPath = await CompileToDllAsync(codes);
            if (string.IsNullOrEmpty(compiledUserControlDllPath))
                return false;

            return await DisplayCompiledDllAsync(compiledUserControlDllPath);
        }

        public static bool ContainsNamedUserControl(string code, string className)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(className))
                return false;

            // Robust: SyntaxTree parsen und gezielt nach einer Klassendeklaration suchen,
            // deren Name passt und deren Basistyp "UserControl" ist (egal ob mit sealed/partial/etc.)
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();

            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (!string.Equals(cls.Identifier.ValueText, className, StringComparison.Ordinal))
                    continue;

                if (cls.BaseList == null)
                    continue;

                foreach (var baseType in cls.BaseList.Types)
                {
                    if (IsUserControlType(baseType.Type))
                        return true;
                }
            }

            return false;
        }

        private static bool IsUserControlType(TypeSyntax typeSyntax)
        {
            // Wir prüfen syntaktisch auf den rechten/letzten Namensteil == "UserControl".
            // Damit funktionieren u.a.:
            // - UserControl
            // - System.Windows.Controls.UserControl
            // - Avalonia.Controls.UserControl
            // - global::System.Windows.Controls.UserControl
            var rightMost = GetRightMostIdentifier(typeSyntax);
            return string.Equals(rightMost, "UserControl", StringComparison.Ordinal);
        }

        private static string? GetRightMostIdentifier(TypeSyntax typeSyntax)
        {
            switch (typeSyntax)
            {
                case IdentifierNameSyntax ins:
                    return ins.Identifier.ValueText;
                case GenericNameSyntax gns:
                    return gns.Identifier.ValueText;
                case QualifiedNameSyntax qns:
                    return GetRightMostIdentifier(qns.Right);
                case AliasQualifiedNameSyntax aqns:
                    return GetRightMostIdentifier(aqns.Name);
                default:
                    return null;
            }
        }
    }
}
