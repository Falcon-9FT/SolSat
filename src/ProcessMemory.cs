// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Runtime.InteropServices;

namespace SolSatellite
{
    /// <summary>The game's own memory use, from Windows: KSP's Mono reports 0 for
    /// Process.PrivateMemorySize64.</summary>
    static class ProcessMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Counters // PROCESS_MEMORY_COUNTERS_EX
        {
            public uint Size;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
        static extern bool GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint size);

        static bool failed;

        /// <summary>Private bytes (memory committed for the game alone), or 0 if Windows won't say.</summary>
        public static long Private()
        {
            if (failed)
                return 0;
            try
            {
                var c = new Counters { Size = (uint)Marshal.SizeOf(typeof(Counters)) };
                if (GetProcessMemoryInfo(GetCurrentProcess(), ref c, c.Size))
                    return (long)c.PrivateUsage.ToUInt64();
            }
            catch (Exception)
            {
                failed = true; // not Windows, or no such function: don't try every time
            }
            return 0;
        }
    }
}
