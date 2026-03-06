using System;
using System.IO;
using System.Security;
using Vestris.ResourceLib;

namespace Neo.App
{
    /// <summary>
    /// Embeds a .ico file into an existing .exe as Win32 resources (RT_GROUP_ICON / RT_ICON).
    /// After this, Explorer/Taskbar and WPF will use the icon automatically.
    /// </summary>
    public static class Win32IconInjector
    {
        /// <summary>
        /// Injects the given .ico into the specified .exe (replaces/sets icon group with ID = 1).
        /// </summary>
        /// <param name="exeAbsolutePath">Absolute path to the target .exe.</param>
        /// <param name="icoAbsolutePath">Absolute path to the .ico file (prefer multi-size, incl. 256px).</param>
        /// <exception cref="ArgumentException">If paths are missing or not absolute.</exception>
        /// <exception cref="FileNotFoundException">If files do not exist.</exception>
        /// <exception cref="IOException">If the file is locked or not writable.</exception>
        /// <exception cref="InvalidOperationException">If resource manipulation fails.</exception>
        public static void InjectIcon(string exeAbsolutePath, string icoAbsolutePath)
        {
            if (string.IsNullOrWhiteSpace(exeAbsolutePath))
                throw new ArgumentException("Target EXE path is empty.", nameof(exeAbsolutePath));
            if (string.IsNullOrWhiteSpace(icoAbsolutePath))
                throw new ArgumentException("ICO path is empty.", nameof(icoAbsolutePath));

            if (!Path.IsPathRooted(exeAbsolutePath))
                throw new ArgumentException("Target EXE path must be absolute.", nameof(exeAbsolutePath));
            if (!Path.IsPathRooted(icoAbsolutePath))
                throw new ArgumentException("ICO path must be absolute.", nameof(icoAbsolutePath));

            if (!File.Exists(exeAbsolutePath))
                throw new FileNotFoundException("Target EXE was not found.", exeAbsolutePath);
            if (!File.Exists(icoAbsolutePath))
                throw new FileNotFoundException("ICO file was not found.", icoAbsolutePath);

            // Quick writability/lock probe (exclusive open).
            FileStream? lockProbe = null;
            try
            {
                lockProbe = new FileStream(exeAbsolutePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (UnauthorizedAccessException e)
            {
                throw new IOException("Write access to the EXE is denied (admin rights required or file is read-only).", e);
            }
            catch (IOException e)
            {
                throw new IOException("The EXE appears to be locked (is the process running or a scanner holding a handle?).", e);
            }
            finally
            {
                lockProbe?.Dispose();
            }

            try
            {
                // Load .ico (must contain proper frames; prefer multi-size incl. 256px PNG-compressed).
                var iconFile = new IconFile(icoAbsolutePath);

                // Use deterministic group ID = 1, language neutral (0).
                var groupId = new ResourceId(1);
                const ushort lang = 0;

                // Remove existing group with the same ID (if any), to avoid duplicates/stale entries.
                try
                {
                    var existing = new IconDirectoryResource
                    {
                        Name = groupId,
                        Language = lang
                    };
                    existing.DeleteFrom(exeAbsolutePath);
                }
                catch
                {
                    // Ignore: group 1 may not exist yet.
                }

                // Create new group from .ico and save.
                var group = new IconDirectoryResource(iconFile)
                {
                    Name = groupId,
                    Language = lang
                };
                group.SaveTo(exeAbsolutePath);
            }
            catch (SecurityException e)
            {
                throw new InvalidOperationException("A security error occurred while writing resources.", e);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Failed to embed the icon into the EXE.", e);
            }
        }
    }
}
