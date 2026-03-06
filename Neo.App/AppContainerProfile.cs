using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Neo.App
{
    /// <summary>
    /// Repräsentiert ein AppContainer-Profil (Moniker + SID).
    /// Erstellt bei Bedarf, liefert den konkreten LowBox-SID.
    /// Optionales Auto-Delete beim Dispose.
    /// </summary>
    public sealed class AppContainerProfile : IDisposable
    {
        public string Name { get; }
        public SecurityIdentifier Sid { get; }
        private readonly bool _createdNow;
        private bool _disposed;

        private AppContainerProfile(string name, SecurityIdentifier sid, bool createdNow)
        {
            Name = name;
            Sid = sid;
            _createdNow = createdNow;
        }

        /// <summary>
        /// Erzeugt (oder leitet ab) ein Profil mit dem angegebenen Namen.
        /// </summary>
        public static AppContainerProfile Create(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
                throw new ArgumentException("profileName must not be empty.", nameof(profileName));

            IntPtr sidPtr = IntPtr.Zero;
            bool createdNow = false;

            const int S_OK = 0;
            const int ALREADY_EXISTS = unchecked((int)0x800700B7);

            try
            {
                int hr = CreateAppContainerProfile(profileName, "Sandbox", "Temporary Sandbox Profile",
                                                   IntPtr.Zero, 0, out sidPtr);
                if (hr == S_OK)
                {
                    createdNow = true;
                }
                else if (hr == ALREADY_EXISTS)
                {
                    // ableiten
                    if (sidPtr != IntPtr.Zero) { LocalFree(sidPtr); sidPtr = IntPtr.Zero; }
                    int hr2 = AppContainerDeriveSidFromMoniker(profileName, out sidPtr);
                    if (hr2 != S_OK) throw Marshal.GetExceptionForHR(hr2)!;
                }
                else
                {
                    throw Marshal.GetExceptionForHR(hr)!;
                }

                var sid = new SecurityIdentifier(sidPtr);
                return new AppContainerProfile(profileName, sid, createdNow);
            }
            finally
            {
                if (sidPtr != IntPtr.Zero) LocalFree(sidPtr); // SecurityIdentifier hat eigene Kopie
            }
        }

        /// <summary>
        /// Generiert einen frischen Profilnamen (GUID-basiert) und erstellt/ableitet das Profil.
        /// </summary>
        public static AppContainerProfile CreateNewGuid()
            => Create("SandboxProfile_" + Guid.NewGuid().ToString("N"));

        /// <summary>
        /// Löscht das Profil nur, wenn es in diesem Lauf erstellt wurde. Optional aufrufen,
        /// viele Apps lassen Profile auch bestehen.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_createdNow)
                {
                    // Profil löschen – der bereits gestartete Prozess behält sein Token.
                    _ = DeleteAppContainerProfile(Name);
                }
            }
            catch
            {
                // best effort
            }
        }

        // P/Invoke
        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        private static extern int CreateAppContainerProfile(
            string pszAppContainerName,
            string pszDisplayName,
            string pszDescription,
            IntPtr pCapabilities,
            uint dwCapabilityCount,
            out IntPtr ppSid);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        private static extern int AppContainerDeriveSidFromMoniker(string pszAppContainerName, out IntPtr ppsidAppContainerSid);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        private static extern int DeleteAppContainerProfile(string pszAppContainerName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);
    }
}
