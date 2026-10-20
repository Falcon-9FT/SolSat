// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace SolSatellite
{
    /// <summary>
    /// The terrain as it is drawn right now, around the camera: an orthographic camera high
    /// above looks straight down and renders only the planet's terrain (shaders tagged
    /// "SolSatHeight", our Parallax build) with HeightCapture, which writes how far below it
    /// the ground is. Shaders of things that lie on the ground (3D roads) read that to put
    /// their vertices exactly on it - the PQS mesh between its vertices, as drawn, which the
    /// PQS's own height function doesn't give - wherever the camera is, at no cost to the CPU.
    /// Drawn again when the camera has moved on and every few seconds (the terrain's mesh gets
    /// finer as the camera comes near). Update decides, the capture itself is made as the player's
    /// camera starts rendering (RenderPending): made in LateUpdate, before the frame's rendering,
    /// it was the frame's first render and took on the whole wait for the graphics card's last
    /// frame - 20 to 210 ms in a city by day (2026-10-03).
    /// </summary>
    sealed class HeightField : IDisposable
    {
        const int Size = 2048;
        const double Above = 20000.0;     // the camera this far above sea level
        const double Below = 12000.0;     // ... and sees this far below it
        const float Refresh = 3f;         // seconds between captures
        const string ReplacementTag = "SolSatHeight";

        static readonly int MapId = Shader.PropertyToID("_SolSatHeightMap");
        static readonly int FrameId = Shader.PropertyToID("_SolSatHeightFrame");
        static readonly int ParamsId = Shader.PropertyToID("_SolSatHeightParams");
        static readonly LightEvent[] LightEvents = (LightEvent[])Enum.GetValues(typeof(LightEvent));

        readonly CelestialBody body;
        readonly Shader shader;
        RenderTexture map;
        Camera cam;
        GameObject holder;
        readonly HashSet<MonoBehaviour> detached = new HashSet<MonoBehaviour>(); // other mods' scripts on the camera, switched off
        Vector3d centre;         // PQS-local unit direction the capture is centred on
        Vector3d east, north;    // ... and its axes there
        double half;             // half the width captured, metres
        float nextCapture;
        bool captured;
        bool pending;              // a capture asked for by Update, made by RenderPending
        Vector3d pendingCentre;
        double pendingHalf;

        public bool Ready => captured;

        public HeightField(CelestialBody body, Shader shader)
        {
            this.body = body;
            this.shader = shader;
        }

        /// <summary>Captures again if the camera has moved too far from the middle, or the
        /// time has come. reach: how far from the camera the ground is needed (m).</summary>
        public void Update(Focus camera, double reach, bool wanted)
        {
            // 3D roads are built up to 6 km (BuildingManager): higher up a capture - a render of
            // the terrain, tens of milliseconds - was made every few seconds for nothing.
            if (!wanted || !camera.Valid || body.pqsController == null || camera.Alt > 8000.0)
            {
                captured = false;
                pending = false;
                return;
            }
            Vector3d here = Geo.Direction(camera.Lat, camera.Lon);
            double wantedHalf = Math.Ceiling((reach + 800.0) / 500.0) * 500.0;
            double moved = captured ? Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(here, centre)))) * body.Radius : double.MaxValue;
            float now = Time.realtimeSinceStartup;
            if (pending || captured && moved < (half - reach) * 0.8 && now < nextCapture && wantedHalf == half)
                return;
            pending = true;
            pendingCentre = here;
            pendingHalf = wantedHalf;
            nextCapture = now + Refresh;
        }

        /// <summary>Makes the capture Update asked for, if any: call as the player's camera starts
        /// rendering (its pre-cull), before Apply.</summary>
        public void RenderPending()
        {
            if (!pending)
                return;
            pending = false;
            if (body.pqsController != null)
                Capture(pendingCentre, pendingHalf);
        }

        void Capture(Vector3d dir, double newHalf)
        {
            if (map == null)
            {
                map = new RenderTexture(Size, Size, 24, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
                {
                    name = "SolSatellite height field",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    autoGenerateMips = false,
                };
                map.Create();
                holder = new GameObject("SolSatellite height camera");
                UnityEngine.Object.DontDestroyOnLoad(holder);
                cam = holder.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.aspect = 1f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color((float)(Above + Below), 0f, 0f, 0f); // "no ground here"
                cam.cullingMask = 1 << 15;
                cam.renderingPath = RenderingPath.Forward;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.useOcclusionCulling = false;
                cam.depthTextureMode = DepthTextureMode.None;
                cam.targetTexture = map;
            }
            centre = dir;
            half = newHalf;
            var plane = new TangentPlane(Math.Asin(Math.Max(-1.0, Math.Min(1.0, dir.y))) * 180.0 / Math.PI, Math.Atan2(dir.z, dir.x) * 180.0 / Math.PI, body.Radius);
            east = plane.East;
            north = plane.North;
            Place();
            cam.orthographicSize = (float)half;
            cam.nearClipPlane = 1f;
            cam.farClipPlane = (float)(Above + Below);
            // No shadows for it: in forward rendering Unity draws the sun's shadow cascades for
            // every camera - here all the terrain and buildings again, for an image of heights
            // (~20 ms every 3 s, 2026-10-03).
            float shadows = QualitySettings.shadowDistance;
            QualitySettings.shadowDistance = 0f;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                cam.RenderWithShader(shader, ReplacementTag);
            }
            finally
            {
                QualitySettings.shadowDistance = shadows;
                DetachOthers();
            }
            Perf.Step(ref start, "terrain height capture under the 3D roads");
            captured = true;
        }

        /// <summary>Other mods hook every camera that draws their objects. EVE's volumetric cloud
        /// shadows (Atmosphere.ScreenSpaceShadowsManager.OnWillRenderObject) put a renderer on this
        /// camera whose OnPreCull adds command buffers to the camera and to the sun, and whose
        /// OnPostRender takes them off only when the camera's stereoActiveEye isn't Left - which it
        /// is for a camera rendered by hand. Every capture left three more behind, run for each
        /// camera with shadows every frame (2026-10-20, clean Sol: 166 on this camera and 323 on the
        /// sun after 10 minutes of flying, the frame 6 -> 45 ms). So whatever another mod puts on
        /// this camera is switched off once it shows up, and its command buffers come off this camera
        /// and the lights (each is its own: EVE makes them per camera).</summary>
        void DetachOthers()
        {
            cam.RemoveAllCommandBuffers(); // none are ours
            foreach (MonoBehaviour b in holder.GetComponents<MonoBehaviour>())
            {
                if (b == null || !detached.Add(b))
                    continue;
                b.enabled = false;
                int before = 0, after = 0;
                Light[] lights = UnityEngine.Object.FindObjectsOfType<Light>();
                foreach (Light light in lights)
                    foreach (LightEvent e in LightEvents)
                        before += light.GetCommandBuffers(e).Length;
                foreach (FieldInfo f in b.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!typeof(CommandBuffer).IsAssignableFrom(f.FieldType) || !(f.GetValue(b) is CommandBuffer buffer))
                        continue;
                    foreach (Light light in lights)
                        foreach (LightEvent e in LightEvents)
                            light.RemoveCommandBuffer(e, buffer);
                }
                foreach (Light light in lights)
                    foreach (LightEvent e in LightEvents)
                        after += light.GetCommandBuffers(e).Length;
                Debug.Log($"[SolSatellite] Height camera: switched off {b.GetType().FullName} ({b.GetType().Assembly.GetName().Name}) " +
                          $"that another mod put on it; command buffers on the lights {before} -> {after}");
            }
        }

        /// <summary>The camera above the centre, looking down, north up on the image - in this
        /// frame's world (KSP moves the world's origin about).</summary>
        void Place()
        {
            Transform pqs = body.pqsController.transform;
            Vector3d upWorld = pqs.TransformDirection((Vector3)centre);
            Vector3d northWorld = pqs.TransformDirection((Vector3)north);
            Vector3d position = body.position + upWorld.normalized * (body.Radius + Above);
            holder.transform.position = (Vector3)position;
            holder.transform.rotation = Quaternion.LookRotation(-(Vector3)upWorld, (Vector3)northWorld);
        }

        /// <summary>Shader values for this frame (globals): the map, and the matrix from world
        /// space to the capture's (x east, y north in metres from its middle, z depth below
        /// the camera).</summary>
        public void Apply()
        {
            if (!captured || map == null)
            {
                Shader.SetGlobalVector(ParamsId, Vector4.zero);
                return;
            }
            Transform pqs = body.pqsController.transform;
            Vector3d upWorld = ((Vector3d)pqs.TransformDirection((Vector3)centre)).normalized;
            Vector3d eastWorld = ((Vector3d)pqs.TransformDirection((Vector3)east)).normalized;
            Vector3d northWorld = ((Vector3d)pqs.TransformDirection((Vector3)north)).normalized;
            Vector3d camWorld = body.position + upWorld * (body.Radius + Above);
            var m = Matrix4x4.identity;
            SetRow(ref m, 0, eastWorld, camWorld);
            SetRow(ref m, 1, northWorld, camWorld);
            SetRow(ref m, 2, -upWorld, camWorld);
            Shader.SetGlobalTexture(MapId, map);
            Shader.SetGlobalMatrix(FrameId, m);
            Shader.SetGlobalVector(ParamsId, new Vector4((float)(0.5 / half), (float)(Above + Below - 1.0), 1f, (float)(2.0 * half / Size)));
        }

        static void SetRow(ref Matrix4x4 m, int row, Vector3d axis, Vector3d origin)
        {
            m[row, 0] = (float)axis.x;
            m[row, 1] = (float)axis.y;
            m[row, 2] = (float)axis.z;
            m[row, 3] = (float)-Vector3d.Dot(axis, origin);
        }

        public void Dispose()
        {
            if (holder != null)
                UnityEngine.Object.Destroy(holder);
            if (map != null)
            {
                map.Release();
                UnityEngine.Object.Destroy(map);
            }
        }
    }
}
