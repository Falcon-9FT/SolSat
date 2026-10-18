// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.Profiling;
using UnityEngine.Scripting;
using Debug = UnityEngine.Debug;

namespace SolSatellite
{
    /// <summary>
    /// Where the whole frame goes, not just the mod's part (Perf): markers put into Unity's
    /// player loop time physics (FixedUpdate, every step of it), all scripts' Update and
    /// LateUpdate, the frame's rendering (cameras, up to FinishFrameRendering) and the wait for
    /// the graphics card to take the previous frame (PresentAfterDraw: long when the card is
    /// what holds the game up). Garbage collections are counted with the frames they stopped,
    /// over the last two seconds and the last minute (how often, how long such a frame was).
    /// Averages and worst frames over the last two seconds, like Perf; the settings window's
    /// "Нагрузка" tab shows them and the log gets them every 30 seconds.
    ///
    /// Why collections hurt (checked 2026-10-02 in Unity's Mono and bdwgc sources at the
    /// commit KSP 1.12.5 runs): KSP's Mono uses the Boehm collector in its incremental mode
    /// (boot.config gc-max-time-slice=3: marking spread over frames, 3 ms a step), but every
    /// collection still ends with the whole game stopped while one thread marks again what
    /// changed meanwhile (pages written since it began, all thread stacks), with no time limit
    /// after the first 3 ms attempt; with KSP's 3 GB heap that took ~100-200 ms. A collection
    /// begins each time the game has allocated a share of what it holds (hundreds of MB for
    /// KSP), so the less garbage, the rarer these stops. Boehm here marks on one thread only
    /// (no GC_MARKERS).
    /// </summary>
    static class FrameProfile
    {
        public enum Phase { Physics, Update, LateUpdate, Render, Present, Rest }
        public const int Count = 6;

        public static readonly string[] Names =
        {
            "Физика KSP (FixedUpdate)", "Скрипты: Update", "Скрипты: LateUpdate (здесь и мод)", "Рендер кадра (камеры)",
            "Ожидание видеокарты", "Прочее",
        };

        static readonly string[] LogNames = { "physics", "update", "late update", "render", "gpu wait", "other" };

        /// <summary>The type our player loop entries carry, to find them again.</summary>
        sealed class SolSatelliteFrameMarker
        {
        }

        const float Window = 2f;
        static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        static bool installed;
        static long frameStart, fixedStart, updateStart, lateStart, renderStart, presentStart;
        static readonly double[] frame = new double[Count], sum = new double[Count], worst = new double[Count];
        static double totalSum, totalWorst, stepsSum, gcWorstFrame;
        static int frames, steps, gcLast = -1, gcInWindow;
        static float windowStart;
        static readonly FrameTiming[] timings = new FrameTiming[1];
        // Allocation (all threads): how much the managed heap in use grew from frame to frame,
        // frames with a collection left out. Boehm takes a new 4 KB block once the free space
        // swept in partly used ones runs out, so this is a little less than what was allocated.
        static long usedLast = -1;
        static double allocSum, allocMs, allocMinute, allocMinuteMs;
        static int timingMisses;
        static double gpuSum;
        static int gpuFrames;
        // The last collections: when, and how long the frame they stopped was.
        const int GcKept = 64;
        const float GcMinute = 60f;
        static readonly float[] gcAt = new float[GcKept];
        static readonly double[] gcFrame = new double[GcKept];
        static int gcNext, gcCount;

        /// <summary>Per phase over the last window: average ms per frame and the worst frame.</summary>
        public static readonly double[] Avg = new double[Count], Max = new double[Count];
        public static double FrameAvg, FrameMax, StepsAvg, GcWorstFrame, GpuAvg = -1.0;
        /// <summary>MB a second the game allocates (managed, all threads), over the last window and about a minute.</summary>
        public static double AllocRate, AllocRateMinute;
        public static int Collections;
        /// <summary>Over the last minute: collections, seconds between them, their frames (ms).</summary>
        public static int MinuteCollections;
        public static double MinuteInterval, MinuteGcFrameAvg, MinuteGcFrameMax;
        public static bool Active => installed;

        // Unity's own parts of the player loop (PreUpdate.SendMouseEvents, Update.ScriptRunBehaviourUpdate,
        // Update.ScriptRunDelayedDynamicFrameRate = coroutines...), each timed: what the mod profile can't put on
        // any script (2026-10-17: 12.8 ms of a 47 ms frame "elsewhere"). Summed since the start (ms); the mod
        // profile takes the difference over its recording.
        static readonly List<string> partNames = new List<string>();
        static double[] partMs = new double[0];
        static int partOpen = -1;
        static long partStart;
        public static int PartCount => partNames.Count;
        public static string PartName(int i) => partNames[i];
        public static void CopyParts(double[] to) => Array.Copy(partMs, to, Math.Min(partMs.Length, to.Length));

