using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Neo.App
{
    public static class ProcessFactory
    {
        /// <summary>
        /// Startet den Prozess im angegebenen AppContainer-Profil (vorher via AppContainerProfile.Create erzeugt).
        /// Nutzt deinen bestehenden Staging/ACL/Env-Block-Ansatz – nur ohne Profil-Erzeugung/-Löschung.
        /// </summary>
        public static Process? StartInAppContainer(string executablePath, string arguments, SandboxSettings settings, AppContainerProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            // ---- Alles ab hier ist dein bestehender, funktionierender Code – leicht adaptiert ----
             string profileName = profile.Name;
             var acSid = profile.Sid; // konkreter LowBox SID
 
             IntPtr attributeList = IntPtr.Zero;
             IntPtr appContainerSidPtr = IntPtr.Zero;
             IntPtr securityCapabilitiesPtr = IntPtr.Zero;
             IntPtr sidAndAttributesArrayPtr = IntPtr.Zero;
             PROCESS_INFORMATION pi = new();

            var capabilityBlocksToFree = new List<IntPtr>();

            try
            {
                // 1) Capabilities aus Settings
                var capabilityNames = new List<string> { "privateNetworkClientServer" };
                if (settings.AllowNetworkAccess) capabilityNames.Add("internetClient");
                if (settings.AllowUserFileAccess) capabilityNames.AddRange(new[] { "documentsLibrary", "picturesLibrary", "videosLibrary", "musicLibrary" });
                if (settings.AllowWebcamAndMicrophone) capabilityNames.AddRange(new[] { "webcam", "microphone" });

                // 2) SID_AND_ATTRIBUTES[]
                uint capabilityCount;
                sidAndAttributesArrayPtr = BuildSidAndAttributesArray(capabilityNames, out capabilityCount, capabilityBlocksToFree);

                // 3) SECURITY_CAPABILITIES (mit bereits vorhandenem AppContainerSid)
                 appContainerSidPtr = GetSidPointerFromSecurityIdentifier(acSid);
                 var secCaps = new SECURITY_CAPABILITIES
                 {
                     AppContainerSid = appContainerSidPtr,
                     Capabilities = sidAndAttributesArrayPtr,
                     CapabilityCount = capabilityCount,
                     Reserved = 0
                 };

                securityCapabilitiesPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_CAPABILITIES>());
                Marshal.StructureToPtr(secCaps, securityCapabilitiesPtr, false);

                // 4) AttributeList initialisieren
                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attributeList = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");

                if (!UpdateProcThreadAttribute(
                        attributeList, 0,
                        PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES,
                        securityCapabilitiesPtr,
                        (IntPtr)Marshal.SizeOf<SECURITY_CAPABILITIES>(),
                        IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
                }
                
                // 5) Staging (dein Code, unverändert im Sinne des Verhaltens)
                string workRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Neo", "ac_work", profileName);
                
                try {
                    Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neo", "ac_work"), true);
                } catch { /* may not exist or be in use */ }
                
                Directory.CreateDirectory(workRoot); // Root normal lassen
                AcStaging.EnsureDirectoryAcl(workRoot, acSid);

                string stagedExe = AcStaging.StageExecutableTree(executablePath, workRoot, acSid);
                string stagedDir = Path.GetDirectoryName(stagedExe)!;
                string tempDir = Path.Combine(workRoot, "temp");
                
                AcStaging.EnsureDirectoryAcl(tempDir, acSid);

                // Für WebView 2

                string wv2Base = Path.Combine(workRoot, "wv2");
                Directory.CreateDirectory(wv2Base);
                AcStaging.EnsureDirectoryAcl(wv2Base, acSid);

                string udf = Path.Combine(workRoot, "wv2", "profile");
                Directory.CreateDirectory(udf);
                AcStaging.EnsureDirectoryAcl(udf, acSid);

                // Optional: auch einen Cache trennen
                string wv2Cache = Path.Combine(workRoot, "wv2", "cache");
                Directory.CreateDirectory(wv2Cache);
                AcStaging.EnsureDirectoryAcl(wv2Cache, acSid);

                foreach( string d in settings.GrantedFolders )
                {
                    Directory.CreateDirectory(d);
                    AcStaging.EnsureDirectoryAcl(d, acSid);
                }

                // 

                var additionalPath = new Dictionary<string, string>
                {
                    ["AC_WORK"] = workRoot,
                    ["WEBVIEW2_USER_DATA_FOLDER"] = udf,
                    ["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = "--no-first-run"
                };
                if (settings.GrantedFolders.Any())
                    additionalPath["SHARED_FOLDER"] = settings.GrantedFolders[0];

                // Minimale Leserechte für Default-Ordner (quasi nur ansehen)

                var foldersToAllow = new[]
                    {
                        Environment.SpecialFolder.Desktop,
                        //Environment.SpecialFolder.MyDocuments,
                        //Environment.SpecialFolder.MyPictures,
                        //Environment.SpecialFolder.MyMusic,
                        //Environment.SpecialFolder.MyVideos
                        // Environment.SpecialFolder.UserProfile ist oft auch eine gute Idee
                    };

                foreach (var specialFolder in foldersToAllow)
                {
                    string folderPath = Environment.GetFolderPath(specialFolder);
                    if (!string.IsNullOrEmpty(folderPath))
                    {
                        GrantMinimalReadAccessCanonically(folderPath, acSid);
                    }
                }

                // 6) Env-Block
                IntPtr envBlock = IntPtr.Zero;
                try
                {
                    envBlock = AcEnv.BuildUnicodeEnvWithTemp(
                        baseTemp: tempDir,
                        dotnetRoot: null,           // optional private Runtime
                        setBundleExtract: true,
                         extra: additionalPath);

                    var si = new STARTUPINFOEX();
                    si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                    si.lpAttributeList = attributeList;
                    si.StartupInfo.lpDesktop = "winsta0\\default";

                    uint creationFlags = EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT;

                    string cmdLine = $"\"{stagedExe}\" {arguments}".TrimEnd();

                    if (!CreateProcess(
                            stagedExe,
                            cmdLine,
                            IntPtr.Zero, IntPtr.Zero,
                            false,
                            creationFlags,
                            envBlock,
                            stagedDir,
                            ref si,
                            out pi))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess in AppContainer failed.");
                    }

                    // Exit-Frühdiagnose
                    if (!GetExitCodeProcess(pi.hProcess, out uint ec))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "GetExitCodeProcess failed.");
                    if (ec != STILL_ACTIVE)
                        throw new InvalidOperationException($"AC child exited immediately. ExitCode=0x{ec:X8} ({ec})");

                    try { WaitForInputIdle(pi.hProcess, 5000); } catch { /* best-effort GUI init */ }

                    Process? p = null;
                    try { p = Process.GetProcessById(pi.dwProcessId); }
                    catch
                    {
                        for (int i = 0; i < 5 && p == null; i++)
                        {
                            System.Threading.Thread.Sleep(50);
                            try { p = Process.GetProcessById(pi.dwProcessId); } catch { /* retry in loop */ }
                        }
                        if (p == null) throw new InvalidOperationException("Process.GetProcessById failed although child looks alive.");
                    }

                    return p;
                }
                finally
                {
                    if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
                }
            }
            finally
            {
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);

                 if (attributeList != IntPtr.Zero)
                 {
                     DeleteProcThreadAttributeList(attributeList);
                     Marshal.FreeHGlobal(attributeList);
                 }

                 if (appContainerSidPtr != IntPtr.Zero) Marshal.FreeHGlobal(appContainerSidPtr);
                 if (securityCapabilitiesPtr != IntPtr.Zero) Marshal.FreeHGlobal(securityCapabilitiesPtr);
                 if (sidAndAttributesArrayPtr != IntPtr.Zero) Marshal.FreeHGlobal(sidAndAttributesArrayPtr);
 
                 foreach (var block in capabilityBlocksToFree)
                     if (block != IntPtr.Zero) LocalFree(block);
            }
        }

        public static void GrantMinimalReadAccessCanonically(string dirPath, SecurityIdentifier acSid)
        {
            try
            {
                var di = new DirectoryInfo(dirPath);
                if (!di.Exists) return;

                // 1. Bestehende ACL und Besitzerinformationen auslesen
                var originalSd = di.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);

                // 2. Eine neue, leere DirectorySecurity erstellen. Diese ist garantiert kanonisch.
                var newSd = new DirectorySecurity();

                // 3. Besitzer und Gruppe vom Original übernehmen (wichtig!)
                newSd.SetOwner(originalSd.GetOwner(typeof(SecurityIdentifier))!);
                newSd.SetGroup(originalSd.GetGroup(typeof(SecurityIdentifier))!);

                // 4. Alle bestehenden Regeln in die neue ACL kopieren.
                //    Dabei werden sie automatisch in die korrekte kanonische Reihenfolge gebracht.
                foreach (FileSystemAccessRule oldRule in originalSd.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    newSd.AddAccessRule(oldRule);
                }

                // 5. JETZT unsere neue Regel zur sauberen, kanonischen ACL hinzufügen.
                var newRule = new FileSystemAccessRule(
                    acSid,
                    FileSystemRights.Read,
                    InheritanceFlags.None,
                    PropagationFlags.None,
                    AccessControlType.Allow);
                //var newRule = new FileSystemAccessRule(
                //    acSid,
                //    FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions, // <-- Genau das, was wir brauchen
                //    InheritanceFlags.None,
                //    PropagationFlags.None,
                //    AccessControlType.Allow);

                newSd.AddAccessRule(newRule);

                // 6. Die komplett neue, korrigierte ACL auf das Verzeichnis anwenden.
                di.SetAccessControl(newSd);

                Debug.WriteLine($"Minimaler Lesezugriff für '{dirPath}' erfolgreich und kanonisch gesetzt.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Fehler beim kanonischen Setzen des Lesezugriffs für '{dirPath}': {ex.Message}");
                // Hier könnten Sie entscheiden, ob der Fehler kritisch ist oder ignoriert werden kann.
            }
        }

        private static IntPtr GetSidPointerFromSecurityIdentifier(SecurityIdentifier sid)
        {
            // SECURITY_CAPABILITIES erwartet ein PSID (Pointer). Wir duplizieren die Bytes in unmanaged memory.
            byte[] bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);
            IntPtr p = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            return p;
        }

        /// <summary>
        /// Startet einen Prozess normal oder in AppContainer-Sandbox.
        /// </summary>
        public static Process? StartProcess(string executablePath, string arguments, bool useSandbox, SandboxSettings settings)
        {
            if (!useSandbox)
            {
                try
                {
                    var psi = new ProcessStartInfo(executablePath)
                    {
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    return Process.Start(psi);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to start process normally: {ex.Message}");
                    return null;
                }
            }

            try
            {
                return StartInAppContainer(executablePath, arguments, settings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to start process in AppContainer: {ex}");
                System.Windows.MessageBox.Show(
                    $"Fehler beim Starten des sandboxed Prozesses:\n{ex.Message}",
                    "Sandbox Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                return null;
            }
        }

        // ------------------------------------------------------------
        //  AppContainer-Start
        // ------------------------------------------------------------
        private static Process? StartInAppContainer(string executablePath, string arguments, SandboxSettings settings)
        {
            string profileName = "SandboxProfile_" + Guid.NewGuid().ToString("N");

            IntPtr appContainerSid = IntPtr.Zero;         // via CreateAppContainerProfile/AppContainerDeriveSidFromMoniker
            IntPtr attributeList = IntPtr.Zero;           // für STARTUPINFOEX
            IntPtr securityCapabilitiesPtr = IntPtr.Zero; // Heap-Block für SECURITY_CAPABILITIES
            IntPtr sidAndAttributesArrayPtr = IntPtr.Zero;// Heap-Block (SID_AND_ATTRIBUTES[])
            PROCESS_INFORMATION pi = new();

            // diese Blöcke werden von DeriveCapabilitySidsFromName geliefert und per LocalFree freigegeben
            var capabilityBlocksToFree = new List<IntPtr>(); // Arrays (PSID*) für cap/group SIDs

            try
            {
                // 1) Capability-Namen aus Settings bestimmen
                var capabilityNames = new List<string> { "privateNetworkClientServer" }; // Für Pipe immer erlauben
                if (settings.AllowNetworkAccess)
                    capabilityNames.Add("internetClient");

                if (settings.AllowUserFileAccess)
                {
                    // Hinweis: diese *Library*-Capabilities sind in Desktop-AppContainern meist symbolisch;
                    // wir lassen sie als Demo drin – Schaden richten sie nicht an.
                    capabilityNames.AddRange(new[] { "documentsLibrary", "picturesLibrary", "videosLibrary", "musicLibrary" });
                }
                if (settings.AllowWebcamAndMicrophone)
                {
                    capabilityNames.Add("webcam");
                    capabilityNames.Add("microphone");
                }

                // 2) SID_AND_ATTRIBUTES[] aufbauen (aus DeriveCapabilitySidsFromName)
                uint capabilityCount;
                sidAndAttributesArrayPtr = BuildSidAndAttributesArray(capabilityNames, out capabilityCount, capabilityBlocksToFree);

                // 3) AppContainer-Profil anlegen (oder vorhandenen SID ableiten)
                int hr = CreateAppContainerProfile(profileName, "Sandbox", "Temporary Sandbox Profile",
                                                   IntPtr.Zero, 0, out appContainerSid);

                const int S_OK = 0;
                const int HRESULT_FROM_WIN32_ALREADY_EXISTS = unchecked((int)0x800700B7);

                if (hr != S_OK)
                {
                    if (hr == HRESULT_FROM_WIN32_ALREADY_EXISTS)
                    {
                        int hr2 = AppContainerDeriveSidFromMoniker(profileName, out appContainerSid);
                        if (hr2 != S_OK) throw Marshal.GetExceptionForHR(hr2)!;
                    }
                    else
                    {
                        throw Marshal.GetExceptionForHR(hr)!;
                    }
                }

                // 4) SECURITY_CAPABILITIES befüllen
                var secCaps = new SECURITY_CAPABILITIES
                {
                    AppContainerSid = appContainerSid,
                    Capabilities = sidAndAttributesArrayPtr,
                    CapabilityCount = capabilityCount,
                    Reserved = 0
                };
                securityCapabilitiesPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_CAPABILITIES>());
                Marshal.StructureToPtr(secCaps, securityCapabilitiesPtr, false);

                // 5) AttributeList initialisieren und SECURITY_CAPABILITIES anhängen
                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size); // Größe ermitteln
                attributeList = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");

                if (!UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES,
                        securityCapabilitiesPtr,
                        (IntPtr)Marshal.SizeOf<SECURITY_CAPABILITIES>(),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
                }

                // 6) Arbeitsbereiche vorbereiten (Root normal, app/temp hart ACL in StageExecutableTree)
                var acSid = new SecurityIdentifier(appContainerSid); // IntPtr -> managed SID

                string workRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Neo", "ac_work", profileName);

                Directory.CreateDirectory(workRoot); // Root NICHT verrammeln, sonst sperrst du dich aus.

                // Binaries stagen (kopiert EXE-Verzeichnis rekursiv → workRoot\app, setzt harte DACL für AC + User)
                string stagedExe = AcStaging.StageExecutableTree(executablePath, workRoot, acSid);
                string stagedDir = Path.GetDirectoryName(stagedExe)!;

                // TEMP/TMP sollen auf workRoot\temp zeigen (wird in StageExecutableTree angelegt + ACL gesetzt)
                string tempDir = Path.Combine(workRoot, "temp");

                // Optional: private dotnet-Runtime neben der EXE (falls framework-dependent gewünscht)
                // string dotnetRoot = Path.Combine(stagedDir, "dotnet");
                // bool havePrivateDotnet = Directory.Exists(dotnetRoot);

                // 7) Environment-Block bauen (Unicode; TEMP/TMP; optional DOTNET_ROOT; Single-File Extract Pfad)
                IntPtr envBlock = IntPtr.Zero;
                try
                {
                    envBlock = AcEnv.BuildUnicodeEnvWithTemp(
                        baseTemp: tempDir,
                        dotnetRoot: null /* havePrivateDotnet ? dotnetRoot : null */,
                        setBundleExtract: true);

                    // 8) STARTUPINFOEX befüllen (inkl. Desktop)
                    var si = new STARTUPINFOEX();
                    si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                    si.lpAttributeList = attributeList;
                    si.StartupInfo.lpDesktop = "winsta0\\default"; // WPF mag das

                    // 9) CreateProcess (Unicode-Env, kein Fenster)
                    uint creationFlags = EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT;

                    // Wichtig: lpApplicationName explizit setzen, CurrentDirectory = stagedDir
                    string cmdLine = $"\"{stagedExe}\" {arguments}".TrimEnd();

                    if (!CreateProcess(
                            stagedExe,     // lpApplicationName (explizit!)
                            cmdLine,       // command line (inkl. exe)
                            IntPtr.Zero,
                            IntPtr.Zero,
                            false,
                            creationFlags,
                            envBlock,      // Unicode-Environment
                            stagedDir,     // CurrentDirectory = Staging
                            ref si,
                            out pi))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess in AppContainer failed.");
                    }

                    // 10) Sofort-Exit diagnostizieren (ohne Race)
                    if (!GetExitCodeProcess(pi.hProcess, out uint ec))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "GetExitCodeProcess failed.");

                    if (ec != STILL_ACTIVE) // 259
                        throw new InvalidOperationException($"AC child exited immediately. ExitCode=0x{ec:X8} ({ec})");

                    // (optional) GUI init: WaitForInputIdle
                    try { WaitForInputIdle(pi.hProcess, 5000); } catch { /* best effort */ }

                    // 11) Process-Objekt best-effort holen
                    Process? p = null;
                    try { p = Process.GetProcessById(pi.dwProcessId); }
                    catch
                    {
                        for (int i = 0; i < 5 && p == null; i++)
                        {
                            Thread.Sleep(50);
                            try { p = Process.GetProcessById(pi.dwProcessId); } catch { /* retry in loop */ }
                        }
                        if (p == null) throw new InvalidOperationException("Process.GetProcessById failed although child looks alive.");
                    }

                    return p;
                }
                finally
                {
                    if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
                }
            }
            finally
            {
                // Handles
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);

                // AttributeList
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }

                // Heap-Blöcke
                if (securityCapabilitiesPtr != IntPtr.Zero) Marshal.FreeHGlobal(securityCapabilitiesPtr);
                if (sidAndAttributesArrayPtr != IntPtr.Zero) Marshal.FreeHGlobal(sidAndAttributesArrayPtr);

                // Von UserEnv (LocalAlloc) gelieferte Blöcke freigeben
                foreach (var block in capabilityBlocksToFree)
                {
                    if (block != IntPtr.Zero) LocalFree(block);
                }

                // AppContainer-SID freigeben (LocalFree, nicht FreeSid!)
                if (appContainerSid != IntPtr.Zero) LocalFree(appContainerSid);

                // Profil wieder löschen (optional; der gestartete Prozess behält sein Token)
                if (!string.IsNullOrEmpty(profileName))
                {
                    _ = DeleteAppContainerProfile(profileName);
                }
            }
        }


        /// <summary>
        /// Erstellt ein SID_AND_ATTRIBUTES[] im eigenen Heap, basierend auf Capability-Namen.
        /// Zusätzlich werden die von UserEnv gelieferten Array-Blöcke (PSID*) gesammelt,
        /// damit wir sie in der Aufräumphase via LocalFree freigeben können.
        /// </summary>
        private static IntPtr BuildSidAndAttributesArray(
            List<string> capabilityNames,
            out uint count,
            List<IntPtr> blocksToFree)
        {
            var sids = new List<IntPtr>();

            foreach (var name in capabilityNames)
            {
                // DeriveCapabilitySidsFromName liefert:
                // - capabilityGroupSids: PSID* (Array)
                // - capabilitySids:      PSID* (Array) -> wir nutzen i.d.R. das erste Element
                if (!DeriveCapabilitySidsFromName(
                        name,
                        out var groupSids, out var groupCount,
                        out var capSids, out var capCount))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"DeriveCapabilitySidsFromName failed for '{name}'.");
                }

                // Die beiden zurückgegebenen Array-Blöcke merken, um sie später freizugeben
                blocksToFree.Add(groupSids);
                blocksToFree.Add(capSids);

                if (capCount > 0)
                {
                    // erstes PSID aus dem capSids-Array lesen
                    IntPtr firstCapSid = Marshal.ReadIntPtr(capSids, 0);
                    sids.Add(firstCapSid);
                }
                else
                {
                    // Es ist ungewöhnlich, aber wir ignorieren fehlende cap SIDs still – oder man wirft hier eine Exception.
                    // throw new InvalidOperationException($"Capability '{name}' returned no capability SIDs.");
                }
            }

            int elemSize = Marshal.SizeOf<SID_AND_ATTRIBUTES>();
            IntPtr arrayPtr = Marshal.AllocHGlobal(elemSize * Math.Max(1, sids.Count));

            for (int i = 0; i < sids.Count; i++)
            {
                var sas = new SID_AND_ATTRIBUTES
                {
                    Sid = sids[i],
                    Attributes = 0x00000004 /* SE_GROUP_ENABLED */
                };
                Marshal.StructureToPtr(sas, arrayPtr + i * elemSize, false);
            }

            count = (uint)sids.Count;
            return arrayPtr;
        }

        // ------------------------------------------------------------
        //  P/Invoke
        // ------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SECURITY_CAPABILITIES
        {
            public IntPtr AppContainerSid;
            public IntPtr Capabilities;   // -> SID_AND_ATTRIBUTES[]
            public uint CapabilityCount;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SID_AND_ATTRIBUTES
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        // CreateProcessW
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcess(
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            [In] ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        // AppContainer-APIs geben HRESULT zurück (INT), nicht BOOL/LastError!
        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        static extern int CreateAppContainerProfile(
            string pszAppContainerName,
            string pszDisplayName,
            string pszDescription,
            IntPtr pCapabilities,              // PSID_AND_ATTRIBUTES* (optional)
            uint dwCapabilityCount,
            out IntPtr ppSid                   // PSID (LocalAlloc)
        );

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        static extern int DeleteAppContainerProfile(string pszAppContainerName);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        static extern int AppContainerDeriveSidFromMoniker(string pszAppContainerName, out IntPtr ppsidAppContainerSid);

        // Liefert zwei PSID*-Arrays (LocalAlloc), die wir nach Benutzung via LocalFree freigeben
        [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DeriveCapabilitySidsFromName(
            string CapName,
            out IntPtr capabilityGroupSids, out uint capabilityGroupSidCount, // PSID**
            out IntPtr capabilitySids, out uint capabilitySidCount       // PSID**
        );

        // AttributeList
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll")]
        static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr LocalFree(IntPtr hMem);

        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

        private static readonly IntPtr PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES = (IntPtr)0x00020009;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint WaitForInputIdle(IntPtr hProcess, uint dwMilliseconds);

        const uint STILL_ACTIVE = 259;
    }

    internal static class AcIoHelpers
    {
        // Setzt DACL: Vollzugriff für AppContainer SID auf Ordner
        public static void EnsureDirectoryForAppContainer(string dir, SecurityIdentifier appContainerSid)
        {
            var di = new DirectoryInfo(dir);
            di.Create(); // idempotent

            // eigene DACL aufsetzen (keine Vererbung)
            var sd = new DirectorySecurity();
            sd.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            // Vollzugriff für den AppContainer-SID
            var rule = new FileSystemAccessRule(
                appContainerSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow);
            sd.AddAccessRule(rule);

            // Optional: BUILTIN\Users Leserechte (wenn gewünscht)
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            sd.AddAccessRule(new FileSystemAccessRule(
                users,
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            // HIER: Extension-Methode aus System.IO.FileSystem.AccessControl
            di.SetAccessControl(sd);
        }

        // Baut ein Unicode-Environment-Block (key=value\0 ... \0\0)
        public static IntPtr BuildEnvironmentBlockWithTemp(string baseTemp)
        {
            // 1) Basis: aktuelles Prozess-Environment (damit SystemRoot, ComSpec, Path etc. garantiert dabei sind)
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry de in Environment.GetEnvironmentVariables())
            {
                env[(string)de.Key] = (string?)de.Value ?? string.Empty;
            }

            // 2) TEMP/TMP überschreiben → auf unser beschreibbares Verzeichnis zeigen
            env["TEMP"] = baseTemp;
            env["TMP"] = baseTemp;

            // 3) Safety: SystemRoot ist für Loader/Subsystems kritisch – sicherstellen
            var sysRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrEmpty(sysRoot))
                env["SystemRoot"] = sysRoot;

            // (Optional: falls du USERPROFILE in AC bewusst „leer“ willst, hier setzen/entfernen)

            // 4) In Unicode-Block umwandeln: "k=v\0k=v\0...\0\0"
            // Sortierung ist nicht zwingend, aber traditionell ok:
            var keys = env.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
            var sb = new System.Text.StringBuilder();
            foreach (var k in keys)
            {
                var v = env[k] ?? string.Empty;
                // Keine '=' im Key erlaubt (sollte es „=C:=“ geben, überspringen)
                if (k.Contains('=')) continue;
                sb.Append(k).Append('=').Append(v).Append('\0');
            }
            sb.Append('\0'); // Doppel-Null-Terminierung

            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(sb.ToString());

            IntPtr block = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, block, bytes.Length);
            return block;
        }
    }

    internal static class AcStaging
    {
        public static void EnsureDirectoryAcl(string dir, SecurityIdentifier acSid, bool keepUserAccess = true)
        {
            var di = new DirectoryInfo(dir);
            di.Create(); // stellt sicher, dass der Ordner existiert

            // Bestehende ACL als Basis (damit wir uns nicht aus Versehen aussperren)
            var sd = di.GetAccessControl();

            // Schutz aktivieren, aber bestehende Regeln optional behalten
            sd.SetAccessRuleProtection(isProtected: true, preserveInheritance: keepUserAccess);

            // 1) AppContainer: Vollzugriff (vererbend)
            sd.AddAccessRule(new FileSystemAccessRule(
                acSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            if (keepUserAccess)
            {
                // 2) Aktueller Benutzer: Vollzugriff (vererbend)
                var me = WindowsIdentity.GetCurrent().User;
                if (me != null)
                {
                    sd.AddAccessRule(new FileSystemAccessRule(
                        me,
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                }

                // 3) Optional: Admins + SYSTEM behalten
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                sd.AddAccessRule(new FileSystemAccessRule(
                    admins, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));

                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                sd.AddAccessRule(new FileSystemAccessRule(
                    system, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            }

            try
            {
                di.SetAccessControl(sd);
            }
            catch (Exception)
            {
                // ACL application may fail for protected directories — non-fatal
            }
        }

        public static bool TryEnsureDirectoryAcl(string dir, SecurityIdentifier acSid, bool keepUserAccess = true)
        {
            try
            {
                var di = new DirectoryInfo(dir);
                di.Create(); // Stellt sicher, dass der Ordner existiert

                // Bestehende ACL als Basis
                var sd = di.GetAccessControl();

                // Schutz aktivieren, aber bestehende Regeln optional behalten
                sd.SetAccessRuleProtection(isProtected: true, preserveInheritance: keepUserAccess);

                // 1) AppContainer: Vollzugriff (vererbend)
                sd.AddAccessRule(new FileSystemAccessRule(
                    acSid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));

                if (keepUserAccess)
                {
                    // 2) Aktueller Benutzer: Vollzugriff (vererbend)
                    var me = WindowsIdentity.GetCurrent().User;
                    if (me != null)
                    {
                        sd.AddAccessRule(new FileSystemAccessRule(
                            me,
                            FileSystemRights.FullControl,
                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                            PropagationFlags.None,
                            AccessControlType.Allow));
                    }

                    // 3) Admins + SYSTEM behalten
                    var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                    sd.AddAccessRule(new FileSystemAccessRule(
                        admins, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None, AccessControlType.Allow));

                    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                    sd.AddAccessRule(new FileSystemAccessRule(
                        system, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None, AccessControlType.Allow));
                }

                di.SetAccessControl(sd);
                return true; // Erfolg
            }
            catch (UnauthorizedAccessException ex)
            {
                // Hier den Fehler protokollieren oder dem Benutzer eine verständliche Nachricht geben
                Debug.WriteLine($"Fehler: Fehlende Berechtigungen für das Verzeichnis '{dir}'. Führen Sie die Anwendung als Administrator aus. Details: {ex.Message}");
                return false; // Misserfolg
            }
            catch (Exception ex)
            {
                // Andere mögliche Fehler abfangen
                Debug.WriteLine($"Ein unerwarteter Fehler ist aufgetreten: {ex.Message}");
                return false; // Misserfolg
            }
        }

        /// <summary>
        /// Kopiert die Child-Binaries (EXE-Verzeichnis rekursiv) in ein AC-lesbares Staging-Verzeichnis
        /// und setzt die DACL auf den AppContainer-SID. Liefert Pfad zur gestageten EXE zurück.
        /// </summary>
        public static string StageExecutableTree(string sourceExePath, string stagingRoot, SecurityIdentifier acSid)
        {
            var srcExeFull = Path.GetFullPath(sourceExePath);
            var srcDir = Path.GetDirectoryName(srcExeFull)!;

            // Root & Unterordner
            Directory.CreateDirectory(stagingRoot); // normal, mit Vererbung → Parent bleibt Owner

            var appDir = Path.Combine(stagingRoot, "app");
            var tempDir = Path.Combine(stagingRoot, "temp");

            // Falls von früher „verrammelt“: ACL resetten, dann löschen
            SafeDeleteTree(appDir);
            SafeDeleteTree(tempDir);

            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(tempDir);

            // Jetzt gezielt harte DACL nur auf app/ + temp/: AC + User
            EnsureDirectoryAcl(appDir, acSid, keepUserAccess: true);
            EnsureDirectoryAcl(tempDir, acSid, keepUserAccess: true);

            // Inhalte kopieren (rekursiv)
            foreach (var dir in Directory.EnumerateDirectories(srcDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(srcDir, dir);
                Directory.CreateDirectory(Path.Combine(appDir, rel));
            }
            foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(srcDir, file);
                var target = Path.Combine(appDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }

            return Path.Combine(appDir, Path.GetFileName(srcExeFull));
        }

        // versucht, problematische Altverzeichnisse zu entsperren & löschen
        private static void SafeDeleteTree(string path)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                // Vererbung wieder einschalten, damit der aktuelle User sicher Rechte hat
                var di = new DirectoryInfo(path);
                var sd = di.GetAccessControl();
                sd.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
                di.SetAccessControl(sd);
            }
            catch { /* best effort */ }

            try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
        }

    }

    internal static class AcEnv
    {
        public static IntPtr BuildUnicodeEnvWithTemp(string baseTemp, string? dotnetRoot = null, bool setBundleExtract = true, IDictionary<string, string>? extra = null)
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry de in Environment.GetEnvironmentVariables())
                env[(string)de.Key] = (string?)de.Value ?? string.Empty;

            env["TEMP"] = baseTemp;
            env["TMP"] = baseTemp;

            // Beibehaltung systemkritischer Vars
            var sysRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrEmpty(sysRoot)) env["SystemRoot"] = sysRoot;

            // Wenn du eine private Runtime beilegst (empfohlen bei AC)
            if (!string.IsNullOrEmpty(dotnetRoot))
            {
                env["DOTNET_ROOT"] = dotnetRoot!;
                env["DOTNET_ROOT(x86)"] = dotnetRoot!;
            }

            if (setBundleExtract)
                env["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = baseTemp;

            if (extra != null) foreach (var kv in extra) env[kv.Key] = kv.Value;

            // Unicode-Block "k=v\0... \0\0"
            var sb = new System.Text.StringBuilder();
            foreach (var kv in env.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (kv.Key.Contains('=')) continue;
                sb.Append(kv.Key).Append('=').Append(kv.Value ?? "").Append('\0');
            }
            sb.Append('\0');

            var bytes = System.Text.Encoding.Unicode.GetBytes(sb.ToString());
            var block = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, block, bytes.Length);
            return block;
        }
    }

    public static class AppContainerFolderAccess
    {
        /// <summary>
        /// Gewährt dem AppContainer (LowBox SID) Lese- oder Lese/Schreibrechte
        /// auf einen Ordner inkl. Vererbung auf Unterordner/Dateien.
        /// </summary>
        public static void GrantFolder(string folderPath, SecurityIdentifier appContainerSid, bool readWrite)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) throw new ArgumentNullException(nameof(folderPath));
            if (appContainerSid == null) throw new ArgumentNullException(nameof(appContainerSid));

            var di = new DirectoryInfo(folderPath);
            di.Create(); // idempotent

            // Bestehende ACL laden
            var sd = di.GetAccessControl(AccessControlSections.Access);

            var rights = readWrite
                ? (FileSystemRights.ReadAndExecute
                   | FileSystemRights.ListDirectory
                   | FileSystemRights.Read
                   | FileSystemRights.Write
                   | FileSystemRights.CreateFiles
                   | FileSystemRights.CreateDirectories
                   | FileSystemRights.Modify)
                : (FileSystemRights.ReadAndExecute
                   | FileSystemRights.ListDirectory
                   | FileSystemRights.Read);

            var rule = new FileSystemAccessRule(
                appContainerSid,
                rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow);

            bool modified;
            sd.ModifyAccessRule(AccessControlModification.Add, rule, out modified);

            // Optional (empfehlenswert): aktuellem Benutzer explizit Vollzugriff lassen
            var me = WindowsIdentity.GetCurrent().User;
            if (me != null)
            {
                var meRule = new FileSystemAccessRule(
                    me,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow);
                sd.ModifyAccessRule(AccessControlModification.Add, meRule, out _);
            }

            di.SetAccessControl(sd);
        }

        /// <summary>
        /// Entfernt sämtliche ACEs für den AppContainer-SID (Aufräumen).
        /// </summary>
        public static void RevokeFolder(string folderPath, SecurityIdentifier appContainerSid)
        {
            var di = new DirectoryInfo(folderPath);
            var sd = di.GetAccessControl(AccessControlSections.Access);

            var rules = sd.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule r in rules)
            {
                if (r.IdentityReference is SecurityIdentifier sid && sid == appContainerSid)
                {
                    sd.RemoveAccessRuleAll(r);
                }
            }

            di.SetAccessControl(sd);
        }
    }
}
