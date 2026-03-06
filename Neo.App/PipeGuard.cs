// PipeGuard.cs
using System;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Neo.App
{
    public static class PipeGuard
    {
        /// <summary>
        /// Erstellt eine PipeSecurity, die NUR dem aktuellen Benutzer und dem angegebenen AppContainer-SID
        /// (plus optional SYSTEM/Admins) Zugriff erlaubt.
        /// </summary>
        public static PipeSecurity BuildPipeSecurity(SecurityIdentifier appContainerSid, bool allowAdminsAndSystem = true)
        {
            var ps = new PipeSecurity();

            // Aktueller Benutzer (Parent) – Vollzugriff
            var me = WindowsIdentity.GetCurrent().User!;
            ps.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));

            // Konkreter AppContainer – Read/Write + Instanz erstellen
            ps.AddAccessRule(new PipeAccessRule(
                appContainerSid,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

            if (allowAdminsAndSystem)
            {
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                ps.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));
                ps.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
            }

            // Nichts für World/Everyone, NICHT S-1-15-2-1 (generisch) hinzufügen!
            return ps;
        }

        /// <summary>
        /// Baut einen NamedPipeServerStream mit harter DACL.
        /// </summary>
        public static NamedPipeServerStream CreateServer(string pipeName, SecurityIdentifier appContainerSid, int maxInstances = 1)
        {
            var sec = BuildPipeSecurity(appContainerSid);
            return NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                maxInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                pipeSecurity: sec);
        }

        /// <summary>
        /// Prüft nach erfolgreicher Verbindung, ob der Client wirklich dein Child ist:
        /// 1) PID == expectedPid
        /// 2) Token-AppContainerSid == expectedAppContainerSid
        /// Wirft UnauthorizedAccessException bei Verstoß.
        /// </summary>
        public static void VerifyClient(NamedPipeServerStream server, int expectedPid, SecurityIdentifier expectedAppContainerSid)
        {
            // 1) PID prüfen
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out uint clientPid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetNamedPipeClientProcessId failed.");

            if ((int)clientPid != expectedPid)
                throw new UnauthorizedAccessException($"Unexpected client PID {clientPid}, expected {expectedPid}.");

            // 2) AppContainer SID via Impersonation prüfen
            if (!ImpersonateNamedPipeClient(server.SafePipeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ImpersonateNamedPipeClient failed.");

            try
            {
                if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, true, out IntPtr hTok))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenThreadToken failed.");

                try
                {
                    // Größe ermitteln
                    if (!GetTokenInformation(hTok, TokenAppContainerSid, IntPtr.Zero, 0, out int size) && Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation(size) failed.");

                    if (size <= 0)
                        throw new UnauthorizedAccessException("Client token has no AppContainerSid (not a LowBox token).");

                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        if (!GetTokenInformation(hTok, TokenAppContainerSid, buf, size, out _))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation(TokenAppContainerSid) failed.");

                        var clientAcSid = new SecurityIdentifier(buf);
                        if (!clientAcSid.Equals(expectedAppContainerSid))
                            throw new UnauthorizedAccessException($"Unexpected AppContainer SID: {clientAcSid.Value}");
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { CloseHandle(hTok); }
            }
            finally
            {
                RevertToSelf();
            }
        }

        // ---------------- P/Invoke ----------------

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(SafeHandle Pipe, out uint ClientProcessId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ImpersonateNamedPipeClient(SafeHandle hNamedPipe);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool RevertToSelf();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenThreadToken(IntPtr ThreadHandle, uint DesiredAccess, bool OpenAsSelf, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const int TokenAppContainerSid = 29; // TOKEN_INFORMATION_CLASS
        private const uint TOKEN_QUERY = 0x0008;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
    }
}
