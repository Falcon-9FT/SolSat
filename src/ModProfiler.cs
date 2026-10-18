// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SolSatellite
{
    /// <summary>
    /// Records for a while which mods take the scripts' part of the frame (the settings window's
    /// "Нагрузка" tab; a report in PluginData\Logs). Every per-frame method that mods and KSP
    /// declare on their MonoBehaviours - Update, LateUpdate, FixedUpdate, OnGUI, the camera
    /// callbacks, and the OnUpdate/OnFixedUpdate that KSP calls on part and IVA modules - gets a
    /// timer (Harmony prefix and postfix) for the recording and loses it afterwards; patching
    /// and unpatching go a slice of frames at a time (some 1400 methods with this game's mods,
    /// 2026-10-02). Times are exclusive: a patched method called from another one is charged to
    /// its own mod. Each camera's rendering (the main thread's part: culling, drawing commands)
    /// is timed by Camera.onPreCull/onPostRender. Not seen: coroutines, events, PQS mods, jobs,
    /// Harmony patches mods put on KSP's methods (they count as the KSP method), what the
    /// graphics card does. Main thread only.
    /// </summary>
    static class ModProfiler
    {
        public enum Stage { Idle, Patching, Recording, Unpatching }

        public const float RecordSeconds = 30f;
        const string HarmonyId = "SolSatellite.ModProfiler";
        const double SliceMs = 12.0; // patching or unpatching, per frame

        static readonly string[] Messages =
        {
            "Update", "LateUpdate", "FixedUpdate", "OnGUI", "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderImage",
            "OnRenderObject", "OnWillRenderObject",
        };

        /// <summary>Not Unity messages: KSP calls these on part modules (Part) and IVA modules.</summary>
        static readonly string[] ModuleUpdates = { "OnUpdate", "OnFixedUpdate" };

        sealed class Mod
        {
            public string Name;
            public double Frame, Total, Worst;
            public long Calls;
            public bool Touched;
            public long Bytes; // allocated in its methods (exclusive), the whole recording
        }

        sealed class Target
        {
            public string Name; // Type.Method
            public Mod Mod;
            public double Frame, Total, Worst;
            public long Calls;
            public bool Touched;
            public long Bytes;
        }

        sealed class Cam
        {
            public string Name;
            public long Start;
            public double Frame, Total, Worst;
            public int Renders;
            public bool Touched;
            public long Rank;                // its place among the frame's cameras, summed over renders
            public int First;                // renders it was the frame's first camera
            public double Scripts;           // timed methods' own time while it rendered (in the mods' table too)
            public readonly Dictionary<Mod, double> ByMod = new Dictionary<Mod, double>();
        }

        public static Stage Now { get; private set; } = Stage.Idle;
        /// <summary>The last recording: a few lines for the window, and where the full report went.</summary>
        public static string Summary { get; private set; }
        public static string ReportPath { get; private set; }

        static Harmony harmony;
        static HarmonyMethod prefix, postfix;
        static readonly List<MethodInfo> todo = new List<MethodInfo>();
        static readonly List<MethodInfo> patched = new List<MethodInfo>();
        static readonly Dictionary<MethodBase, Target> targets = new Dictionary<MethodBase, Target>();
        static readonly Dictionary<string, Mod> mods = new Dictionary<string, Mod>();
        static readonly Dictionary<Camera, Cam> cams = new Dictionary<Camera, Cam>();
        static readonly List<Target> touched = new List<Target>();
        static readonly List<Mod> touchedMods = new List<Mod>();
        static readonly List<Cam> touchedCams = new List<Cam>();
        static int next, failed, frames, gcStart, lastGc, gcFrames, camRank;
        static float recordUntil, recordStart;
        static string cutShort; // why the recording ended early (scene change, game closing), for the report
        static double[] partsAtStart = new double[0]; // FrameProfile's parts of Unity's loop as the recording began
        static double frameSum, frameWorst, frameWorstClean;
        // Allocation: the managed heap in use (GC.GetTotalMemory, all threads) as each timed
        // method starts and ends, exclusive like the times; and from frame to frame, frames
        // with a collection left out. Worker threads allocating meanwhile land in whatever ran.
        static long usedAtFrame = -1, allocFrames;
        static double allocMs;
        // Cameras rendering now, innermost last (a mod may render one inside another's callbacks).
        static readonly List<Cam> camStack = new List<Cam>();
        static readonly double[] phaseSum = new double[FrameProfile.Count];
        static long calls;

        // Exclusive timing: the methods running now, innermost last.
        static readonly Target[] stack = new Target[64];
        static readonly long[] since = new long[64];
        static readonly long[] usedSince = new long[64];
        static int depth;
        static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        public static string Status
        {
            get
            {
                switch (Now)
                {
                    case Stage.Patching:
                        return $"Подготовка: таймеры на методы {next} из {todo.Count} (игра подтормаживает)";
                    case Stage.Recording:
                        return $"Запись: ещё {Math.Max(0f, recordUntil - Time.realtimeSinceStartup):0} с, {frames} кадров " +
                               $"({patched.Count} методов{(failed > 0 ? $", {failed} не вышло" : "")})";
                    case Stage.Unpatching:
                        return $"Снимаю таймеры: {patched.Count} осталось";
                    default:
                        return "";
                }
            }
        }

        public static void Start()
        {
            if (Now != Stage.Idle)
                return;
            try
            {
                harmony ??= new Harmony(HarmonyId);
                prefix ??= new HarmonyMethod(typeof(ModProfiler).GetMethod(nameof(Enter), BindingFlags.Static | BindingFlags.NonPublic));
                postfix ??= new HarmonyMethod(typeof(ModProfiler).GetMethod(nameof(Leave), BindingFlags.Static | BindingFlags.NonPublic));
                Collect();
                next = failed = 0;
                Now = Stage.Patching;
                Debug.Log($"[SolSatellite] Mod profile: putting timers on {todo.Count} methods of {mods.Count} mods");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Now = Stage.Idle;
            }
        }

        /// <summary>Ends the recording early (the report covers what was recorded). why: for the report
        /// (null: stopped from the window).</summary>
        public static void Stop(string why = null)
        {
            if (Now == Stage.Recording)
            {
                cutShort = why;
                Finish();
            }
            else if (Now == Stage.Patching)
                Now = Stage.Unpatching;
        }

        /// <summary>Game closing: the report of a recording under way (2026-10-17: a recording lost to the game
        /// closed 21 s into it), then every timer off at once.</summary>
        public static void Abort()
        {
            if (Now == Stage.Recording)
                Stop("игра закрыта");
            if (Now == Stage.Idle || harmony == null)
                return;
            try
            {
                harmony.UnpatchAll(HarmonyId);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            patched.Clear();
            Now = Stage.Idle;
        }

        /// <summary>Every frame: a slice of patching or unpatching, the end of the recording.</summary>
        public static void Update()
        {
            switch (Now)
            {
                case Stage.Patching:
                    PatchSome();
                    break;
                case Stage.Recording:
                    if (Time.realtimeSinceStartup >= recordUntil)
                        Finish();
                    break;
                case Stage.Unpatching:
                    UnpatchSome();
                    break;
            }
        }

        // ------------------------------------------------------------------ what gets a timer

        static void Collect()
        {
            todo.Clear();
            patched.Clear();
            targets.Clear();
            mods.Clear();
            cams.Clear();
            touched.Clear();
            touchedMods.Clear();
            touchedCams.Clear();
            Assembly ksp = typeof(Part).Assembly;
            Add(ksp, "KSP");
            foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
            {
                if (la?.assembly == null || la.assembly == ksp)
                    continue;
                string url = (la.url ?? "").Replace('\\', '/');
                string mod = url.Length > 0 ? url.Split('/')[0] : la.name;
                Add(la.assembly, string.IsNullOrEmpty(mod) ? la.name : mod);
            }
        }

        static void Add(Assembly assembly, string modName)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
            }
            catch (Exception)
            {
                return;
            }
            foreach (Type t in types)
            {
                try
                {
                    if (t == null || t.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(t))
                        continue;
                    bool module = typeof(PartModule).IsAssignableFrom(t) || typeof(InternalModule).IsAssignableFrom(t);
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!Wanted(m, module))
                            continue;
                        if (!mods.TryGetValue(modName, out Mod mod))
                            mods[modName] = mod = new Mod { Name = modName };
                        todo.Add(m);
                        targets[m] = new Target { Name = t.Name + "." + m.Name, Mod = mod };
                    }
                }
                catch (Exception)
                {
                    // a type whose base doesn't load: not ours to time
                }
            }
        }

        static bool Wanted(MethodInfo m, bool module)
        {
            if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null)
                return false;
            int n = m.GetParameters().Length;
            if (m.Name == "OnRenderImage")
                return n == 2;
            if (Array.IndexOf(Messages, m.Name) >= 0)
                return n == 0;
            return module && n == 0 && Array.IndexOf(ModuleUpdates, m.Name) >= 0;
        }

        static void PatchSome()
        {
            var clock = Stopwatch.StartNew();
            while (next < todo.Count && clock.Elapsed.TotalMilliseconds < SliceMs)
            {
                MethodInfo m = todo[next++];
                try
                {
                    harmony.Patch(m, prefix, postfix);
                    patched.Add(m);
                }
                catch (Exception e)
                {
                    if (failed++ < 5)
                        Debug.LogWarning($"[SolSatellite] Mod profile: no timer on {m.DeclaringType?.FullName}.{m.Name}: {e.Message}");
                }
            }
            if (next < todo.Count)
                return;
            foreach (Target t in targets.Values)
                t.Frame = t.Total = t.Worst = 0.0;
            frames = gcFrames = camRank = 0;
            frameSum = frameWorst = frameWorstClean = 0.0;
            Array.Clear(phaseSum, 0, phaseSum.Length);
            calls = 0;
            depth = 0;
            camStack.Clear();
            gcStart = lastGc = GC.CollectionCount(0);
            usedAtFrame = -1;
            allocFrames = 0;
            allocMs = 0.0;
            recordStart = Time.realtimeSinceStartup;
            recordUntil = recordStart + RecordSeconds;
            cutShort = null;
            partsAtStart = new double[FrameProfile.PartCount];
            FrameProfile.CopyParts(partsAtStart);
            Perf.TakeSpike(Perf.SpikeFor.Profile); // from now on
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
            Now = Stage.Recording;
            Debug.Log($"[SolSatellite] Mod profile: recording {RecordSeconds:0} s ({patched.Count} methods, {failed} couldn't be timed)");
        }

        static void Finish()
        {
            Camera.onPreCull -= OnPreCull;
            Camera.onPostRender -= OnPostRender;
            try
            {
                Report();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            Now = Stage.Unpatching;
        }

        static void UnpatchSome()
        {
            var clock = Stopwatch.StartNew();
            while (patched.Count > 0 && clock.Elapsed.TotalMilliseconds < SliceMs)
            {
                MethodInfo m = patched[patched.Count - 1];
                patched.RemoveAt(patched.Count - 1);
                try
                {
                    harmony.Unpatch(m, HarmonyPatchType.All, HarmonyId);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SolSatellite] Mod profile: timer stays on {m.DeclaringType?.FullName}.{m.Name}: {e.Message}");
                }
            }
            if (patched.Count == 0)
            {
                Now = Stage.Idle;
                Debug.Log("[SolSatellite] Mod profile: timers off");
            }
        }

        // ------------------------------------------------------------------ timing

        static void Charge(Target t, double ms)
        {
            t.Frame += ms;
            if (camStack.Count > 0)
            {
                // Run while a camera rendered (its OnPreCull, OnPreRender, OnRenderImage...).
                Cam c = camStack[camStack.Count - 1];
                c.Scripts += ms;
                c.ByMod.TryGetValue(t.Mod, out double had);
                c.ByMod[t.Mod] = had + ms;
            }
            if (t.Touched)
                return;
            t.Touched = true;
            touched.Add(t);
        }

        // Harmony prefix on every timed method: what ran so far was the caller's.
        static void Enter(MethodBase __originalMethod)
        {
            if (Now != Stage.Recording)
                return;
            long used = GC.GetTotalMemory(false);
            long now = Stopwatch.GetTimestamp();
            if (depth > 0)
            {
                Target caller = stack[depth - 1];
                if (caller != null)
                {
                    Charge(caller, (now - since[depth - 1]) * MsPerTick);
                    caller.Bytes += Math.Max(0L, used - usedSince[depth - 1]);
                }
            }
            if (depth >= stack.Length)
                depth = 0; // something never returned: start over
            Target t = null;
            if (__originalMethod != null)
                targets.TryGetValue(__originalMethod, out t);
            stack[depth] = t;
            since[depth] = now;
            usedSince[depth] = used;
            depth++;
        }

        // Harmony postfix: the method's own time; its caller runs on from now.
        static void Leave()
        {
            if (depth == 0)
                return;
            long now = Stopwatch.GetTimestamp();
            long used = GC.GetTotalMemory(false);
            depth--;
            Target t = stack[depth];
            stack[depth] = null;
            if (t != null)
            {
                Charge(t, (now - since[depth]) * MsPerTick);
                t.Calls++;
                t.Bytes += Math.Max(0L, used - usedSince[depth]);
            }
            calls++;
            if (depth > 0)
            {
                since[depth - 1] = now;
                usedSince[depth - 1] = used;
            }
        }

        /// <summary>Between player loop phases nothing timed is running: what is left on the
        /// stack threw (its postfix never came) and is dropped.</summary>
        public static void Boundary() => depth = 0;

        /// <summary>A frame closed (FrameProfile): its times go to the totals.</summary>
        public static void FrameClosed(double[] phases, double total)
        {
            depth = 0;
            camStack.Clear();
            camRank = 0;
            if (Now != Stage.Recording)
                return;
            frames++;
            frameSum += total;
            frameWorst = Math.Max(frameWorst, total);
            // A frame with a garbage collection: the collector's pause lands in whatever method
            // happened to allocate, so it says nothing of that method; not a worst frame.
            int gc = GC.CollectionCount(0);
            bool collected = gc != lastGc;
            lastGc = gc;
            if (collected)
                gcFrames++;
            else
                frameWorstClean = Math.Max(frameWorstClean, total);
            long usedNow = GC.GetTotalMemory(false);
            if (usedAtFrame >= 0 && !collected)
            {
                allocFrames += Math.Max(0L, usedNow - usedAtFrame);
                allocMs += total;
            }
            usedAtFrame = usedNow;
            for (int i = 0; i < phaseSum.Length && i < phases.Length; i++)
                phaseSum[i] += phases[i];
            foreach (Target t in touched)
            {
                t.Total += t.Frame;
                if (!collected)
                    t.Worst = Math.Max(t.Worst, t.Frame);
                Mod m = t.Mod;
                m.Frame += t.Frame;
                m.Calls += t.Calls;
                t.Calls = 0;
                if (!m.Touched)
                {
                    m.Touched = true;
                    touchedMods.Add(m);
                }
                t.Frame = 0.0;
                t.Touched = false;
            }
            touched.Clear();
            foreach (Mod m in touchedMods)
            {
                m.Total += m.Frame;
                if (!collected)
                    m.Worst = Math.Max(m.Worst, m.Frame);
                m.Frame = 0.0;
                m.Touched = false;
            }
            touchedMods.Clear();
            foreach (Cam c in touchedCams)
            {
                c.Total += c.Frame;
                if (!collected)
                    c.Worst = Math.Max(c.Worst, c.Frame);
                c.Frame = 0.0;
                c.Touched = false;
            }
            touchedCams.Clear();
        }

        // Cameras are timed exclusively too: one rendered inside another's callbacks counts
        // for itself only.
        static void OnPreCull(Camera cam)
        {
            if (Now != Stage.Recording || cam == null)
                return;
            long now = Stopwatch.GetTimestamp();
            if (!cams.TryGetValue(cam, out Cam c))
                cams[cam] = c = new Cam { Name = cam.name };
            if (camStack.Count > 0)
            {
                Cam outer = camStack[camStack.Count - 1];
                outer.Frame += (now - outer.Start) * MsPerTick;
            }
            camStack.Remove(c); // its last render never ended (nothing to draw): starts over
            camStack.Add(c);
            c.Start = now;
            c.Rank += ++camRank;
            if (camRank == 1)
                c.First++;
        }

        static void OnPostRender(Camera cam)
        {
            if (Now != Stage.Recording || cam == null || !cams.TryGetValue(cam, out Cam c))
                return;
            int at = camStack.LastIndexOf(c);
            if (at < 0)
                return;
            camStack.RemoveRange(at, camStack.Count - at); // cameras inside it that never ended go too
            long now = Stopwatch.GetTimestamp();
            c.Frame += (now - c.Start) * MsPerTick;
            c.Renders++;
            if (camStack.Count > 0)
                camStack[camStack.Count - 1].Start = now;
            if (c.Touched)
                return;
            c.Touched = true;
            touchedCams.Add(c);
        }

        // ------------------------------------------------------------------ the report

        static void Report()
        {
            var ci = CultureInfo.InvariantCulture;
            int n = Math.Max(1, frames);
            double frame = frameSum / n;
            double seconds = Time.realtimeSinceStartup - recordStart;
            double scripts = 0.0;
            var modList = new List<Mod>(mods.Values);
            foreach (Mod m in modList)
                scripts += m.Total / n;
            modList.Sort((a, b) => b.Total.CompareTo(a.Total));
            var methodList = new List<Target>(targets.Values);
            methodList.Sort((a, b) => b.Total.CompareTo(a.Total));
            var camList = new List<Cam>(cams.Values);
            camList.Sort((a, b) => b.Total.CompareTo(a.Total));
            double scriptPhases = phaseSum[(int)FrameProfile.Phase.Physics] / n + phaseSum[(int)FrameProfile.Phase.Update] / n +
                                  phaseSum[(int)FrameProfile.Phase.LateUpdate] / n;

            var sb = new StringBuilder();
            sb.AppendLine($"Профиль по модам, SolSatellite, {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", ci)}");
            sb.AppendLine($"Сцена {HighLogic.LoadedScene}" + Where() + $"; запись {seconds:0.0} с, {frames} кадров, " +
                          $"средний кадр {frame:0.0} мс ({1000.0 / Math.Max(frame, 0.1):0} к/с), худший {frameWorst:0} мс" +
                          (gcFrames > 0 ? $" (без сборок мусора {frameWorstClean:0} мс)" : ""));
            if (cutShort != null)
                sb.AppendLine($"Запись прервана раньше {RecordSeconds:0} с: {cutShort}");
            sb.Append("Фазы кадра, мс:");
            for (int i = 0; i < FrameProfile.Count; i++)
                sb.Append($" {FrameProfile.Names[i]} {phaseSum[i] / n:0.0};");
            sb.AppendLine();
            sb.AppendLine($"Сборок мусора за запись: {GC.CollectionCount(0) - gcStart}" +
                          (gcFrames > 0 ? $"; кадры с ними ({gcFrames}) не считаются «худшими» ниже: пауза сборщика достаётся тому, кто как раз выделял память" : ""));
            sb.AppendLine($"Таймеры на {patched.Count} методах ({failed} не вышло), {calls / (double)n:0} вызовов за кадр: " +
                          $"сам замер добавляет к кадру примерно {calls / (double)n * 0.0004:0.0} мс");
            // Allocation, MB a second of the recording's frames without a collection.
            double cleanSeconds = Math.Max(0.001, allocMs / 1000.0);
            long methodBytes = 0;
            foreach (Target t in targets.Values)
            {
                methodBytes += t.Bytes;
                t.Mod.Bytes += t.Bytes;
            }
            sb.AppendLine($"Память: игра выделяет ~{allocFrames / 1048576.0 / cleanSeconds:0} МБ/с (все потоки); в методах под таймерами " +
                          $"~{methodBytes / 1048576.0 / seconds:0} МБ/с, остальное — фоновые потоки модов, корутины, движок. Сборка мусора " +
                          "начинается, когда выделено столько-то от занятого, так что чем больше МБ/с, тем чаще сборки.");
            string spike = Perf.TakeSpike(Perf.SpikeFor.Profile);
            if (spike != null)
                sb.AppendLine("SolSatellite, самый долгий свой кадр без сборки мусора (части мода и самый долгий шаг): " + spike);
            sb.AppendLine();
            sb.AppendLine("МОДЫ — собственное время их скриптов, мс на кадр: среднее / худший кадр / доля кадра / вызовов за кадр /");
            sb.AppendLine("  выделено памяти, МБ/с (на главном потоке; фоновые потоки, выделяющие в это же время, попадают сюда же — мелкие цифры — шум)");
            foreach (Mod m in modList)
            {
                if (m.Total <= 0.0)
                    continue;
                sb.AppendLine(string.Format(ci, "  {0,-36} {1,7:0.00} {2,8:0.0} {3,5:0.0}% {4,8:0} {5,8:0.0}", m.Name, m.Total / n, m.Worst,
                                            m.Total / n / Math.Max(frame, 0.1) * 100.0, m.Calls / (double)n, m.Bytes / 1048576.0 / seconds));
            }
            sb.AppendLine(string.Format(ci, "  {0,-36} {1,7:0.00}", "Остальное в физике, Update и LateUpdate",
                                        Math.Max(0.0, scriptPhases - scripts)));
            sb.AppendLine("    (движок Unity и PhysX, корутины, события, PQS, патчи модов на методы KSP)");
            sb.AppendLine();
            UnityParts(sb, ci, n);
            sb.AppendLine("САМЫЕ ТЯЖЁЛЫЕ МЕТОДЫ, мс на кадр: среднее / худший кадр — мод");
            for (int i = 0; i < methodList.Count && i < 40; i++)
            {
                Target t = methodList[i];
                if (t.Total <= 0.0)
                    break;
                sb.AppendLine(string.Format(ci, "  {0,-56} {1,7:0.00} {2,8:0.0}  {3}", t.Name, t.Total / n, t.Worst, t.Mod.Name));
            }
            sb.AppendLine();
            sb.AppendLine("БОЛЬШЕ ВСЕГО ВЫДЕЛЯЮТ ПАМЯТИ, МБ/с — мод");
            methodList.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            for (int i = 0; i < methodList.Count && i < 15; i++)
            {
                Target t = methodList[i];
                if (t.Bytes / 1048576.0 / seconds < 0.05)
                    break;
                sb.AppendLine(string.Format(ci, "  {0,-56} {1,7:0.0}  {2}", t.Name, t.Bytes / 1048576.0 / seconds, t.Mod.Name));
            }
            sb.AppendLine();
            sb.AppendLine("КАМЕРЫ — отрисовка на главном потоке (отсечение, команды для видеокарты), мс на кадр: среднее / худший кадр / раз за кадр /");
            sb.AppendLine("  место среди камер кадра (среднее; сколько раз первая). Скрипты внутри — время модов, пока камера рисовалась (оно же есть в");
            sb.AppendLine("  таблице модов); остальное — сама отрисовка Unity. Первая камера кадра может нести и ожидание потока рендера.");
            foreach (Cam c in camList)
            {
                if (c.Total <= 0.0)
                    continue;
                sb.AppendLine(string.Format(ci, "  {0,-44} {1,7:0.00} {2,8:0.0} {3,6:0.0}   место {4:0.0}, первая {5:0}%",
                                            c.Name, c.Total / n, c.Worst, c.Renders / (double)n,
                                            c.Rank / (double)Math.Max(1, c.Renders), 100.0 * c.First / Math.Max(1, c.Renders)));
                if (c.Scripts / n >= 0.05)
                {
                    var inside = new List<KeyValuePair<Mod, double>>(c.ByMod);
                    inside.Sort((a, b) => b.Value.CompareTo(a.Value));
                    var line = new StringBuilder();
                    for (int i = 0; i < inside.Count && i < 4; i++)
                        line.Append(string.Format(ci, "{0}{1} {2:0.00}", i > 0 ? ", " : "", inside[i].Key.Name, inside[i].Value / n));
                    sb.AppendLine(string.Format(ci, "      скрипты внутри {0:0.00}: {1}; отрисовка Unity {2:0.00}",
                                                c.Scripts / n, line, Math.Max(0.0, (c.Total - c.Scripts) / n)));
                }
            }
            sb.AppendLine();
            sb.AppendLine("Не видно: работа видеокарты, нативный рендер вне камер, корутины и события модов, PQS-моды,");
            sb.AppendLine("задачи Burst; патчи модов (Harmony) на методы KSP считаются временем того метода KSP.");

            string dir = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "SolSatellite", "PluginData", "Logs");
            Directory.CreateDirectory(dir);
            ReportPath = Path.Combine(dir, $"mod-profile-{DateTime.Now.ToString("yyyyMMdd-HHmmss", ci)}.txt");
            File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));

            var s = new StringBuilder();
            s.Append($"Кадр {frame:0.0} мс за {seconds:0} с; скрипты модов и KSP {scripts:0.0} мс, их нет в рендере и видеокарте.\n");
            int shown = 0;
            foreach (Mod m in modList)
            {
                if (m.Total <= 0.0 || shown++ >= 8)
                    break;
                s.Append($"{m.Name}: {m.Total / n:0.00} мс (худший {m.Worst:0.0})\n");
            }
            if (camList.Count > 0)
            {
                s.Append("Камеры:");
                for (int i = 0; i < camList.Count && i < 4; i++)
                    s.Append($" {camList[i].Name} {camList[i].Total / n:0.0} мс;");
                s.Append('\n');
            }
            Summary = s.ToString().TrimEnd();
            Debug.Log("[SolSatellite] Mod profile written to " + ReportPath + "\n" + sb);
        }

        static readonly Dictionary<string, string> PartHints = new Dictionary<string, string>
        {
            { "PreUpdate.SendMouseEvents", "OnMouse*: лучи из камер в коллайдеры" },
            { "PreUpdate.PhysicsUpdate", "физика: перенос сдвинутых объектов" },
            { "FixedUpdate.PhysicsFixedUpdate", "PhysX, шаги физики" },
            { "FixedUpdate.ScriptRunDelayedFixedFrameRate", "корутины на шагах физики" },
            { "Update.ScriptRunDelayedDynamicFrameRate", "корутины" },
            { "Update.ScriptRunDelayedTasks", "отложенные вызовы движка" },
            { "PreLateUpdate.DirectorUpdateAnimationBegin", "анимации" },
            { "PreLateUpdate.DirectorUpdateAnimationEnd", "анимации" },
            { "PostLateUpdate.FinishFrameRendering", "камеры" },
            { "PostLateUpdate.PresentAfterDraw", "ожидание видеокарты" },
            { "PostLateUpdate.PlayerUpdateCanvases", "интерфейс" },
            { "PostLateUpdate.PlayerEmitCanvasGeometry", "интерфейс" },
            { "PostLateUpdate.UpdateAllRenderers", "границы отрисовываемых объектов" },
            { "PostLateUpdate.ParticleSystemEndUpdateAll", "частицы" },
        };

        /// <summary>Unity's own parts of the frame's loop over the recording (FrameProfile), the biggest first; for the
        /// parts that call scripts, how much of it the timers saw (the rest: Unity calling them, untimed scripts).</summary>
        static void UnityParts(StringBuilder sb, CultureInfo ci, int n)
        {
            int count = FrameProfile.PartCount;
            if (count == 0)
                return;
            var now = new double[count];
            FrameProfile.CopyParts(now);
            double fixedTimed = 0.0, updateTimed = 0.0, lateTimed = 0.0;
            foreach (Target t in targets.Values)
            {
                if (t.Name.EndsWith(".FixedUpdate") || t.Name.EndsWith(".OnFixedUpdate"))
                    fixedTimed += t.Total;
                else if (t.Name.EndsWith(".LateUpdate"))
                    lateTimed += t.Total;
                else if (t.Name.EndsWith(".Update") || t.Name.EndsWith(".OnUpdate"))
                    updateTimed += t.Total;
            }
            var ms = new double[count];
            var order = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                ms[i] = (now[i] - (i < partsAtStart.Length ? partsAtStart[i] : 0.0)) / n;
                order.Add(i);
            }
            order.Sort((a, b) => ms[b].CompareTo(ms[a]));
            sb.AppendLine("ЦИКЛ КАДРА UNITY по частям, мс на кадр (вместе со скриптами, что в них вызываются):");
            foreach (int i in order)
            {
                if (ms[i] < 0.05)
                    break;
                string name = FrameProfile.PartName(i);
                double timed = name.EndsWith(".ScriptRunBehaviourUpdate") ? updateTimed / n
                             : name.EndsWith(".ScriptRunBehaviourLateUpdate") ? lateTimed / n
                             : name.EndsWith(".ScriptRunBehaviourFixedUpdate") ? fixedTimed / n
                             : -1.0;
                string note = timed >= 0.0 ? string.Format(ci, "   скрипты под таймерами {0:0.00}, вне их {1:0.00}", timed, Math.Max(0.0, ms[i] - timed))
                            : PartHints.TryGetValue(name, out string hint) ? "   " + hint : "";
                sb.AppendLine(string.Format(ci, "  {0,-56} {1,7:0.00}{2}", name, ms[i], note));
            }
            sb.AppendLine();
        }

        static string Where()
        {
            try
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (HighLogic.LoadedSceneIsFlight && v != null)
                    return string.Format(CultureInfo.InvariantCulture, ", аппарат «{0}» ({1} деталей) над {2} на {3:0} м, {4:0.0000} {5:0.0000}",
                                         v.vesselName, v.parts.Count, v.mainBody?.bodyName, v.altitude, v.latitude, v.longitude);
            }
            catch (Exception)
            {
            }
            return "";
        }
    }
}
