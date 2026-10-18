// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;

public static class SolSatBuild
{
    // Parallax loads "<path>-windows.unity3d" and takes every shader in it by name, so the
    // replacement bundle must contain all four of its terrain shaders.
    static readonly string[] TerrainShaders =
    {
        "Assets/Shaders/Terrain/Parallax.shader",
        "Assets/Shaders/Terrain/DiscardAll.shader",
        "Assets/Shaders/Terrain/KSCTerrain.shader",
        "Assets/Shaders/Terrain/Wireframe.shader",
    };

    // SolSatellite's own shaders, loaded by the plugin.
    static readonly string[] OwnShaders =
    {
        "Assets/Shaders/SolSatellite/TileProject.shader",
        "Assets/Shaders/SolSatellite/Building.shader",
        "Assets/Shaders/SolSatellite/Lamp.shader",
        "Assets/Shaders/SolSatellite/OrbitDetail.shader",
        "Assets/Shaders/SolSatellite/HeightCapture.shader",
        "Assets/Shaders/SolSatellite/Road3D.shader",
        "Assets/Shaders/SolSatellite/PlanetCityLight.shader", // EVE's city lights from orbit over Parallax's raised land
        // Parallax's scatter distribution with SolSatellite's scatter mask (Earth only).
        "Assets/Shaders/Scatters/TerrainScatters.compute",
    };

    /// <summary>Both bundles: Parallax's terrain shaders (slow) and SolSatellite's own.</summary>
    public static void BuildTerrainBundle()
    {
        Build(
            new AssetBundleBuild { assetBundleName = "parallaxterrain-windows.unity3d", assetNames = TerrainShaders },
            new AssetBundleBuild { assetBundleName = "solsatellite-windows.unity3d", assetNames = OwnShaders });
    }

    /// <summary>Only SolSatellite's own bundle (quick).</summary>
    public static void BuildOwnBundle()
    {
        Build(new AssetBundleBuild { assetBundleName = "solsatellite-windows.unity3d", assetNames = OwnShaders });
    }

    static void Build(params AssetBundleBuild[] bundles)
    {
        KeepFogVariants();
        bool ok = true;
        foreach (AssetBundleBuild bundle in bundles)
            foreach (string path in bundle.assetNames)
                ok &= Check(path);
        const string outDir = "Build";
        Directory.CreateDirectory(outDir);
        var manifest = BuildPipeline.BuildAssetBundles(
            outDir,
            bundles,
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
            BuildTarget.StandaloneWindows64);
        EditorApplication.Exit(manifest == null || !ok ? 1 : 0);
    }

    /// <summary>Logs a shader's compile messages and variant count; false on errors. Unity would
    /// otherwise put a broken shader into the bundle silently. The ShaderUtil calls are internal
    /// in this Unity version, hence reflection.</summary>
    static bool Check(string path)
    {
        if (path.EndsWith(".compute", StringComparison.OrdinalIgnoreCase))
            return CheckCompute(path);
        var shader = AssetDatabase.LoadAssetAtPath<UnityEngine.Shader>(path);
        if (shader == null)
        {
            UnityEngine.Debug.LogError($"[SolSatBuild] {path}: not found");
            return false;
        }
        const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        Type util = typeof(ShaderUtil);
        bool errors = (bool?)util.GetMethod("ShaderHasError", any, null, new[] { typeof(UnityEngine.Shader) }, null)?.Invoke(null, new object[] { shader }) ?? false;
        object variants;
        try
        {
            variants = util.GetMethod("GetVariantCount", any)?.Invoke(null, new object[] { shader, false });
        }
        catch (Exception e)
        {
            variants = "? (" + e.GetBaseException().Message + ")";
        }
        var sb = new StringBuilder($"[SolSatBuild] {path}: {(errors ? "ERRORS" : "compiles")}, variants {variants}");
        if (util.GetMethod("GetShaderMessages", any, null, new[] { typeof(UnityEngine.Shader) }, null)?.Invoke(null, new object[] { shader }) is Array messages)
            foreach (object m in messages)
            {
                sb.Append("\n   ");
                const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (FieldInfo f in m.GetType().GetFields(members))
                    sb.Append(' ').Append(f.Name).Append('=').Append(f.GetValue(m));
                foreach (PropertyInfo p in m.GetType().GetProperties(members))
                    sb.Append(' ').Append(p.Name).Append('=').Append(p.GetValue(m));
            }
        UnityEngine.Debug.Log(sb.ToString());
        return !errors;
    }

    /// <summary>A compute shader's compile messages; false on errors.</summary>
    static bool CheckCompute(string path)
    {
        var shader = AssetDatabase.LoadAssetAtPath<UnityEngine.ComputeShader>(path);
        if (shader == null)
        {
            UnityEngine.Debug.LogError($"[SolSatBuild] {path}: not found");
            return false;
        }
        const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        Type util = typeof(ShaderUtil);
        bool errors = false;
        var sb = new StringBuilder();
        MethodInfo count = util.GetMethod("GetComputeShaderMessageCount", any, null, new[] { typeof(UnityEngine.ComputeShader) }, null);
        MethodInfo message = util.GetMethod("GetComputeShaderMessage", any, null, new[] { typeof(UnityEngine.ComputeShader), typeof(int) }, null);
        if (count != null && message != null)
        {
            int n = (int)count.Invoke(null, new object[] { shader });
            for (int i = 0; i < n; i++)
            {
                object m = message.Invoke(null, new object[] { shader, i });
                const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                sb.Append("\n   ");
                foreach (FieldInfo f in m.GetType().GetFields(members))
                {
                    object v = f.GetValue(m);
                    sb.Append(' ').Append(f.Name).Append('=').Append(v);
                    if (f.Name.IndexOf("severity", StringComparison.OrdinalIgnoreCase) >= 0 && v != null && v.ToString().IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0)
                        errors = true;
                }
            }
        }
        int kernels = 0;
        for (int k = 0; k < 64; k++)
        {
            try
            {
                shader.GetKernelThreadGroupSizes(k, out uint x, out uint y, out uint z);
                kernels++;
            }
            catch (Exception)
            {
                break;
            }
        }
        // (kernels can only be counted with a graphics device: 0 under -nographics)
        UnityEngine.Debug.Log($"[SolSatBuild] {path}: {(errors ? "ERRORS" : "compiles")}, kernels {kernels}{sb}");
        return !errors;
    }

    // With automatic stripping and no scenes in the project Unity drops the FOG_* variants of
    // multi_compile_fog; keep them all, as the original Parallax bundle has no such gaps.
    static void KeepFogVariants()
    {
        var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
        settings.FindProperty("m_FogStripping").intValue = 1; // custom
        settings.FindProperty("m_FogKeepLinear").boolValue = true;
        settings.FindProperty("m_FogKeepExp").boolValue = true;
        settings.FindProperty("m_FogKeepExp2").boolValue = true;
        settings.ApplyModifiedProperties();
    }
}
