// Parent: PipeServer.cs
using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Neo.IPC;

namespace Neo.App
{
    public sealed class PipeServer : IDisposable, IAsyncDisposable
    {
        public readonly string PipeName;
        private readonly NamedPipeServerStream _pipe;
        private readonly FramedPipeMessenger _messenger;

        public Stream UnderlyingStream => _pipe;
        public NamedPipeServerStream Underlying => _pipe; // optionaler bequemer Zugriff
        public bool IsConnected => _pipe.IsConnected;

        /// <summary>
        /// Pipe für den Nicht-Sandbox-Modus: Nur der aktuelle Benutzer erhält Zugriff.
        /// Für Sandbox-Modus den Konstruktor mit konkreter AppContainer-SID verwenden.
        /// </summary>
        public PipeServer(string pipeName)
        {
            PipeName = pipeName;

            var pipeSecurity = new PipeSecurity();

            // Nur aktueller Benutzer — ohne Sandbox läuft der Child-Prozess als gleicher User
            var me = WindowsIdentity.GetCurrent().User;
            if (me != null)
            {
                pipeSecurity.AddAccessRule(
                    new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
            }

            _pipe = NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                1, // MaxNumberOfServerInstances
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0, // InBufferSize
                0, // OutBufferSize
                pipeSecurity);

            _messenger = new FramedPipeMessenger(_pipe);
        }

        /// <summary>
        /// Neuer, „harter“ Konstruktor:
        /// Nur aktueller Benutzer + KONKRETER AppContainer-SID (LowBox-SID) erhalten Zugriff.
        /// Optional Admins/SYSTEM.
        /// </summary>
        public PipeServer(string pipeName,
                          SecurityIdentifier allowedAppContainerSid,
                          bool allowAdminsAndSystem = true,
                          int maxInstances = 1)
        {
            PipeName = pipeName;

            var ps = new PipeSecurity();

            // Aktueller Benutzer (Full)
            var me = WindowsIdentity.GetCurrent().User!;
            ps.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));

            // Konkreter AC (Read/Write + CreateNewInstance)
            ps.AddAccessRule(new PipeAccessRule(
                allowedAppContainerSid,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

            if (allowAdminsAndSystem)
            {
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                ps.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));
                ps.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
            }

            _pipe = NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                maxInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0,
                0,
                ps);

            _messenger = new FramedPipeMessenger(_pipe);
        }

        public async Task WaitForClientAsync(CancellationToken ct = default)
        {
            await _pipe.WaitForConnectionAsync(ct);
        }

        /// <summary>
        /// Zusatzschutz nach erfolgreichem Connect:
        /// PID der Gegenseite muss expectedPid entsprechen.
        /// Wirft UnauthorizedAccessException bei Abweichung.
        /// </summary>
        public void VerifyClientPidOrThrow(int expectedPid)
        {
            if (!Native.GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out uint clientPid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetNamedPipeClientProcessId failed.");

            if ((int)clientPid != expectedPid)
                throw new UnauthorizedAccessException($"Unexpected client PID {clientPid}, expected {expectedPid}.");
        }

        /// <summary>
        /// Zusatzschutz nach erfolgreichem Connect:
        /// 1) PID der Gegenseite muss expectedPid entsprechen.
        /// 2) Token-AppContainerSid der Gegenseite muss expectedAppContainerSid entsprechen.
        /// Wirft UnauthorizedAccessException bei Abweichung.
        /// </summary>
        public void VerifyClientOrThrow(int expectedPid, SecurityIdentifier expectedAppContainerSid)
        {
            // 1) PID des Pipe-Clients holen
            if (!Native.GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out uint clientPid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetNamedPipeClientProcessId failed.");

            if ((int)clientPid != expectedPid)
                throw new UnauthorizedAccessException($"Unexpected client PID {clientPid}, expected {expectedPid}.");

            // 2) AppContainer-SID des Client-Tokens prüfen
            if (!Native.ImpersonateNamedPipeClient(_pipe.SafePipeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ImpersonateNamedPipeClient failed.");

            try
            {
                if (!Native.OpenThreadToken(Native.GetCurrentThread(), Native.TOKEN_QUERY, true, out IntPtr hTok))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenThreadToken failed.");

                try
                {
                    // Puffergröße ermitteln
                    if (!Native.GetTokenInformation(hTok, Native.TokenAppContainerSid, IntPtr.Zero, 0, out int size) &&
                        Marshal.GetLastWin32Error() != Native.ERROR_INSUFFICIENT_BUFFER)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation(size) failed.");

                    if (size <= 0)
                        throw new UnauthorizedAccessException("Client token has no AppContainerSid (not a LowBox token).");

                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        if (!Native.GetTokenInformation(hTok, Native.TokenAppContainerSid, buf, size, out _))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation(TokenAppContainerSid) failed.");

                        var clientAcSid = new SecurityIdentifier(buf);
                        if (!clientAcSid.Equals(expectedAppContainerSid))
                            throw new UnauthorizedAccessException($"Unexpected AppContainer SID: {clientAcSid.Value}");
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { Native.CloseHandle(hTok); }
            }
            finally
            {
                Native.RevertToSelf();
            }
        }

        public Task SendAsync(IpcEnvelope env, CancellationToken ct = default)
            => _messenger.SendControlAsync(env, ct);

        public Task<IpcEnvelope?> ReceiveAsync(CancellationToken ct = default)
            => _messenger.ReceiveControlAsync(ct);

        public FramedPipeMessenger Messenger => _messenger; // Zugriff für Blob-APIs

        public async ValueTask DisposeAsync()
        {
            try { _pipe.Dispose(); } catch { /* ignore */ }
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            try { _pipe.Dispose(); } catch { /* ignore */ }
        }

        // --------- P/Invoke-Helfer ---------
        private static class Native
        {
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool GetNamedPipeClientProcessId(SafeHandle Pipe, out uint ClientProcessId);

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool ImpersonateNamedPipeClient(SafeHandle hNamedPipe);

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool RevertToSelf();

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool OpenThreadToken(IntPtr ThreadHandle, uint DesiredAccess, bool OpenAsSelf, out IntPtr TokenHandle);

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);

            [DllImport("kernel32.dll")]
            public static extern IntPtr GetCurrentThread();

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr hObject);

            public const int TokenAppContainerSid = 29; // TOKEN_INFORMATION_CLASS
            public const uint TOKEN_QUERY = 0x0008;
            public const int ERROR_INSUFFICIENT_BUFFER = 122;
        }
    }
}
