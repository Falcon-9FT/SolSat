// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;

namespace SolSatellite
{
    /// <summary>
    /// "Перепись сцены" (the Нагрузка tab): counts what the game holds - renderers and colliders by
    /// object name, scripts by type, materials, meshes and textures, command buffers on cameras and
    /// lights - and writes it with the change since the previous count to PluginData\Logs. Made for
    /// frames that get slower the longer one flies (2026-10-20, clean Sol with EVE's volumetric
    /// clouds: 6 -> 45 ms in 10 minutes, video memory flat): what piles up is the biggest increase.
    /// Goes through every object in the game: a hitch of a fraction of a second, only on the button.
    /// </summary>
    static class SceneCensus
    {
        static readonly Regex Digits = new Regex(@"\d+");
        static Dictionary<string, int> previous;
        static DateTime previousAt;

        public static string Summary { get; private set; }
        public static string ReportPath { get; private set; }

        public static void Take()
        {
            var counts = new Dictionary<string, int>();
            void Add(string key)
            {
                counts.TryGetValue(key, out int n);
                counts[key] = n + 1;
            }

            int renderers = 0, colliders = 0, scripts = 0;
            foreach (Renderer r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                renderers++;
                Add("рендерер   " + Where(r.transform) + (r.enabled ? "" : " (выкл.)"));
            }
            foreach (Collider c in UnityEngine.Object.FindObjectsOfType<Collider>())
            {
                colliders++;
                Add("коллайдер  " + Where(c.transform));
            }
            foreach (MonoBehaviour b in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (b == null)
                    continue;
                scripts++;
                Type t = b.GetType();
                Add("скрипт     " + t.Assembly.GetName().Name + ": " + t.FullName);
            }
            counts["всего      рендереров на сцене"] = renderers;
            counts["всего      коллайдеров на сцене"] = colliders;
            counts["всего      скриптов на сцене"] = scripts;
            // Assets too (FindObjectsOfTypeAll): what was made in code and never destroyed is here.
            counts["всего      объектов (GameObject, с префабами)"] = Resources.FindObjectsOfTypeAll<GameObject>().Length;
            counts["всего      материалов"] = Resources.FindObjectsOfTypeAll<Material>().Length;
            counts["всего      мешей"] = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            counts["всего      текстур Texture2D"] = Resources.FindObjectsOfTypeAll<Texture2D>().Length;
            counts["всего      RenderTexture"] = Resources.FindObjectsOfTypeAll<RenderTexture>().Length;
            counts["всего      compute-шейдеров"] = Resources.FindObjectsOfTypeAll<ComputeShader>().Length;
            foreach (Camera cam in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (cam == null || !cam.gameObject.scene.IsValid())
                    continue; // a prefab's
                int n = 0;
                foreach (CameraEvent e in Enum.GetValues(typeof(CameraEvent)))
                    n += cam.GetCommandBuffers(e).Length;
                Add("камера     " + Clean(cam.name) + (cam.enabled ? "" : " (выкл.)"));
                if (n > 0)
                    counts["буферы     command buffer'ов на камере " + Clean(cam.name)] = n;
            }
            foreach (Light light in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                int n = 0;
                foreach (LightEvent e in Enum.GetValues(typeof(LightEvent)))
                    n += light.GetCommandBuffers(e).Length;
                Add("свет       " + Clean(light.name));
                if (n > 0)
                {
                    string key = "буферы     command buffer'ов на свете " + Clean(light.name);
                    counts.TryGetValue(key, out int had);
                    counts[key] = had + n;
                }
            }

            CultureInfo ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            DateTime now = DateTime.Now;
            sb.AppendLine($"Перепись сцены, SolSatellite, {now.ToString("yyyy-MM-dd HH:mm:ss", ci)}");
            sb.AppendLine($"Сцена {HighLogic.LoadedScene}" + (FlightGlobals.ActiveVessel != null
                ? $", аппарат «{FlightGlobals.ActiveVessel.vesselName}» над {FlightGlobals.ActiveVessel.mainBody.name} на {FlightGlobals.ActiveVessel.altitude:0} м"
                : "") + $"; кадр сейчас {Time.unscaledDeltaTime * 1000f:0} мс");
            sb.AppendLine($"Рендереров {renderers}, коллайдеров {colliders}, скриптов {scripts} (на сцене); строки сгруппированы по имени " +
                          "объекта и его родителя, цифры в именах заменены на #.");
            sb.AppendLine();

            string top = null;
            if (previous != null)
            {
                var change = new List<KeyValuePair<string, int>>();
                foreach (var kv in counts)
                {
                    previous.TryGetValue(kv.Key, out int was);
                    if (kv.Value != was)
                        change.Add(new KeyValuePair<string, int>(kv.Key, kv.Value - was));
                }
                foreach (var kv in previous)
                    if (!counts.ContainsKey(kv.Key))
                        change.Add(new KeyValuePair<string, int>(kv.Key, -kv.Value));
                double minutes = (now - previousAt).TotalMinutes;
                change.Sort((a, b) => b.Value.CompareTo(a.Value));
                sb.AppendLine($"ПРИБАВИЛОСЬ с прошлой переписи ({minutes:0.#} мин назад): было -> стало (+разница)");
                int shown = 0;
                foreach (var kv in change)
                {
                    if (kv.Value <= 0 || shown++ >= 40)
                        break;
                    previous.TryGetValue(kv.Key, out int was);
                    sb.AppendLine(string.Format(ci, "  {0,7} -> {1,7}  (+{2})  {3}", was, counts[kv.Key], kv.Value, kv.Key));
                    if (top == null && !kv.Key.StartsWith("всего", StringComparison.Ordinal))
                        top = $"+{kv.Value} {kv.Key.Substring(11).Trim()} ({kv.Key.Substring(0, 10).Trim()})";
                }
                sb.AppendLine();
                sb.AppendLine("УБАВИЛОСЬ:");
                change.Sort((a, b) => a.Value.CompareTo(b.Value));
                shown = 0;
                foreach (var kv in change)
                {
                    if (kv.Value >= 0 || shown++ >= 20)
                        break;
                    previous.TryGetValue(kv.Key, out int was);
                    counts.TryGetValue(kv.Key, out int has);
                    sb.AppendLine(string.Format(ci, "  {0,7} -> {1,7}  ({2})  {3}", was, has, kv.Value, kv.Key));
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("Первая перепись за игру: сравнивать не с чем. Полетайте, пока кадр не потяжелеет, и нажмите ещё раз.");
                sb.AppendLine();
            }

            var all = new List<KeyValuePair<string, int>>(counts);
            all.Sort((a, b) => b.Value.CompareTo(a.Value));
            sb.AppendLine("СЕЙЧАС БОЛЬШЕ ВСЕГО:");
            for (int i = 0; i < all.Count && i < 60; i++)
                sb.AppendLine(string.Format(ci, "  {0,7}  {1}", all[i].Value, all[i].Key));

            string dir = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "SolSatellite", "PluginData", "Logs");
            Directory.CreateDirectory(dir);
            ReportPath = Path.Combine(dir, $"scene-census-{now.ToString("yyyyMMdd-HHmmss", ci)}.txt");
            File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
            Summary = previous == null
                ? $"Первая перепись: рендереров {renderers}, коллайдеров {colliders}, скриптов {scripts}. Полетайте и нажмите ещё раз."
                : $"Рендереров {renderers}, коллайдеров {colliders}, скриптов {scripts}. Больше всего прибавилось: {top ?? "ничего"}.";
            Debug.Log("[SolSatellite] Scene census written to " + ReportPath + ": " + Summary);
            previous = counts;
            previousAt = now;
        }

        /// <summary>The object's parent and own name, digits as #: tiles, quads and clones group together.</summary>
        static string Where(Transform t)
        {
            Transform p = t.parent;
            return (p != null ? Clean(p.name) + "/" : "") + Clean(t.name);
        }

        static string Clean(string name)
        {
            string s = Digits.Replace(name ?? "", "#");
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }
    }
}