        public static void Install()
        {
            if (installed)
                return;
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                PlayerLoopSystem[] phases = root.subSystemList;
                if (phases == null)
                    return;
                int found = 0;
                for (int i = 0; i < phases.Length; i++)
                {
                    Type t = phases[i].type;
                    PlayerLoopSystem[] subs = phases[i].subSystemList ?? new PlayerLoopSystem[0];
                    if (t != typeof(UnityEngine.PlayerLoop.Initialization))
                        subs = Parts(subs, t.Name);
                    if (t == typeof(UnityEngine.PlayerLoop.Initialization))
                        phases[i].subSystemList = Wrap(subs, OnFrameStart, null);
                    else if (t == typeof(UnityEngine.PlayerLoop.FixedUpdate))
                        phases[i].subSystemList = Wrap(subs, OnFixedStart, OnFixedEnd);
                    else if (t == typeof(UnityEngine.PlayerLoop.PreUpdate))
                        phases[i].subSystemList = Wrap(subs, OnUpdateStart, null);
                    else if (t == typeof(UnityEngine.PlayerLoop.Update))
                        phases[i].subSystemList = Wrap(subs, null, OnUpdateEnd);
                    else if (t == typeof(UnityEngine.PlayerLoop.PreLateUpdate))
                        phases[i].subSystemList = Wrap(subs, OnLateStart, OnLateEnd);
                    else if (t == typeof(UnityEngine.PlayerLoop.PostLateUpdate))
                        phases[i].subSystemList = WrapPostLate(subs);
                    else
                        continue;
                    found++;
                }
                if (found < 6)
                {
                    Debug.LogWarning($"[SolSatellite] Frame profile: Unity's player loop isn't as expected ({found} of 6 phases); not timed");
                    return;
                }
                root.subSystemList = phases;
                PlayerLoop.SetPlayerLoop(root);
                installed = true;
                string divisor = Environment.GetEnvironmentVariable("GC_FREE_SPACE_DIVISOR");
                Debug.Log($"[SolSatellite] Frame profile on; incremental GC {(GarbageCollector.isIncremental ? "on" : "off")}, " +
                          $"GC free space divisor {(string.IsNullOrEmpty(divisor) ? "3 (default)" : divisor + " (environment)")}, " +
                          $"{Environment.ProcessorCount} logical processors");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SolSatellite] Frame profile unavailable: " + e.Message);
            }
        }

        public static void Uninstall()
        {
            if (!installed)
                return;
            installed = false;
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                PlayerLoopSystem[] phases = root.subSystemList;
                if (phases == null)
                    return;
                for (int i = 0; i < phases.Length; i++)
                {
                    if (phases[i].subSystemList == null)
                        continue;
                    var kept = new List<PlayerLoopSystem>(phases[i].subSystemList.Length);
                    foreach (PlayerLoopSystem s in phases[i].subSystemList)
                        if (s.type != typeof(SolSatelliteFrameMarker))
                            kept.Add(s);
                    phases[i].subSystemList = kept.ToArray();
                }
                root.subSystemList = phases;
                PlayerLoop.SetPlayerLoop(root);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SolSatellite] Frame profile: " + e.Message);
            }
        }

        static PlayerLoopSystem Marker(PlayerLoopSystem.UpdateFunction f) =>
            new PlayerLoopSystem { type = typeof(SolSatelliteFrameMarker), updateDelegate = f };

        /// <summary>Each of Unity's systems of a phase timed as a part of its own: a marker before each, one after the last.</summary>
        static PlayerLoopSystem[] Parts(PlayerLoopSystem[] subs, string phase)
        {
            var list = new List<PlayerLoopSystem>(subs.Length * 2 + 1);
            foreach (PlayerLoopSystem s in subs)
            {
                if (s.type == typeof(SolSatelliteFrameMarker))
                {
                    list.Add(s);
                    continue;
                }
                int index = partNames.Count;
                partNames.Add(phase + "." + (s.type != null ? s.type.Name : "?"));
                list.Add(Marker(() => PartBegin(index)));
                list.Add(s);
            }
            list.Add(Marker(() => PartBegin(-1)));
            if (partMs.Length < partNames.Count)
                Array.Resize(ref partMs, partNames.Count);
            return list.ToArray();
        }

        static void PartBegin(int index)
        {
            long now = Stopwatch.GetTimestamp();
            if (partOpen >= 0)
                partMs[partOpen] += Ms(now - partStart);
            partOpen = index;
            partStart = now;
        }

        static PlayerLoopSystem[] Wrap(PlayerLoopSystem[] subs, PlayerLoopSystem.UpdateFunction first, PlayerLoopSystem.UpdateFunction last)
        {
            var list = new List<PlayerLoopSystem>(subs.Length + 2);
            if (first != null)
                list.Add(Marker(first));
            list.AddRange(subs);
            if (last != null)
                list.Add(Marker(last));
            return list.ToArray();
        }

        /// <summary>Rendering from the start of PostLateUpdate to the end of FinishFrameRendering
        /// (cameras, image effects, the GUI); the wait for the card around PresentAfterDraw.</summary>
        static PlayerLoopSystem[] WrapPostLate(PlayerLoopSystem[] subs)
        {
            var list = new List<PlayerLoopSystem>(subs.Length + 4) { Marker(OnRenderStart) };
            bool rendered = false;
            foreach (PlayerLoopSystem s in subs)
            {
                if (s.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate.PresentAfterDraw))
                {
                    list.Add(Marker(OnPresentStart));
                    list.Add(s);
                    list.Add(Marker(OnPresentEnd));
                    continue;
                }
                list.Add(s);
                if (s.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate.FinishFrameRendering))
                {
                    list.Add(Marker(OnRenderEnd));
                    rendered = true;
                }
            }
            if (!rendered)
                list.Add(Marker(OnRenderEnd));
            return list.ToArray();
        }

        static double Ms(long ticks) => ticks * MsPerTick;

        static void OnFixedStart()
        {
            ModProfiler.Boundary();
            fixedStart = Stopwatch.GetTimestamp();
        }

        static void OnFixedEnd()
        {
            frame[(int)Phase.Physics] += Ms(Stopwatch.GetTimestamp() - fixedStart);
            steps++;
        }

        static void OnUpdateStart()
        {
            ModProfiler.Boundary();
            updateStart = Stopwatch.GetTimestamp();
        }

        static void OnUpdateEnd() => frame[(int)Phase.Update] += Ms(Stopwatch.GetTimestamp() - updateStart);

        static void OnLateStart()
        {
            ModProfiler.Boundary();
            lateStart = Stopwatch.GetTimestamp();
        }

        static void OnLateEnd() => frame[(int)Phase.LateUpdate] += Ms(Stopwatch.GetTimestamp() - lateStart);

        static void OnRenderStart()
        {
            ModProfiler.Boundary();
            renderStart = Stopwatch.GetTimestamp();
        }

        static void OnRenderEnd() => frame[(int)Phase.Render] += Ms(Stopwatch.GetTimestamp() - renderStart);

        static void OnPresentStart() => presentStart = Stopwatch.GetTimestamp();

        static void OnPresentEnd() => frame[(int)Phase.Present] += Ms(Stopwatch.GetTimestamp() - presentStart);

        /// <summary>A new frame: the one before it is closed.</summary>
        static void OnFrameStart()
        {
            long now = Stopwatch.GetTimestamp();
            if (frameStart != 0)
            {
                double total = Ms(now - frameStart);
                double known = 0.0;
                for (int i = 0; i < (int)Phase.Rest; i++)
                    known += frame[i];
                frame[(int)Phase.Rest] = Math.Max(0.0, total - known);
                for (int i = 0; i < Count; i++)
                {
                    sum[i] += frame[i];
                    if (frame[i] > worst[i])
                        worst[i] = frame[i];
                }
                totalSum += total;
                if (total > totalWorst)
                    totalWorst = total;
                ModProfiler.FrameClosed(frame, total);
                stepsSum += steps;
                frames++;
                // A collection since the last frame began stopped that frame.
                int gc = GC.CollectionCount(0);
                long used = GC.GetTotalMemory(false);
                if (usedLast >= 0 && gc == gcLast)
                {
                    allocSum += Math.Max(0L, used - usedLast);
                    allocMs += total;
                }
                usedLast = used;
                if (gcLast >= 0 && gc != gcLast)
                {
                    gcInWindow += gc - gcLast;
                    gcWorstFrame = Math.Max(gcWorstFrame, total);
                    gcAt[gcNext] = Time.realtimeSinceStartup;
                    gcFrame[gcNext] = total;
                    gcNext = (gcNext + 1) % GcKept;
                    gcCount = Math.Min(gcCount + 1, GcKept);
                }
                gcLast = gc;
            }
            frameStart = now;
            Array.Clear(frame, 0, Count);
            steps = 0;
            SampleGpu();

            float t = Time.realtimeSinceStartup;
            if (t - windowStart < Window || frames == 0)
                return;
            for (int i = 0; i < Count; i++)
            {
                Avg[i] = sum[i] / frames;
                Max[i] = worst[i];
                sum[i] = worst[i] = 0.0;
            }
            FrameAvg = totalSum / frames;
            FrameMax = totalWorst;
            StepsAvg = stepsSum / frames;
            Collections = gcInWindow;
            GcWorstFrame = gcWorstFrame;
            GpuAvg = gpuFrames > 0 ? gpuSum / gpuFrames : -1.0;
            AllocRate = allocMs > 0.0 ? allocSum / 1048576.0 / (allocMs / 1000.0) : 0.0;
            // About the last minute: the windows' sums fade (a window is 2 s, 1/30 of a minute).
            allocMinute = allocMinute * (29.0 / 30.0) + allocSum;
            allocMinuteMs = allocMinuteMs * (29.0 / 30.0) + allocMs;
            AllocRateMinute = allocMinuteMs > 0.0 ? allocMinute / 1048576.0 / (allocMinuteMs / 1000.0) : 0.0;
            allocSum = allocMs = 0.0;
            MinuteStats(t);
            totalSum = totalWorst = stepsSum = gcWorstFrame = gpuSum = 0.0;
            frames = gcInWindow = gpuFrames = 0;
            windowStart = t;
        }

        /// <summary>Collections of the last minute: how many, how far apart, how long their frames were.</summary>
        static void MinuteStats(float now)
        {
            int n = 0;
            double sum = 0.0, max = 0.0;
            float first = float.MaxValue, last = float.MinValue;
            for (int i = 0; i < gcCount; i++)
            {
                float at = gcAt[i];
                if (now - at > GcMinute)
                    continue;
                n++;
                sum += gcFrame[i];
                max = Math.Max(max, gcFrame[i]);
                first = Math.Min(first, at);
                last = Math.Max(last, at);
            }
            MinuteCollections = n;
            MinuteGcFrameAvg = n > 0 ? sum / n : 0.0;
            MinuteGcFrameMax = max;
            MinuteInterval = n > 1 ? (last - first) / (n - 1) : 0.0;
        }

        /// <summary>The card's own time for a frame, where Unity measures it (needs frame timing
        /// stats in the player settings; tried for a while, then left alone).</summary>
        static void SampleGpu()
        {
            if (timingMisses > 600)
                return;
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timings) > 0 && timings[0].gpuFrameTime > 0.0)
                {
                    gpuSum += timings[0].gpuFrameTime;
                    gpuFrames++;
                    timingMisses = 0;
                    return;
                }
            }
            catch (Exception)
            {
                timingMisses = int.MaxValue;
                return;
            }
            timingMisses++;
        }

        /// <summary>The biggest phase that isn't the mod's: what to look at first.</summary>
        public static Phase Heaviest
        {
            get
            {
                int best = 0;
                for (int i = 1; i < Count; i++)
                    if (Avg[i] > Avg[best])
                        best = i;
                return (Phase)best;
            }
        }

        /// <summary>Managed heap in use, Unity's own allocations (both bytes; Unity's may be 0
        /// where it doesn't report it).</summary>
        public static void Memory(out long managed, out long unity)
        {
            managed = GC.GetTotalMemory(false);
            try
            {
                unity = Profiler.GetTotalAllocatedMemoryLong();
            }
            catch (Exception)
            {
                unity = 0;
            }
        }

        /// <summary>One line for the log.</summary>
        public static string Summary()
        {
            if (!installed)
                return "not timed";
            var sb = new System.Text.StringBuilder();
            sb.Append($"{FrameAvg:0.0} ms (worst {FrameMax:0}):");
            for (int i = 0; i < Count; i++)
            {
                sb.Append($" {LogNames[i]} {Avg[i]:0.0}/{Max[i]:0}");
                if (i == (int)Phase.Physics)
                    sb.Append($" ({StepsAvg:0.0} steps)");
                sb.Append(i < Count - 1 ? "," : "");
            }
            if (GpuAvg >= 0.0)
                sb.Append($"; gpu {GpuAvg:0.0} ms");
            sb.Append($"; gc {Collections} in 2 s" + (Collections > 0 ? $" (frame with it {GcWorstFrame:0} ms)" : ""));
            sb.Append($", {MinuteCollections} in a minute" +
                      (MinuteCollections > 0 ? $" (every {MinuteInterval:0} s, frames {MinuteGcFrameAvg:0} ms, worst {MinuteGcFrameMax:0})" : ""));
            sb.Append($"; allocating {AllocRate:0} MB/s ({AllocRateMinute:0} over a minute)");
            sb.Append($"; mesh pools {MeshPools.Held / 1048576.0:0} MB");
            Memory(out long managed, out long unity);
            sb.Append($"; managed {managed / 1073741824.0:0.0} GB" + (unity > 0 ? $", unity {unity / 1073741824.0:0.0} GB" : ""));
            return sb.ToString();
        }
    }
}
