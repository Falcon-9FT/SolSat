// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.IO;
using UnityEngine;

namespace SolSatellite
{
    /// <summary>
    /// Surface materials of the 3D roads and railways and of the buildings (CC0 textures from
    /// ambientCG in PluginData/Materials, where KSP's own loader doesn't look): colour and
    /// normal maps, each set packed into a texture array, compressed, with mipmaps. A layer
    /// index is a Kind value; Road3D.shader and Building.shader sample by it. Average holds
    /// each colour map's mean, which the building shader scales to the colour a wall should
    /// have, so one texture serves every colour of plaster or metal.
    /// </summary>
    static class Materials
    {
        public enum Kind
        {
            Asphalt = 0, Paving = 1, Concrete = 2, Ballast = 3, Steel = 4,
            Plaster = 5, Brick = 6, Glass = 7, Corrugated = 8, Wood = 9, ClayTiles = 10, Slate = 11,
        }

        static readonly string[] Folders =
        {
            "Asphalt015", "PavingStones136", "Concrete034", "Gravel040", "Metal038",
            "Plaster001", "Bricks086", "Facade001", "CorrugatedSteel009", "WoodSiding009", "RoofingTiles012A", "RoofingTiles001",
        };
        const int Size = 1024;
        public const int MaxLayers = 16; // Building.shader's array of averages

        static Texture2DArray colour, normal, plainColour, plainNormal;
        static readonly Vector4[] average = new Vector4[MaxLayers];
        static bool tried;

        public static Texture2DArray Colour => Load() ? colour : null;
        public static Texture2DArray Normal => Load() ? normal : null;
        public static Vector4[] Average
        {
            get
            {
                Load();
                return average;
            }
        }

        /// <summary>The arrays, or when they couldn't be loaded, plain grey stand-ins (buildings
        /// then just have their colours).</summary>
        public static Texture2DArray ColourOrPlain => Load() ? colour : Plain(false);
        public static Texture2DArray NormalOrPlain => Load() ? normal : Plain(true);

        static bool Load()
        {
            if (tried)
                return colour != null;
            tried = true;
            string dir = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "SolSatellite", "PluginData", "Materials");
            try
            {
                colour = Array(dir, "Color.jpg", false, average);
                normal = Array(dir, "Normal.jpg", true, null);
                Debug.Log($"[SolSatellite] Materials loaded: {Folders.Length} ({colour.format}, {normal.format})");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SolSatellite] Materials: {e.Message}");
                if (colour != null)
                    UnityEngine.Object.Destroy(colour);
                if (normal != null)
                    UnityEngine.Object.Destroy(normal);
                colour = normal = null;
                for (int i = 0; i < average.Length; i++)
                    average[i] = new Vector4(0.5f, 0.5f, 0.5f, 1f);
            }
            return colour != null;
        }

        /// <summary>One file from each material folder, as layers of a compressed array; the
        /// mean colour of each into means.</summary>
        static Texture2DArray Array(string dir, string file, bool linear, Vector4[] means)
        {
            Texture2DArray array = null;
            for (int i = 0; i < Folders.Length; i++)
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(dir, Folders[i], file));
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
                if (!tex.LoadImage(bytes))
                    throw new InvalidDataException($"{Folders[i]}/{file}: not an image");
                if (tex.width != Size || tex.height != Size)
                    throw new InvalidDataException($"{Folders[i]}/{file}: {tex.width}x{tex.height}, not {Size}");
                tex.Apply(true);
                if (means != null)
                {
                    // The last mip level is one pixel: the mean of the whole image.
                    Color32 c = tex.GetPixels32(tex.mipmapCount - 1)[0];
                    means[i] = new Vector4(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
                }
                tex.Compress(true); // DXT1 (JPEGs load without alpha)
                tex.Apply(false);
                if (array == null)
                {
                    array = new Texture2DArray(Size, Size, Folders.Length, tex.format, true, linear)
                    {
                        name = "SolSatellite materials " + Path.GetFileNameWithoutExtension(file),
                        wrapMode = TextureWrapMode.Repeat,
                        filterMode = FilterMode.Trilinear,
                        anisoLevel = 8,
                    };
                    array.Apply(false);
                }
                for (int mip = 0; mip < tex.mipmapCount; mip++)
                    Graphics.CopyTexture(tex, 0, mip, array, i, mip);
                UnityEngine.Object.Destroy(tex);
            }
            return array;
        }

        static Texture2DArray Plain(bool isNormal)
        {
            ref Texture2DArray plain = ref isNormal ? ref plainNormal : ref plainColour;
            if (plain != null)
                return plain;
            plain = new Texture2DArray(4, 4, Folders.Length, TextureFormat.RGBA32, false, isNormal) { name = "SolSatellite plain materials" };
            var pixels = new Color32[16];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = isNormal ? new Color32(128, 128, 255, 255) : new Color32(128, 128, 128, 255);
            for (int i = 0; i < Folders.Length; i++)
                plain.SetPixels32(pixels, i);
            plain.Apply(false, true);
            return plain;
        }
    }
}
