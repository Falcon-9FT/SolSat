// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Parallax;
using UnityEngine;

namespace SolSatellite
{
    /// <summary>
    /// Trees, grass and rocks placed anew - a quad of the real relief built again in place, or the
    /// scatter mask changed under them - stay drawn as they were until Parallax's new placing is
    /// ready. Parallax places them on the GPU and learns how many it placed from an asynchronous
    /// readback a few frames later, drawing none on that quad meanwhile: every placing anew made
    /// the scatters there blink (2026-10-03, KerbalAvia's airport gradings coming in one by one
    /// near KSC). The old placing, taken out of Parallax's hands, is evaluated with the frame's
    /// other scatters - a Harmony prefix on ScatterRenderer.Render runs after Parallax has reset
    /// and filled its buffers and before it draws them - and cleaned up once the new one draws.
    /// Parallax's files are not touched.
    /// </summary>
    static class ScatterGhosts
    {
        const int MaxFrames = 90; // a placing that doesn't come ready lets the old one go anyway

        sealed class Ghost
        {
            public PQ Quad;
            public ScatterSystemQuadData Placed;   // a quad built again: everything it had, or ...
            public ScatterSystemQuadData Parent;   // ... one scatter placed anew: its quad's data,
            public ScatterData Old, New;           //     the old placing and the one replacing it
            public int Until;
        }

        static readonly List<Ghost> ghosts = new List<Ghost>();
        static AccessTools.FieldRef<ScatterData, bool> eventAdded; // set once the placed count came back
        static int evaluatedFrame = -1;
        static bool applied, failed;

        public static bool Active => applied && !failed;

        /// <summary>Placings anew bridged so far (for the log).</summary>
        public static int Bridged { get; private set; }

        public static int Count => ghosts.Count;

