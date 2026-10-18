// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace SolSatellite
{
    /// <summary>
    /// A texture array with the clipmap's levels and toroidal wrapping that the plugin draws
    /// meshes into: the night light map (pools of light and glowing streets, added and taken
    /// out again) and the road layer. Drawing is recorded into a command buffer and run at
    /// once by End; mip chains are rebuilt a few times a second at most (UpdateMips).
    /// </summary>
    sealed class GroundLayer : IDisposable
    {
        // TileProject passes.
        public const int PoolPass = 2, PoolErase = 3, RoadPass = 4, GlowPass = 5, GlowErase = 6;
        const float MipsInterval = 0.1f;

        static readonly int WindowId = Shader.PropertyToID("_Window");
        static readonly int OldWindowId = Shader.PropertyToID("_OldWindow");
        static readonly int PoolWindowId = Shader.PropertyToID("_PoolWindow");
        static readonly int LightLevelId = Shader.PropertyToID("_LightLevel");
        static readonly int LightOffsetId = Shader.PropertyToID("_LightOffset");

        public readonly RenderTexture Texture;
        public readonly int Size;
        readonly int mips;
        readonly int texelBytes;
        readonly Clipmap clipmap;
        readonly Material material;
        readonly Mesh quad;
        readonly CommandBuffer commands;
        readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        int target = -1;
        bool began, mipsDirty;
        float nextMips;

        public GroundLayer(string name, int size, Clipmap clipmap, Material material, Mesh quad,
                           RenderTextureFormat format = RenderTextureFormat.ARGB32)
        {
            Size = size;
            mips = Clipmap.MipCount(size);
            texelBytes = format == RenderTextureFormat.ARGBHalf ? 8 : 4;
            this.clipmap = clipmap;
            this.material = material;
            this.quad = quad;
            commands = new CommandBuffer { name = name };
            Texture = new RenderTexture(size, size, 0, format, RenderTextureReadWrite.Linear)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = Clipmap.MaxLevels,
                useMipMap = true,
                autoGenerateMips = false,
                wrapMode = TextureWrapMode.Repeat, // toroidal levels
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
                name = name,
            };
            Texture.Create();
        }

        /// <summary>Video memory it takes, with mips.</summary>
        public long Bytes => (long)Size * Size * Clipmap.MaxLevels * texelBytes * 4 / 3;

        /// <summary>For light that is added up and taken out again: floating point, so neither
        /// saturates at 1 (taking a tile's light out of a bright town then left it darker than
        /// before) nor rounds the faint light of far streets on wide levels away to nothing (8
        /// bits: wide levels lost it and showed squares where finer ones ended). 11/11/10-bit
        /// floats take no more memory than 8 bits a channel; half floats where those can't blend.</summary>
        public static RenderTextureFormat LightFormat()
        {
            foreach (RenderTextureFormat f in new[] { RenderTextureFormat.RGB111110Float, RenderTextureFormat.ARGBHalf })
                if (SystemInfo.SupportsRenderTextureFormat(f) && SystemInfo.SupportsBlendingOnRenderTextureFormat(f))
                    return f;
            return RenderTextureFormat.ARGB32;
        }

        /// <summary>How far, plane metres, the shader may move a vertex beyond a mesh's extent in
        /// level k: ribbons and pools are widened by a texel of the level, miters by up to two.
        /// Per level: the coarsest level's texel is kilometres, the finest level's centimetres.</summary>
        public double Pad(int k) => 2.0 * clipmap.LevelWidth(k) / Size;

        void Target(int k, int mip = 0)
        {
            if (!began)
            {
                commands.Clear();
                target = -1;
                began = true;
            }
            int id = k * 64 + mip;
            if (target == id)
                return;
            commands.SetRenderTarget(new RenderTargetIdentifier(Texture, mip, CubemapFace.Unknown, k));
            target = id;
        }

        /// <summary>Clears level k, every mip of it (the coarser mips would show what was there
        /// until the chain is rebuilt).</summary>
        public void ClearLevel(int k)
        {
            for (int m = 0; m < mips; m++)
            {
                Target(k, m);
                commands.ClearRenderTarget(false, true, Color.clear);
            }
        }

        /// <summary>After level k moved: clears what has new ground in the window (pass 1), in
        /// every mip - those texels hold what left the window on the other side.</summary>
        public void ClearExposed(int k, Vector4 oldWindow)
        {
            properties.SetVector(WindowId, clipmap.LevelWindow(k));
            properties.SetVector(OldWindowId, oldWindow);
            for (int m = 0; m < mips; m++)
            {
                Target(k, m);
                commands.DrawMesh(quad, Matrix4x4.identity, material, 0, 1, properties);
            }
        }

        /// <summary>Queues a mesh for level k with a TileProject pass, if it reaches area. The
        /// matrix takes mesh coordinates to plane metres; x0..y1 is the mesh's extent in plane
        /// metres (ribbon widths included; Pad is added here). It is drawn once per wrap of the
        /// toroidal level where it meets area (absolute level coordinates, at most a level wide):
        /// up to four times, however many level widths the mesh spans. Returns whether it was.</summary>
        public bool Draw(int k, Mesh mesh, Matrix4x4 matrix, double x0, double y0, double x1, double y1, Vector4 area, int pass)
        {
            double width = clipmap.LevelWidth(k), inv = 1.0 / width, pad = Pad(k);
            double ax0 = Math.Max((x0 - pad) * inv, area.x), ax1 = Math.Min((x1 + pad) * inv, area.z);
            double ay0 = Math.Max((y0 - pad) * inv, area.y), ay1 = Math.Min((y1 + pad) * inv, area.w);
            if (ax0 > ax1 || ay0 > ay1)
                return false;
            Target(k);
            for (int ox = (int)Math.Floor(ax0); ox <= (int)Math.Floor(ax1); ox++)
            {
                for (int oy = (int)Math.Floor(ay0); oy <= (int)Math.Floor(ay1); oy++)
                {
                    properties.SetVector(PoolWindowId, area);
                    properties.SetVector(LightLevelId, new Vector4((float)inv, (float)(width / Size), 0f, 0f));
                    properties.SetVector(LightOffsetId, new Vector4(ox, oy, 0f, 0f));
                    commands.DrawMesh(mesh, matrix, material, 0, pass, properties);
                }
            }
            return true;
        }

        /// <summary>Runs what was queued since the last End.</summary>
        public void End()
        {
            if (!began)
                return;
            Graphics.ExecuteCommandBuffer(commands);
            commands.Clear();
            began = false;
            target = -1;
            mipsDirty = true;
        }

        public void UpdateMips(float now)
        {
            if (!mipsDirty || now < nextMips)
                return;
            Texture.GenerateMips();
            mipsDirty = false;
            nextMips = now + MipsInterval;
        }

        public void Dispose()
        {
            commands.Release();
            Texture.Release();
            UnityEngine.Object.Destroy(Texture);
        }

        public static bool Intersect(Vector4 a, Vector4 b, out Vector4 r)
        {
            r = new Vector4(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y), Mathf.Min(a.z, b.z), Mathf.Min(a.w, b.w));
            return r.x < r.z && r.y < r.w;
        }

        /// <summary>The part of window w outside window old, as up to two rectangles.</summary>
        public static int Exposed(Vector4 old, Vector4 w, Vector4[] into)
        {
            int n = 0;
            if (w.x < old.x)
                into[n++] = new Vector4(w.x, w.y, old.x, w.w);
            else if (w.z > old.z)
                into[n++] = new Vector4(old.z, w.y, w.z, w.w);
            float x0 = Mathf.Max(w.x, old.x), x1 = Mathf.Min(w.z, old.z);
            if (w.y < old.y)
                into[n++] = new Vector4(x0, w.y, x1, old.y);
            else if (w.w > old.w)
                into[n++] = new Vector4(x0, old.w, x1, w.w);
            return n;
        }
    }
}
