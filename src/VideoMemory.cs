// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SolSatellite
{
    /// <summary>
    /// The graphics card's memory as Windows accounts it for this process (DXGI 1.4,
    /// IDXGIAdapter3.QueryVideoMemoryInfo): the budget the system gives the game on the card,
    /// what it uses of it, and what has gone to main memory - textures and meshes the card then
    /// reads over the bus, many times slower. 2026-10-03 at Vnukovo: 7.8 GB on the card and
    /// 4.0 GB spilled to main memory, 6 frames a second, with the scripts taking 5 ms of the
    /// 150 ms frame. Queried once a second; nothing here changes anything.
    /// </summary>
    static class VideoMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Info
        {
            public ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr obj);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate uint ReleaseFn(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetDesc1Fn(IntPtr self, IntPtr desc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int QueryVideoMemoryInfoFn(IntPtr self, uint node, int group, out Info info);

        [DllImport("dxgi.dll")]
        static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        static readonly Guid FactoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");  // IDXGIFactory1
        static readonly Guid Adapter3Id = new Guid("645967A4-1392-4310-A798-8053CE3E93FD"); // IDXGIAdapter3
        const int DescBytes = 312;           // DXGI_ADAPTER_DESC1
        const int VendorAt = 256, DeviceAt = 260, DedicatedAt = 272, FlagsAt = 304;
        const uint SoftwareAdapter = 2;      // DXGI_ADAPTER_FLAG_SOFTWARE

        static IntPtr adapter;               // IDXGIAdapter3 of the card the game draws with
        static QueryVideoMemoryInfoFn query;
        static bool tried;
        static float nextQuery;

        /// <summary>Whether the numbers below are known (Windows 10 with DXGI 1.4, a D3D card).</summary>
        public static bool Available { get; private set; }
        /// <summary>Bytes: the game's budget on the card, what it uses of it, what is in main memory.</summary>
        public static long Budget { get; private set; }
        public static long Used { get; private set; }
        public static long Spilled { get; private set; }
        public static string Adapter { get; private set; }

        /// <summary>Spilled to main memory beyond what is usually there (upload buffers and the
        /// like): the card is short of memory and slows down.</summary>
        public static bool Short => Available && (Spilled > 768L << 20 || Budget > 0 && Used > Budget - (128L << 20));

        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextQuery)
                return;
            nextQuery = now + 1f;
            if (!tried)
            {
                tried = true;
                Open();
            }
            if (query == null)
                return;
            try
            {
                if (query(adapter, 0, 0, out Info local) != 0 || query(adapter, 0, 1, out Info shared) != 0)
                {
                    Available = false;
                    return;
                }
                Budget = (long)local.Budget;
                Used = (long)local.CurrentUsage;
                Spilled = (long)shared.CurrentUsage;
                Available = true;
            }
            catch (Exception e)
            {
                query = null;
                Available = false;
                Debug.LogWarning("[SolSatellite] Video memory query failed: " + e.Message);
            }
        }

        /// <summary>The adapter the game uses: the one with Unity's vendor and device ids, else
        /// the hardware one with the most memory of its own.</summary>
        static void Open()
        {
            IntPtr factory = IntPtr.Zero;
            IntPtr desc = Marshal.AllocHGlobal(DescBytes);
            try
            {
                if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor)
                    return;
                Guid fid = FactoryId;
                if (CreateDXGIFactory1(ref fid, out factory) != 0 || factory == IntPtr.Zero)
                    return;
                var enumAdapters = Method<EnumAdapters1Fn>(factory, 12);
                IntPtr best = IntPtr.Zero;
                long bestMemory = -1;
                string bestName = null;
                bool bestMatch = false;
                for (uint i = 0; i < 16; i++)
                {
                    if (enumAdapters(factory, i, out IntPtr a) != 0 || a == IntPtr.Zero)
                        break;
                    bool keep = false;
                    if (Method<GetDesc1Fn>(a, 10)(a, desc) == 0)
                    {
                        uint flags = (uint)Marshal.ReadInt32(desc, FlagsAt);
                        int vendor = Marshal.ReadInt32(desc, VendorAt), device = Marshal.ReadInt32(desc, DeviceAt);
                        long memory = Marshal.ReadInt64(desc, DedicatedAt);
                        bool match = vendor == SystemInfo.graphicsDeviceVendorID && device == SystemInfo.graphicsDeviceID;
                        if ((flags & SoftwareAdapter) == 0 && (match && !bestMatch || match == bestMatch && memory > bestMemory))
                        {
                            if (best != IntPtr.Zero)
                                Method<ReleaseFn>(best, 2)(best);
                            best = a;
                            bestMemory = memory;
                            bestMatch = match;
                            bestName = Marshal.PtrToStringUni(desc);
                            keep = true;
                        }
                    }
                    if (!keep)
                        Method<ReleaseFn>(a, 2)(a);
                }
                if (best == IntPtr.Zero)
                    return;
                Guid aid = Adapter3Id;
                int hr = Method<QueryInterfaceFn>(best, 0)(best, ref aid, out IntPtr a3);
                Method<ReleaseFn>(best, 2)(best);
                if (hr != 0 || a3 == IntPtr.Zero)
                    return;
                adapter = a3;
                query = Method<QueryVideoMemoryInfoFn>(a3, 14);
                Adapter = bestName;
                Debug.Log($"[SolSatellite] Video memory: watching '{bestName}' ({bestMemory / 1048576} MB of its own)");
            }
            catch (Exception e)
            {
                query = null;
                Debug.LogWarning("[SolSatellite] Video memory unavailable: " + e.Message);
            }
            finally
            {
                Marshal.FreeHGlobal(desc);
                if (factory != IntPtr.Zero)
                {
                    try
                    {
                        Method<ReleaseFn>(factory, 2)(factory);
                    }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>A COM method from the object's table.</summary>
        static T Method<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr table = Marshal.ReadIntPtr(obj);
            IntPtr fn = Marshal.ReadIntPtr(table, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(fn, typeof(T)) as T;
        }

        /// <summary>For the log and the window.</summary>
        public static string Summary =>
            Available
                ? $"{Used / 1073741824.0:0.0} GB of a {Budget / 1073741824.0:0.0} GB budget on the card, {Spilled / 1073741824.0:0.0} GB in main memory"
                : "not known";
    }
}
