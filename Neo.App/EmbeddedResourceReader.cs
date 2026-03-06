using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Linq; 

namespace Neo.App
{
    public static class EmbeddedResourceReader
    {
        public static string GetEmbeddedResourceContent(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly(); // Oder die relevante Assembly holen

            // Versuche, den vollständigen Ressourcennamen zu ermitteln
            string? fullResourceName = DetermineFullResourceName(assembly, resourceName);

            Stream? stream = null; // <-- Variable hier deklarieren, initial mit null

            try
            {
                // Versuch 1: Mit dem ermittelten vollständigen Namen
                if (!string.IsNullOrEmpty(fullResourceName))
                {
                    stream = assembly.GetManifestResourceStream(fullResourceName);
                }

                // Versuch 2: Fallback auf den ursprünglichen Namen, falls Versuch 1 fehlschlug
                if (stream == null)
                {
                    // WICHTIG: Hier weisen wir der *externen* Variable zu
                    stream = assembly.GetManifestResourceStream(resourceName);
                }

                // Prüfen, ob einer der Versuche erfolgreich war
                if (stream == null)
                {
                    var available = string.Join(", ", assembly.GetManifestResourceNames());
                    throw new FileNotFoundException($"Could not find embedded resource stream for potential names '{fullResourceName}' or '{resourceName}'. Available resources: {available}");
                }

                // Jetzt, da wir sicher einen Stream haben (oder eine Exception geworfen wurde),
                // können wir ihn sicher mit using verwenden.
                // Wichtig: Das using für den StreamReader reicht oft aus, da dieser
                // standardmäßig den zugrunde liegenden Stream mit-disposed.
                // Ein zusätzliches using(stream) schadet aber nicht und ist expliziter.
                using (stream) // Stellt sicher, dass der Stream disposed wird
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) // StreamReader verwendet den Stream
                {
                    return reader.ReadToEnd(); // Inhalt lesen und zurückgeben
                }
                // stream.Dispose() wird hier automatisch aufgerufen (durch using(stream))
                // reader.Dispose() wird hier automatisch aufgerufen (durch using(reader)),
                // was standardmäßig auch stream.Dispose() erneut aufruft (ist aber sicher).

            }
            catch
            {
                // Wenn ein Fehler beim Lesen auftritt, trotzdem sicherstellen,
                // dass der Stream (falls er geöffnet wurde) geschlossen wird.
                // Das wird aber durch das äußere 'using (stream)' bereits abgedeckt,
                // wenn die Exception *innerhalb* des using-Blocks auftritt.
                // Ein finally-Block wäre nur nötig, wenn die Zuweisung selbst fehlschlagen könnte
                // und wir manuell disposen müssten.
                // In diesem Fall fängt das using(stream) alles Nötige ab.
                throw; // Fehler weiterwerfen oder spezifisch behandeln
            }
            // KEIN finally mit stream?.Dispose() hier nötig, da das 'using (stream)' das abdeckt.
        }

        private static string? DetermineFullResourceName(Assembly assembly, string resourceName)
        {
            string potentialName = $"{assembly.GetName().Name}.{resourceName.Replace('/', '.').Replace('\\', '.')}";
            var allNames = assembly.GetManifestResourceNames();

            if (allNames.Contains(potentialName)) return potentialName;

            // Suche nach Suffix (Groß-/Kleinschreibung ignorieren für Robustheit)
            var match = allNames.FirstOrDefault(n => n.EndsWith(resourceName.Replace('/', '.').Replace('\\', '.'), StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            // Fallback: Manchmal ist der Standard-Namespace anders als der Assembly-Name
            Type[] types = assembly.GetTypes();
            if( types == null || types.Length == 0 )
                return null;

            Type? type = types.FirstOrDefault();
            if( type == null )
                return null;

            string? typeNameSpace = type.Namespace;
            if( string.IsNullOrEmpty( typeNameSpace ) ) 
                return null;

            string[] parts = typeNameSpace.Split('.');
            if (parts == null || parts.Length == 0)
                return null;

            string? defaultNamespace = parts.FirstOrDefault();

            if (!string.IsNullOrEmpty(defaultNamespace) && defaultNamespace != assembly.GetName().Name)
            {
                potentialName = $"{defaultNamespace}.{resourceName.Replace('/', '.').Replace('\\', '.')}";
                if (allNames.Contains(potentialName)) return potentialName;
            }


            return null; // Nicht gefunden
        }
    }
}
