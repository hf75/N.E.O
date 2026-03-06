using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Neo.Shared
{
    public static class PeUtils
    {
        // Schnelltest: besitzt die PE einen CLR-Header?
        public static bool IsManagedAssembly(byte[] peBytes)
        {
            try
            {
                using var ms = new MemoryStream(peBytes, writable: false);
                using var br = new BinaryReader(ms);

                if (ms.Length < 0x40) return false;
                ms.Position = 0x3C;
                int lfanew = br.ReadInt32();
                if (lfanew <= 0 || lfanew + 0x18 > ms.Length) return false;

                ms.Position = lfanew;
                uint sig = br.ReadUInt32(); // "PE\0\0" = 0x00004550
                if (sig != 0x00004550) return false;

                ms.Position = lfanew + 0x18;
                ushort magic = br.ReadUInt16();
                bool isPE32Plus = (magic == 0x20b);

                int dataDirOffsetFromOpt = isPE32Plus ? 0x70 : 0x60;
                ms.Position = lfanew + 0x18 + dataDirOffsetFromOpt;

                // DataDirectory[14] = CLR Runtime Header
                ms.Position += 14 * 8;
                uint clrRva = br.ReadUInt32();
                uint clrSize = br.ReadUInt32();

                return clrRva != 0 && clrSize != 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PeUtils.IsManagedAssembly] {ex.Message}");
                return false;
            }
        }

        // Ermittelt AssemblyName (inkl. FullName) ohne den Default-ALC zu verschmutzen.
        public static AssemblyName? TryGetAssemblyName(byte[] peBytes)
        {
            TransientAlc? alc = null;
            try
            {
                alc = new TransientAlc();

                using var ms = new MemoryStream(peBytes, writable: false);
                var asm = alc.LoadFromStream(ms);

                return asm?.GetName();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PeUtils.TryGetAssemblyName] {ex.Message}");
                return null;
            }
            finally
            {
                if (alc != null)
                {
                    alc.Unload();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
            }
        }

        private sealed class TransientAlc : AssemblyLoadContext
        {
            public TransientAlc() : base(isCollectible: true) { }
            protected override Assembly? Load(AssemblyName assemblyName) => null;
        }
    }
}
