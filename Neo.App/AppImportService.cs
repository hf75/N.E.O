using Newtonsoft.Json;
using System.IO;

namespace Neo.App
{
    /// <summary>
    /// Stellt das Ergebnis eines Importvorgangs dar.
    /// </summary>
    public record ImportResult(bool Success, ResxData? Data, string? ErrorMessage);

    public interface IAppImportService
    {
        /// <summary>
        /// Versucht, die Daten aus einer zuvor exportierten .exe-Datei zu importieren.
        /// </summary>
        /// <param name="assemblyPath">Der Pfad zur .exe-Datei.</param>
        /// <returns>Ein ImportResult-Objekt, das den Erfolg und die geladenen Daten enthält.</returns>
        ImportResult ImportFromAssembly(string assemblyPath);
    }

    public class AppImportService : IAppImportService
    {
        public ImportResult ImportFromAssembly(string resxPath)
        {
            try
            {
                if (!File.Exists(resxPath))
                {
                    return new ImportResult(false, null, $"File not found: {resxPath}");
                }

                if (!resxPath.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
                {
                    return new ImportResult(false, null, "The selected file is not a valid .resx file.");
                }

                string resxContent = File.ReadAllText(resxPath);
                ResxData? resxData = JsonConvert.DeserializeObject<ResxData>(resxContent);

                // Validierung der geladenen Daten
                if (resxData == null || string.IsNullOrWhiteSpace(resxData.Code) || resxData.Nuget == null)
                {
                    return new ImportResult(false, null, "The assembly's resource data seems to be corrupt or incomplete.");
                }

                return new ImportResult(true, resxData, null);
            }
            catch (Exception ex)
            {
                // Alle anderen Fehler (z.B. Lesefehler, JSON-Parse-Fehler) abfangen
                return new ImportResult(false, null, $"An error occurred during import: {ex.Message}");
            }
        }

        /// <summary>
        /// Ersetzt die .exe-Dateiendung durch .resx.
        /// </summary>
        private string GetResxPathFromExePath(string exePath)
        {
            const string exeSuffix = ".exe";
            if (exePath.EndsWith(exeSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return exePath.Remove(exePath.Length - exeSuffix.Length) + ".resx";
            }
            return exePath; // Sollte nicht passieren, wenn die Eingabe validiert ist
        }
    }
}