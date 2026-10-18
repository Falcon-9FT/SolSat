// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace SolSatellite
{
    /// <summary>
    /// Where the mod spends main-thread time. Each part is timed with a stopwatch every frame;
    /// averages and the worst frame over the last two seconds go to the settings window's
    /// "Нагрузка" tab and, every 30 seconds, to the log.
    /// </summary>
    static class Perf
    {
        public enum Part
        {
            Imagery, TileDraw, TileLoad,
            City, CityScan, CityPipeline, CityColliders, Heights, Meshes, LightMap, Roads, RoadHeights, Scatters,
            Materials, Window, Other, Terrain,
        }

        public const int Count = 17;

        public static readonly string[] Names =
        {
            "Снимки у земли", "  рисование тайлов", "Снимки: приём текстур",
            "Город", "  поиск тайлов вокруг", "  конвейер тайлов", "  коллайдеры зданий", "  высоты рельефа", "  сборка мешей",
            "  карта света", "  дороги", "  рельеф под 3D-дорогами", "  пропы Parallax по карте",
            "Материалы перед кадром", "Окно настроек", "Прочее (кэш, огни EVE)", "Рельеф: тайлы и перестройка",
        };

        /// <summary>Parts timed inside another one are not added to the total again.</summary>
        static readonly bool[] Nested =
        {
            false, true, false,
            false, true, true, true, true, true, true, true, true, true,
            false, false, false, false,
        };

        const float Window = 2f;
        static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        static readonly long[] started = new long[Count];
        static readonly double[] frame = new double[Count], sum = new double[Count], worst = new double[Count];
        static double frameSum, frameWorst, modSum, modWorst;
        static int frames;
        static float windowStart;

        /// <summary>Per part, over the last window: average ms per frame and the worst frame.</summary>
        public static readonly double[] Avg = new double[Count], Max = new double[Count];
        public static double FrameAvg, FrameMax, ModAvg, ModMax;

        // The slowest frames of the mod without a garbage collection in them (a collection's
        // pause lands in whatever allocated, and says nothing of it), with their parts and the
        // slowest single piece of work in them as whoever timed it named it (Slow, Named): kept
        // for the log's 30 s line and for the mod profile, each taking its own (TakeSpike).
        const double SpikeMs = 20.0;     // a frame of the mod this long is noted
        const double SlowStepMs = 4.0;   // a piece of work this long is named
        public enum SpikeFor { Log, Profile }
        static readonly double[] spikeMs = new double[2];
        static readonly string[] spikeWhat = new string[2];
        static double stepMs;
        static string stepWhat;
        static int gcSeen = -1;

        public static void Begin(Part p) => started[(int)p] = Stopwatch.GetTimestamp();

        public static void End(Part p) => frame[(int)p] += (Stopwatch.GetTimestamp() - started[(int)p]) * MsPerTick;

        /// <summary>Whether a piece of work begun at `start` (Stopwatch ticks) is slow enough to be
        /// named and the slowest of this frame so far; if so the caller names it (Named).</summary>
        public static bool Slow(long start, out double ms)
        {
            ms = (Stopwatch.GetTimestamp() - start) * MsPerTick;
            return ms >= SlowStepMs && ms > stepMs;
        }

        public static void Named(double ms, string what)
        {
            stepMs = ms;
            stepWhat = what;
        }

        /// <summary>Names the work since `start` if it was slow (Slow); `start` moves on to now.
        /// `what` and `n` make the name only then.</summary>
        public static void Step(ref long start, string what, int n = int.MinValue)
        {
            if (Slow(start, out double ms))
                Named(ms, n == int.MinValue ? what : what + " " + n);
            start = Stopwatch.GetTimestamp();
        }

        /// <summary>The slowest frame of the mod without a collection since the last call for
        /// `who` (null: none over SpikeMs), and starts again.</summary>
        public static string TakeSpike(SpikeFor who)
        {
            string what = spikeWhat[(int)who];
            spikeWhat[(int)who] = null;
            spikeMs[(int)who] = 0.0;
            return what;
        }

        static void NoteSpike(double mod)
        {
            if (mod <= spikeMs[0] && mod <= spikeMs[1])
                return;
            var sb = new StringBuilder();
            sb.Append($"{mod:0} ms at {DateTime.Now:HH:mm:ss}:");
            for (int i = 0; i < Count; i++)
                if (frame[i] >= 1.0)
                    sb.Append($" {Names[i].Trim()} {frame[i]:0}");
            if (stepWhat != null)
                sb.Append($"; slowest step: {stepWhat}, {stepMs:0} ms");
            string what = sb.ToString();
            for (int k = 0; k < spikeMs.Length; k++)
            {
                if (mod <= spikeMs[k])
                    continue;
                spikeMs[k] = mod;
                spikeWhat[k] = what;
            }
        }

        /// <summary>Closes the frame that just ended; call once per frame, before anything is timed.</summary>
        public static void NextFrame()
        {
            double mod = 0.0;
            for (int i = 0; i < Count; i++)
                if (!Nested[i])
                    mod += frame[i];
            int gc = GC.CollectionCount(0);
            if (mod >= SpikeMs && gc == gcSeen)
                NoteSpike(mod);
            gcSeen = gc;
            stepMs = 0.0;
            stepWhat = null;
            for (int i = 0; i < Count; i++)
            {
                sum[i] += frame[i];
                if (frame[i] > worst[i])
                    worst[i] = frame[i];
                frame[i] = 0.0;
            }
            modSum += mod;
            if (mod > modWorst)
                modWorst = mod;
            double dt = Time.unscaledDeltaTime * 1000.0;
            frameSum += dt;
            if (dt > frameWorst)
                frameWorst = dt;
            frames++;

            float now = Time.realtimeSinceStartup;
            if (now - windowStart < Window)
                return;
            for (int i = 0; i < Count; i++)
            {
                Avg[i] = sum[i] / frames;
                Max[i] = worst[i];
                sum[i] = worst[i] = 0.0;
            }
            FrameAvg = frameSum / frames;
            FrameMax = frameWorst;
            ModAvg = modSum / frames;
            ModMax = modWorst;
            frameSum = frameWorst = modSum = modWorst = 0.0;
            frames = 0;
            windowStart = now;
        }

        /// <summary>One line for the log (the last two seconds; the slowest frame of the mod
        /// without a garbage collection since the last line).</summary>
        public static string Summary()
        {
            var sb = new StringBuilder();
            sb.Append($"frame {FrameAvg:0.0} ms (worst {FrameMax:0}), mod {ModAvg:0.00} ms (worst {ModMax:0.0}):");
            for (int i = 0; i < Count; i++)
                if (Max[i] >= 0.05)
                    sb.Append($" {Names[i].Trim()} {Avg[i]:0.00}/{Max[i]:0.0}");
            string spike = TakeSpike(SpikeFor.Log);
            if (spike != null)
                sb.Append($"; slowest mod frame without GC since the last line {spike}");
            return sb.ToString();
        }
    }
}