        public static void Apply()
        {
            if (applied)
                return;
            try
            {
                MethodInfo render = AccessTools.Method(typeof(ScatterRenderer), "Render");
                MethodInfo inCameras = AccessTools.Method(typeof(ScatterRenderer), "RenderInCameras");
                if (render == null || AccessTools.Field(typeof(ScatterData), "eventAdded") == null)
                {
                    Debug.LogWarning("[SolSatellite] Parallax's scatters aren't what the scatter bridging expects; scatters placed anew blink as before");
                    return;
                }
                eventAdded = AccessTools.FieldRefAccess<ScatterData, bool>("eventAdded");
                var harmony = new Harmony("SolSatellite.ScatterGhosts");
                var prefix = new HarmonyMethod(typeof(ScatterGhosts).GetMethod(nameof(BeforeRender), BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(render, prefix: prefix);
                if (inCameras != null)
                    harmony.Patch(inCameras, prefix: prefix); // what Parallax calls outside DirectX 11
                applied = true;
                Debug.Log("[SolSatellite] Parallax scatters placed anew stay drawn until the new placing is ready");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SolSatellite] Scatter bridging not applied: " + e.Message);
            }
        }

        /// <summary>A quad about to be built again: its scatters leave Parallax's hands (it would
        /// clean them up as the quad is hidden). Hand them to Keep once the quad is shown again.</summary>
        public static ScatterSystemQuadData Detach(PQ quad)
        {
            if (!Active || quad == null)
                return null;
            try
            {
                Dictionary<PQ, ScatterSystemQuadData> all = ScatterComponent.scatterQuadData;
                if (all == null || !all.TryGetValue(quad, out ScatterSystemQuadData data) || data == null)
                    return null;
                all.Remove(quad);
                for (int i = ghosts.Count - 1; i >= 0; i--)
                {
                    Ghost g = ghosts[i];
                    if (g.Parent == data)
                    {
                        // One of its scatters placed anew a moment ago: the old placing goes back
                        // in, to be drawn with the rest until the quad's new ones are ready.
                        int k = data.quadScatters.IndexOf(g.New);
                        if (k >= 0)
                        {
                            data.quadScatters[k] = g.Old;
                            g.New.Cleanup();
                        }
                        else
                        {
                            g.Old.Cleanup();
                        }
                        ghosts.RemoveAt(i);
                    }
                    else if (g.Placed != null && g.Quad == quad)
                    {
                        Retire(g); // built again twice in a moment: the oldest placing goes
                        ghosts.RemoveAt(i);
                    }
                }
                return data;
            }
            catch (Exception e)
            {
                Fail(e);
                return null;
            }
        }

        /// <summary>The quad built again and shown (Parallax placed its scatters anew): what it
        /// had is drawn until the new placing is ready.</summary>
        public static void Keep(PQ quad, ScatterSystemQuadData placed)
        {
            if (placed == null)
                return;
            if (!Active)
            {
                Cleanup(placed);
                return;
            }
            ghosts.Add(new Ghost { Quad = quad, Placed = placed, Until = Time.frameCount + MaxFrames });
            Bridged++;
        }

        /// <summary>One scatter of a quad placed anew (the mask changed under it): a new placing
        /// takes the old one's place in the quad's list and the old one is drawn until it's
        /// ready. False if that can't be done (placed again in place then, as before).</summary>
        public static bool PlaceAgain(ScatterSystemQuadData parent, ScatterData old)
        {
            if (!Active || parent?.quadScatters == null || old?.scatter == null)
                return false;
            int k = parent.quadScatters.IndexOf(old);
            if (k < 0)
                return false;
            var fresh = new ScatterData(parent, old.scatter) { readBlockMap = old.readBlockMap };
            parent.quadScatters[k] = fresh;
            fresh.Start();
            ghosts.Add(new Ghost { Quad = parent.quad, Parent = parent, Old = old, New = fresh, Until = Time.frameCount + MaxFrames });
            Bridged++;
            return true;
        }

        /// <summary>Every frame: placings that never come ready (or whose quad went) let go even
        /// when Parallax draws nothing.</summary>
        public static void Update()
        {
            if (ghosts.Count == 0)
                return;
            int frame = Time.frameCount;
            for (int i = ghosts.Count - 1; i >= 0; i--)
            {
                Ghost g = ghosts[i];
                if (frame > g.Until || g.Quad == null)
                {
                    Retire(g);
                    ghosts.RemoveAt(i);
                }
            }
        }

        /// <summary>Scene change: all of them go.</summary>
        public static void RetireAll()
        {
            foreach (Ghost g in ghosts)
                Retire(g);
            ghosts.Clear();
        }

        // ------------------------------------------------------------------ the Harmony prefix

        static void BeforeRender()
        {
            int frame = Time.frameCount;
            if (frame == evaluatedFrame)
                return; // once a frame, before the first renderer draws
            evaluatedFrame = frame;
            if (ghosts.Count == 0 || failed)
                return;
            try
            {
                for (int i = ghosts.Count - 1; i >= 0; i--)
                {
                    Ghost g = ghosts[i];
                    if (Done(g))
                    {
                        Retire(g);
                        ghosts.RemoveAt(i);
                        continue;
                    }
                    if (!g.Quad.isVisible)
                        continue;
                    // As ScatterManager does for its own, without pausing out-of-range ones
                    // (resuming would place them anew); the shader culls by range anyway.
                    if (g.Placed != null)
                    {
                        foreach (ScatterData d in g.Placed.quadScatters)
                            if (d != null && !d.cleaned)
                                d.Evaluate();
                    }
                    else if (!g.Old.cleaned)
                    {
                        g.Old.Evaluate();
                    }
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        /// <summary>The new placing draws (ScatterManager evaluated it this frame), or there is
        /// none to wait for.</summary>
        static bool Done(Ghost g)
        {
            if (g.Quad == null || Time.frameCount > g.Until)
                return true;
            Dictionary<PQ, ScatterSystemQuadData> all = ScatterComponent.scatterQuadData;
            if (all == null || !all.TryGetValue(g.Quad, out ScatterSystemQuadData now) || now == null)
                return true; // hidden or gone: Parallax draws nothing there now
            if (g.Placed != null)
            {
                foreach (ScatterData d in now.quadScatters)
                    if (!Ready(d))
                        return false;
                return true;
            }
            return now != g.Parent || !g.Parent.quadScatters.Contains(g.New) || Ready(g.New);
        }

        static bool Ready(ScatterData d) => d == null || d.cleaned || d.paused || eventAdded(d);

        static void Retire(Ghost g)
        {
            if (g.Placed != null)
                Cleanup(g.Placed);
            else
                Cleanup(g.Old);
        }

        static void Cleanup(ScatterSystemQuadData data)
        {
            try
            {
                data.Cleanup(); // its scatters' buffers and colliders
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        static void Cleanup(ScatterData data)
        {
            try
            {
                data?.Cleanup();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        static void Fail(Exception e)
        {
            if (failed)
                return;
            failed = true; // Parallax goes on as it would without us
            Debug.LogException(e);
            RetireAll();
        }
    }
}
