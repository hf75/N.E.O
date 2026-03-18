using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Neo.App
{
    public enum BubbleType
    {
        Prompt,
        Answer,
        CompletionError,
        CompletionSuccess,
        Info
    }

    public enum CrashReason
    {
        /// <summary>
        /// Der Child-Prozess hat eine unbehandelte Exception gemeldet.
        /// -> Löst die automatische KI-Reparatur aus.
        /// </summary>
        UnhandledException,

        /// <summary>
        /// Der Heartbeat-Timer ist abgelaufen; der Prozess reagiert nicht mehr.
        /// -> Zeigt den Benutzerdialog an.
        /// </summary>
        HeartbeatTimeout,

        /// <summary>
        /// Die Pipe-Verbindung wurde unerwartet getrennt (Prozess wurde z.B. im Task-Manager beendet).
        /// -> Zeigt den Benutzerdialog an.
        /// </summary>
        PipeDisconnected
    }

    public enum CreationMode
    {
        /// <summary>
        /// Wirft eine Exception, wenn das Zielverzeichnis nicht leer ist. Sicherster Modus.
        /// </summary>
        FailIfExists,

        /// <summary>
        /// Löscht das Zielverzeichnis vollständig, bevor die Solution erstellt wird. ACHTUNG: Datenverlust möglich!
        /// </summary>
        Overwrite,

        /// <summary>
        /// Versucht, in ein bestehendes Verzeichnis hinein zu erstellen. Kann fehlschlagen, wenn Konflikte bestehen.
        /// </summary>
        Merge
    }

    public enum CrossPlatformExport
    {
        NONE,
        WINDOWS,
        LINUX,
        OSX
    }
}
