using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Neo.Agents
{
    /// <summary>
    /// Starts a program that reads its input from a file and writes its output into files, and that keeps running
    /// when the program that started it ends: no pipe of ours ties it to us. Any program can follow it by its files.
    /// </summary>
    internal static class DetachedProcess
    {
        internal sealed record Started(int ProcessId, DateTime StartTimeUtc);

        public static Started Start(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment, string inputFile, string outputFile, string errorFile)
        {
            return OperatingSystem.IsWindows()
                ? StartOnWindows(fileName, arguments, workingDirectory, environment, inputFile, outputFile, errorFile)
                : StartWithShell(fileName, arguments, workingDirectory, environment, inputFile, outputFile, errorFile);
        }

        /// <summary>Whether the process that was started then is still running (a process id can come back for another one).</summary>
        public static bool IsRunning(int processId, DateTime startTimeUtc)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited
                       && Math.Abs((process.StartTime.ToUniversalTime() - startTimeUtc).TotalSeconds) < 2;
            }
            catch (ArgumentException) { return false; }          // no such process
            catch (InvalidOperationException) { return false; }  // ended meanwhile
            catch (Win32Exception) { return false; }             // not ours to look at
        }

        /// <summary>Ends the process and everything it started, if it is still the one started then.</summary>
        public static void Stop(int processId, DateTime startTimeUtc)
        {
            if (!IsRunning(processId, startTimeUtc))
                return;
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }

        // ── Unix: a shell connects the files and then becomes the program ──────────────────────────

        private static Started StartWithShell(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment, string inputFile, string outputFile, string errorFile)
        {
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = workingDirectory };
            // The arguments reach the program as they are: they are the shell's own positional parameters.
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("i=\"$NEO_DETACHED_IN\"; o=\"$NEO_DETACHED_OUT\"; e=\"$NEO_DETACHED_ERR\"; "
                                   + "unset NEO_DETACHED_IN NEO_DETACHED_OUT NEO_DETACHED_ERR; "
                                   + "exec \"$0\" \"$@\" <\"$i\" >\"$o\" 2>\"$e\"");
            start.ArgumentList.Add(fileName);
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);

            start.Environment.Clear();
            foreach (var (name, value) in environment)
                start.Environment[name] = value;
            start.Environment["NEO_DETACHED_IN"] = inputFile;
            start.Environment["NEO_DETACHED_OUT"] = outputFile;
            start.Environment["NEO_DETACHED_ERR"] = errorFile;

            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start.");
            return new(process.Id, process.StartTime.ToUniversalTime());
        }

        // ── Windows: CreateProcess with the three files as standard handles ────────────────────────

        [SupportedOSPlatform("windows")]
        private static Started StartOnWindows(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment, string inputFile, string outputFile, string errorFile)
        {
            using var input = File.OpenHandle(inputFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = File.OpenHandle(outputFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var error = File.OpenHandle(errorFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

            SafeFileHandle[] handles = [input, output, error];
            foreach (var handle in handles)
            {
                if (!SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "A file for Claude Code could not be handed on.");
            }

            var handleList = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
            var attributeList = IntPtr.Zero;
            var environmentBlock = Marshal.StringToHGlobalUni(EnvironmentBlock(environment));
            try
            {
                for (var i = 0; i < handles.Length; i++)
                    Marshal.WriteIntPtr(handleList, i * IntPtr.Size, handles[i].DangerousGetHandle());

                // Only these three handles go to the new process, not whatever else of ours happens to be inheritable.
                var size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attributeList = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                try
                {
                    if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeHandleList, handleList,
                            (IntPtr)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());

                    var startup = new STARTUPINFOEX();
                    startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                    startup.StartupInfo.dwFlags = StartfUseStdHandles;
                    startup.StartupInfo.hStdInput = input.DangerousGetHandle();
                    startup.StartupInfo.hStdOutput = output.DangerousGetHandle();
                    startup.StartupInfo.hStdError = error.DangerousGetHandle();
                    startup.lpAttributeList = attributeList;

                    var commandLine = WindowsCommandLine.Join(fileName, arguments);
                    const uint flags = ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateNoWindow | CreateNewProcessGroup;

                    // Out of a job object that would end it together with the program that started it, where the job lets it go.
                    if (!Create(flags | CreateBreakawayFromJob, out var info))
                    {
                        var failure = Marshal.GetLastWin32Error();
                        if (failure != ErrorAccessDenied || !Create(flags, out info))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), $"{fileName} could not be started.");
                    }

                    try
                    {
                        if (!GetProcessTimes(info.hProcess, out var created, out _, out _, out _))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        return new(info.dwProcessId, DateTime.FromFileTimeUtc(created));
                    }
                    finally
                    {
                        CloseHandle(info.hThread);
                        CloseHandle(info.hProcess);
                    }

                    bool Create(uint creationFlags, out PROCESS_INFORMATION processInformation)
                        => CreateProcessW(Path.IsPathRooted(fileName) ? fileName : null, new StringBuilder(commandLine),
                            IntPtr.Zero, IntPtr.Zero, true, creationFlags, environmentBlock, workingDirectory,
                            ref startup, out processInformation);
                }
                finally
                {
                    DeleteProcThreadAttributeList(attributeList);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(environmentBlock);
                if (attributeList != IntPtr.Zero)
                    Marshal.FreeHGlobal(attributeList);
                Marshal.FreeHGlobal(handleList);
            }
        }

        // name=value pairs, each ended by a null, the block ended by one more. Sorted, as Windows keeps it.
        private static string EnvironmentBlock(IReadOnlyDictionary<string, string> environment)
        {
            var block = new StringBuilder();
            foreach (var (name, value) in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (name.Length == 0 || name.Contains('=') || name.Contains('\0') || value.Contains('\0'))
                    continue;
                block.Append(name).Append('=').Append(value).Append('\0');
            }
            return block.Append('\0').ToString();
        }

        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint CreateNoWindow = 0x08000000;
        private const uint CreateNewProcessGroup = 0x00000200;
        private const uint CreateBreakawayFromJob = 0x01000000;
        private const int StartfUseStdHandles = 0x00000100;
        private const uint HandleFlagInherit = 0x00000001;
        private static readonly IntPtr ProcThreadAttributeHandleList = (IntPtr)0x00020002;
        private const int ErrorAccessDenied = 5;

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
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
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(SafeFileHandle hObject, uint dwMask, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
            IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime,
            out long lpKernelTime, out long lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }

    /// <summary>A command line that hands every argument on as it is (the rules of CommandLineToArgvW and the C runtime).</summary>
    internal static class WindowsCommandLine
    {
        public static string Join(string fileName, IEnumerable<string> arguments)
        {
            var line = new StringBuilder();
            Append(line, fileName);
            foreach (var argument in arguments)
            {
                line.Append(' ');
                Append(line, argument);
            }
            return line.ToString();
        }

        // Quotes around anything empty or with white space or a quote; inside them a quote gets a backslash,
        // and backslashes before a quote, or before the closing one, are doubled.
        private static void Append(StringBuilder line, string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            {
                line.Append(argument);
                return;
            }

            line.Append('"');
            for (var i = 0; ; i++)
            {
                var backslashes = 0;
                while (i < argument.Length && argument[i] == '\\')
                {
                    i++;
                    backslashes++;
                }

                if (i == argument.Length)
                {
                    line.Append('\\', backslashes * 2);
                    break;
                }

                if (argument[i] == '"')
                {
                    line.Append('\\', backslashes * 2 + 1);
                    line.Append('"');
                }
                else
                {
                    line.Append('\\', backslashes);
                    line.Append(argument[i]);
                }
            }
            line.Append('"');
        }
    }
}
