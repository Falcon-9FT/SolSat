// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Parallax;
using UnityEngine;

namespace SolSatellite
{
    /// <summary>
    /// Works round a race in Parallax's CollisionManager (the colliders of trees and rocks) that
    /// crashes the game. RemoveQueuedData takes a quad's entry out of its bounds list by the
    /// data's list index before making sure the data is in the list at all. Data whose quad went
    /// away before the manager took it in (still in the incoming queue, index 0 by default)
    /// removes some other quad's bounds instead; the two lists drift apart, freed data is taken in
    /// afterwards, and a later removal past the end of the shorter list overruns memory (access
    /// violation in NativeList.RemoveAtSwapBack, 2026-10-02). Quads rebuilt in place (the real
    /// terrain) make that window common. A Harmony prefix runs right before the removal: data
    /// not in the list leaves both queues and is freed here, entries whose data was freed leave
    /// the list, and the bounds list is rebuilt from the list if it drifted anyway. Parallax's
    /// files are not touched.
    /// </summary>
    static class ParallaxCollisionFix
    {
        static FieldInfo incomingField, outgoingField, boundsField;
        static readonly List<ScatterColliderData> keep = new List<ScatterColliderData>();
        static readonly HashSet<ScatterColliderData> strays = new HashSet<ScatterColliderData>();
        static bool applied, failed;

        /// <summary>Times something was set right (strays freed, entries dropped, bounds rebuilt).</summary>
        public static int Repairs { get; private set; }
        public static bool Active => applied && !failed;

        public static void Apply()
        {
            if (applied)
                return;
            try
            {
                Type manager = typeof(Parallax.CollisionManager); // (KSP has a CollisionManager of its own)
                incomingField = AccessTools.Field(manager, "incomingData");
                outgoingField = AccessTools.Field(manager, "outgoingData");
                boundsField = AccessTools.Field(manager, "sqrQuadBounds");
                MethodInfo original = AccessTools.Method(manager, "RemoveQueuedData");
                if (incomingField == null || outgoingField == null || boundsField == null || original == null)
                {
                    Debug.LogWarning("[SolSatellite] Parallax's CollisionManager isn't what the collision fix expects; not patched");
                    return;
                }
                new Harmony("SolSatellite.ParallaxCollisionFix").Patch(original,
                    prefix: new HarmonyMethod(typeof(ParallaxCollisionFix).GetMethod(nameof(BeforeRemove), BindingFlags.Static | BindingFlags.NonPublic)));
                applied = true;
                Debug.Log("[SolSatellite] Parallax scatter collision queue fix applied");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SolSatellite] Parallax collision fix not applied: " + e.Message);
            }
        }

        static void BeforeRemove()
        {
            if (failed)
                return;
            try
            {
                Sanitize();
            }
            catch (Exception e)
            {
                failed = true; // Parallax runs as it would without us
                Debug.LogException(e);
            }
        }

        /// <summary>Runs where Parallax is about to take outgoing data out (no collider jobs run then).</summary>
        static void Sanitize()
        {
            var incoming = incomingField.GetValue(null) as ConcurrentQueue<ScatterColliderData>;
            var outgoing = outgoingField.GetValue(null) as ConcurrentQueue<ScatterColliderData>;
            FastList<ScatterColliderData> list = Parallax.CollisionManager.collisionData;
            if (incoming == null || outgoing == null || list == null)
                return;

            // Outgoing data that isn't in the list: never taken in (or already gone). Out of both
            // queues, freed here, so Parallax neither removes some other entry for it nor takes
            // it in freed afterwards.
            if (!outgoing.IsEmpty)
            {
                keep.Clear();
                strays.Clear();
                while (outgoing.TryDequeue(out ScatterColliderData d))
                {
                    if (d == null)
                        continue;
                    if (!d.disposed && d.id >= 0 && d.id < list.Length && list[d.id] == d)
                        keep.Add(d);
                    else
                        strays.Add(d);
                }
                foreach (ScatterColliderData d in keep)
                    outgoing.Enqueue(d);
                if (strays.Count > 0)
                {
                    keep.Clear();
                    while (incoming.TryDequeue(out ScatterColliderData d))
                        if (d != null && !strays.Contains(d))
                            keep.Add(d);
                    foreach (ScatterColliderData d in keep)
                        incoming.Enqueue(d);
                    foreach (ScatterColliderData d in strays)
                    {
                        d.initDistancesHandle.Complete();
                        d.DisposeNativeArrays();
                    }
                    Repairs += strays.Count;
                }
                keep.Clear();
                strays.Clear();
            }

            // Entries whose data was freed (taken in after it went away, before this fix ran).
            bool changed = false;
            for (int i = list.Length - 1; i >= 0; i--)
            {
                ScatterColliderData d = list[i];
                if (d != null && !d.disposed)
                    continue;
                list.Remove(i); // the last entry moves here, with its index
                changed = true;
                Repairs++;
            }

            // The bounds list must match the list entry for entry.
            object bounds = boundsField.GetValue(null); // NativeList<float>, boxed: it shares the native buffer
            if (bounds == null)
                return;
            Type type = bounds.GetType();
            int length = (int)type.GetProperty("Length").GetValue(bounds, null);
            if (!changed && length == list.Length)
                return;
            MethodInfo clear = type.GetMethod("Clear", Type.EmptyTypes);
            MethodInfo add = null;
            foreach (MethodInfo m in type.GetMethods())
                if (m.Name == "Add" && m.GetParameters().Length == 1)
                    add = m;
            if (clear == null || add == null)
                return;
            clear.Invoke(bounds, null);
            var args = new object[1];
            for (int i = 0; i < list.Length; i++)
            {
                args[0] = list[i].scatterSystemQuad != null ? list[i].scatterSystemQuad.sqrQuadWidth : 0f;
                add.Invoke(bounds, args);
            }
            Repairs++;
            Debug.LogWarning($"[SolSatellite] Parallax scatter collision lists were out of step ({length} bounds for {list.Length} quads): rebuilt");
        }
    }
}
