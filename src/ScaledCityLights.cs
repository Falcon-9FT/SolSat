// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using UnityEngine;

namespace SolSatellite
{
    /// <summary>
    /// EVE's city lights on Earth seen from orbit (2026-10-05: "the night cities go at 80-100 km").
    /// Above 75 km (Quarter Earth) only the scaled planet is drawn; EVE puts its lights on it as one
    /// more material on the planet's renderer (EVE/PlanetCityLight), drawn on the smooth sphere of
    /// its mesh without writing depth. Parallax draws Earth's scaled planet tessellated with the
    /// land raised by its heights, so the lights lay under the ground and failed the depth test;
    /// and EVE masks the ocean with the planet material's _MainTex alpha, which Parallax's material
    /// doesn't have (white, alpha 1: every light out). The material is put on SolSatellite's copy
    /// of EVE's shader (SolSatellite/PlanetCityLight): the same lights, their depth brought nearer
    /// along each line of sight, no _MainTex test. EVE's own material and properties stay; EVE
    /// making a new one (scene changes) gets the copy again on the next look.
    /// </summary>
    static class ScaledCityLights
    {
        const string EveShader = "EVE/PlanetCityLight";
        const double LiftMetres = 4000.0; // game m: over the highest land (2.2 km on Quarter Earth) seen at a slant
        static readonly int LiftId = Shader.PropertyToID("_SolSatLift");

        static Shader shader;
        static float nextLook;
        static bool logged;

        public static void Setup(Shader s) => shader = s;

        public static void Update()
        {
            if (shader == null || Time.realtimeSinceStartup < nextLook)
                return;
            nextLook = Time.realtimeSinceStartup + 2f;
            CelestialBody earth = FlightGlobals.GetBodyByName("Earth");
            if (earth == null || earth.scaledBody == null)
                return;
            Renderer r = earth.scaledBody.GetComponent<Renderer>();
            if (r == null)
                return;
            float lift = (float)(LiftMetres / Math.Max(1.0, ScaledSpace.ScaleFactor));
            foreach (Material m in r.sharedMaterials)
            {
                if (m == null || m.shader == null)
                    continue;
                if (m.shader.name == EveShader)
                {
                    int queue = m.renderQueue;
                    m.shader = shader;     // properties and keywords carry over by name
                    m.renderQueue = queue; // EVE's own queue
                    if (!logged)
                    {
                        logged = true;
                        Debug.Log($"[SolSatellite] EVE's city lights on Earth's scaled planet drawn with {shader.name} (lift {LiftMetres:0} m)");
                    }
                }
                if (m.shader == shader)
                    m.SetFloat(LiftId, lift);
            }
        }
    }
}
