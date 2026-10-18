// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Threading;

namespace SolSatellite
{
    /// <summary>Mesh data a pool keeps: how much its lists hold, so a tile gets data that fits it.</summary>
    interface IPooledData
    {
        /// <summary>Bytes its lists hold (their capacity, not their contents).</summary>
        long HeldBytes { get; }
    }

    /// <summary>
    /// Mesh data that meshing a tile fills - lists of vertices, colours and triangles, megabytes
    /// a tile in a city - kept for the next tile once Unity has its copy, instead of becoming
    /// garbage: every garbage collection stops the game for as long as Mono takes to look
    /// through KSP's memory (gigabytes), and they come the sooner the more garbage there is.
    /// A city tile's data is kept too (until 2026-10-02 it wasn't: a central Tokyo tile grew
    /// its lists anew every time, a quarter of a gigabyte of garbage for one tile's buildings
    /// and 3D roads). A tile gets the smallest kept data that holds what it needs (else the
    /// biggest), so small tiles don't take city-sized data and cities don't grow small data.
    /// All pools together keep at most MeshPools.Budget; what no tile took for a minute is let
    /// go. Rented on worker threads, given back on the main thread.
    /// </summary>
    static class DataPool<T> where T : class, IPooledData, new()
    {
        const int Max = 16;

        struct Entry
        {
            public T Item;
            public long Bytes;
            public int Since; // Environment.TickCount when given back
        }

        static readonly List<Entry> free = new List<Entry>();

        static DataPool()
        {
            MeshPools.Register(Trim);
        }

        /// <summary>Kept data that holds `bytes` (the smallest such; else the biggest kept), or new.</summary>
        public static T Rent(long bytes = 0)
        {
            lock (free)
            {
                int best = -1;
                for (int i = 0; i < free.Count; i++)
                {
                    if (best < 0)
                    {
                        best = i;
                        continue;
                    }
                    long b = free[i].Bytes, current = free[best].Bytes;
                    bool fits = b >= bytes, currentFits = current >= bytes;
                    if (fits ? !currentFits || b < current : !currentFits && b > current)
                        best = i;
                }
                if (best >= 0)
                {
                    Entry e = free[best];
                    free[best] = free[free.Count - 1];
                    free.RemoveAt(free.Count - 1);
                    MeshPools.Release(e.Bytes);
                    return e.Item;
                }
            }
            return new T();
        }

        /// <summary>Emptied data back (its lists keep their capacity); let go if the pools are full.</summary>
        public static void Return(T item)
        {
            long bytes = item.HeldBytes;
            lock (free)
            {
                if (free.Count >= Max || !MeshPools.Hold(bytes))
                    return;
                free.Add(new Entry { Item = item, Bytes = bytes, Since = Environment.TickCount });
            }
        }

        /// <summary>Lets go of what no tile took for KeepMs.</summary>
        static void Trim()
        {
            int now = Environment.TickCount;
            lock (free)
            {
                for (int i = free.Count - 1; i >= 0; i--)
                {
                    if (unchecked(now - free[i].Since) < MeshPools.KeepMs)
                        continue;
                    MeshPools.Release(free[i].Bytes);
                    free[i] = free[free.Count - 1];
                    free.RemoveAt(free.Count - 1);
                }
            }
        }
    }

    static class MeshPools
    {
        /// <summary>All pools together keep at most this (bytes).</summary>
        public const long Budget = 1L << 30;
        /// <summary>Data no tile took for this long (ms) is let go.</summary>
        public const int KeepMs = 60000;

        static long held;
        static readonly List<Action> trims = new List<Action>();

        /// <summary>Bytes the pools keep now.</summary>
        public static long Held => Interlocked.Read(ref held);

        internal static void Register(Action trim)
        {
            lock (trims)
                trims.Add(trim);
        }

        /// <summary>Counts `bytes` as kept if that stays within the budget.</summary>
        internal static bool Hold(long bytes)
        {
            while (true)
            {
                long now = Interlocked.Read(ref held);
                if (now + bytes > Budget)
                    return false;
                if (Interlocked.CompareExchange(ref held, now + bytes, now) == now)
                    return true;
            }
        }

        internal static void Release(long bytes) => Interlocked.Add(ref held, -bytes);

        /// <summary>Lets go of data no tile took for a minute (main thread, now and then).</summary>
        public static void Trim()
        {
            lock (trims)
            {
                foreach (Action trim in trims)
                    trim();
            }
        }

        /// <summary>Bytes a list's capacity takes, `size` bytes an element.</summary>
        public static long Bytes<TE>(List<TE> list, int size) => (long)list.Capacity * size;

        /// <summary>A list made to hold `n` in one go, where it holds less: growing it one
        /// doubling at a time leaves every smaller array behind as garbage.</summary>
        public static void Reserve<TE>(List<TE> list, int n)
        {
            if (list.Capacity < n)
                list.Capacity = n;
        }
    }
}
