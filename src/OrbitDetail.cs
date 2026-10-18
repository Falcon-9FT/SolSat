// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SolSatellite
{
    /// <summary>
    /// Sharper ground seen from orbit. Above Clipmap.OrbitAltitude KSP draws Earth in scaled
    /// space from the orbital colour cube; this keeps a cap of mesh lying on the planet in local
    /// space under the camera, over the clipmap's coarsest window, drawn with the OrbitDetail
    /// shader: what is on screen is multiplied by how much the clipmap's imagery differs from
    /// the cube there, so the planet keeps Sol's lighting, atmosphere and clouds and gains the
    /// detail the cube can't hold. The local camera draws it after scaled space and doesn't
    /// see the scaled planet's depth, so it lies on top wherever the vessel isn't in front.
    /// </summary>
    sealed class OrbitDetail : IDisposable
    {
        const int Grid = 64;             // quads per side of the cap
        const float Strength = 1f;
        const double FadeFrom = 140000.0, FadeTo = 160000.0; // in over these camera altitudes, above the terrain's own range

        static readonly int PlanetCentreId = Shader.PropertyToID("_SolSatPlanetCentre");
        static readonly int SunDirId = Shader.PropertyToID("_SolSatSunDir");
        static readonly int OrbitId = Shader.PropertyToID("_SolSatOrbit");
        static readonly int CubeId = Shader.PropertyToID("_SolSatCube");
        static readonly int WorldToPlanetId = Shader.PropertyToID("_SolSatWorldToPlanet");
        static readonly int CapId = Shader.PropertyToID("_SolSatCap");

        readonly CelestialBody body;
        readonly Material material;
        GameObject cap;
        Mesh mesh;
        int builtPlane = -1;
        double builtX = double.NaN, builtY = double.NaN, builtHalf;
        Vector3d capCentre; // PQS-local direction of the cap's centre

        public bool Shown { get; private set; }

        public OrbitDetail(CelestialBody body, Shader shader)
        {
            this.body = body;
            material = new Material(shader) { name = "SolSatellite orbit detail" };
        }

        /// <summary>Call after the clipmap's Update, with the camera's altitude, while the
        /// terrain camera renders (not in map view).</summary>
        public void Update(Clipmap clipmap, bool wanted)
        {
            bool on = wanted && clipmap != null && clipmap.Active && clipmap.CameraAltitude > FadeFrom &&
                      clipmap.CoarsestWindow(out double cx, out double cy, out double half);
            if (!on)
            {
                Hide();
                return;
            }
            clipmap.CoarsestWindow(out cx, out cy, out half);
            if (cap == null)
            {
                cap = new GameObject("SolSatellite orbit detail") { layer = 15 };
                cap.AddComponent<MeshFilter>();
                var renderer = cap.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            Transform parent = body.bodyTransform;
            if (cap.transform.parent != parent)
                cap.transform.SetParent(parent, false);
            if (builtPlane != clipmap.PlaneVersion || cx != builtX || cy != builtY || half != builtHalf)
            {
                Build(clipmap.Plane, cx, cy, half);
                capCentre = clipmap.Plane.Unproject(cx, cy);
            }
            builtPlane = clipmap.PlaneVersion;
            builtX = cx;
            builtY = cy;
            builtHalf = half;
            cap.SetActive(true);
            Shown = true;
        }

        /// <summary>A grid over the window on the clipmap's plane, lifted onto the sphere at sea
        /// level, in the body's frame so it turns with the planet.</summary>
        void Build(TangentPlane plane, double cx, double cy, double half)
        {
            var vertices = new List<Vector3>((Grid + 1) * (Grid + 1));
            var normals = new List<Vector3>((Grid + 1) * (Grid + 1));
            var triangles = new List<int>(Grid * Grid * 6);
            Transform pqs = body.pqsController.transform, parent = body.bodyTransform;
            for (int j = 0; j <= Grid; j++)
            {
                for (int i = 0; i <= Grid; i++)
                {
                    double x = cx - half + 2.0 * half * i / Grid, y = cy - half + 2.0 * half * j / Grid;
                    Vector3d dir = plane.Unproject(x, y);
                    Vector3 world = pqs.TransformPoint((Vector3)(dir * body.Radius));
                    vertices.Add(parent.InverseTransformPoint(world));
                    normals.Add(parent.InverseTransformDirection(pqs.TransformDirection((Vector3)dir)));
                }
            }
            for (int j = 0; j < Grid; j++)
            {
                for (int i = 0; i < Grid; i++)
                {
                    int a = j * (Grid + 1) + i, b = a + 1, c = a + Grid + 1, d = c + 1;
                    // Seen from outside: x east, y north on the plane, so (a, c, b) winds clockwise from above.
                    triangles.Add(a);
                    triangles.Add(c);
                    triangles.Add(b);
                    triangles.Add(b);
                    triangles.Add(c);
                    triangles.Add(d);
                }
            }
            if (mesh == null)
                mesh = new Mesh { name = "SolSatellite orbit detail" };
            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            cap.GetComponent<MeshFilter>().sharedMesh = mesh;
        }

        /// <summary>Per-frame values; call right before rendering, after the clipmap's shader
        /// parameters are ready.</summary>
        public void UpdateMaterial(Clipmap clipmap, Texture cube, Matrix4x4 worldToPlanet)
        {
            if (!Shown)
                return;
            clipmap.Apply(material);
            if (cube != null)
                material.SetTexture(CubeId, cube);
            material.SetMatrix(WorldToPlanetId, worldToPlanet);
            Vector3d centre = body.position;
            material.SetVector(PlanetCentreId, new Vector4((float)centre.x, (float)centre.y, (float)centre.z, 0f));
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            Vector3d toSun = sun != null ? (sun.position - centre).normalized : Vector3d.up;
            material.SetVector(SunDirId, new Vector4((float)toSun.x, (float)toSun.y, (float)toSun.z, 0f));
            float fade = Mathf.Clamp01((float)((clipmap.CameraAltitude - FadeFrom) / (FadeTo - FadeFrom)));
            material.SetVector(OrbitId, new Vector4(cube != null ? Strength * fade : 0f, clipmap.LevelTexels, 0f, 0f));
            Vector3 capWorld = body.pqsController.transform.TransformPoint((Vector3)(capCentre * body.Radius));
            material.SetVector(CapId, new Vector4(capWorld.x, capWorld.y, capWorld.z, (float)builtHalf));
        }

        public void Hide()
        {
            if (cap != null && cap.activeSelf)
                cap.SetActive(false);
            Shown = false;
        }

        public void Dispose()
        {
            if (cap != null)
                UnityEngine.Object.Destroy(cap);
            if (mesh != null)
                UnityEngine.Object.Destroy(mesh);
            UnityEngine.Object.Destroy(material);
        }
    }
}
