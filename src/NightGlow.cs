// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using UnityEngine;

namespace SolSatellite
{
    /// <summary>
    /// Sol lights towns at night through EVE: on the ground a repeating pattern of lights,
    /// masked by a 2.4 km map of where towns are. The pattern doesn't match real streets, so
    /// while SolSatellite lights the real ones (lamps on, camera below 40 km) EVE's detail
    /// texture on the ground is swapped for a smooth warm glow, dimmer the lower the camera,
    /// or for nothing ("off"). Higher up, and in "sol" mode, EVE's own texture is back. EVE's
    /// city lights in scaled space (seen from orbit) are never touched.
    /// </summary>
    static class EveCityLights
    {
        const string ShaderName = "EVE/TerrainCityLight";
        const string DetailProperty = "_CityDarkOverlayDetailTex";
        const double OwnLightsAltitude = 40000.0;

        static Material material;
        static Texture original;
        static Texture2D glow;
        static float applied = -1f; // alpha of the glow now in place; -1: EVE's own texture
        static float nextSearch, searchInterval = 15f;

        public static void Update(Settings settings, double cameraAltitude)
        {
            if (material == null)
            {
                // EVE makes the material when it applies its configs; it may also remake it.
                applied = -1f;
                if (Time.realtimeSinceStartup < nextSearch)
                    return;
                // Going through every material in the game is a hitch: less and less often.
                nextSearch = Time.realtimeSinceStartup + searchInterval;
                searchInterval = Mathf.Min(searchInterval * 2f, 120f);
                material = Find();
                if (material == null)
                    return;
                searchInterval = 15f;
                original = material.GetTexture(DetailProperty);
                Debug.Log("[SolSatellite] Found EVE's city lights material");
            }

            if (settings.EveCityLights == Settings.EveCityLightsMode.Sol || !settings.LampsEnabled || cameraAltitude > OwnLightsAltitude)
            {
                Restore();
                return;
            }
            float alpha = settings.EveCityLights == Settings.EveCityLightsMode.Off
                ? 0f
                : Mathf.Lerp(0.1f, 0.45f, Mathf.InverseLerp(2000f, (float)OwnLightsAltitude, (float)cameraAltitude)); // low: the lit streets show
            if (Mathf.Abs(alpha - applied) < 0.02f)
                return;
            if (glow == null)
                glow = new Texture2D(1, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, name = "SolSatellite town glow" };
            glow.SetPixel(0, 0, new Color(1f, 0.78f, 0.5f, alpha));
            glow.Apply(false);
            material.SetTexture(DetailProperty, glow);
            applied = alpha;
        }

        public static void Restore()
        {
            if (material != null && applied >= 0f)
                material.SetTexture(DetailProperty, original);
            applied = -1f;
        }

        static Material Find()
        {
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
                if (m != null && m.shader != null && m.shader.name == ShaderName)
                    return m;
            return null;
        }
    }
}
