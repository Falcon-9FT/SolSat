// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

// Sharper ground seen from orbit (OrbitDetail.cs). Above ~140 km KSP draws Earth in scaled space
// from the orbital colour cube, which holds 0.6 km a texel at best (twice that with KSP's texture
// quality at half). A cap of mesh lying on the planet in local space is drawn over that and adds
// only the fine detail: the brightness of what is already on screen is scaled by how much the
// clipmap's imagery (up to 128 m a texel; render textures, which texture quality doesn't halve)
// differs from its own average over ~1.5 km, about what the cube holds. So the average colour and
// brightness stay Sol's - no tint, no visible square - and only on land (over the sea Sol draws
// its own ocean). The detail fades out towards the cap's edge, the limb, the night side and over
// bright clouds.
Shader "SolSatellite/OrbitDetail"
{
    SubShader
    {
        Tags { "Queue" = "Geometry+100" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        GrabPass { "_SolSatOrbitGrab" }

        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "UnityCG.cginc"
            #include "../Terrain/SolSatellite.cginc"

            sampler2D _SolSatOrbitGrab;
            float4 _SolSatPlanetCentre; // xyz: world position of the planet's centre
            float4 _SolSatSunDir;       // xyz: world direction to the sun
            float4 _SolSatOrbit;        // x: strength, y: texels per clipmap level
            float4 _SolSatCap;          // xyz: world position of the cap's centre, w: its half width (m)

            static const float AverageMetres = 1500.0;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                float4 grab : TEXCOORD1;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.grab = ComputeGrabScreenPos(o.pos);
                return o;
            }

            // The imagery averaged over AverageMetres: each level sampled at the mip that wide.
            float4 ClipAverage(float3 worldPos)
            {
                float2 delta = SolSatPlaneDelta(worldPos);
                float4 result = 0;
                [unroll]
                for (int i = 0; i < SOLSAT_LEVELS; i++)
                {
                    float4 level = _SolSatLevels[i];
                    float2 d = delta * level.z;
                    float2 edge = 0.5 - abs(level.xy + d);
                    float fade = saturate(min(edge.x, edge.y) * 16.0) * level.w;
                    if (fade > 0.0 && result.a < 0.999)
                    {
                        float lod = max(0.0, log2(AverageMetres * _SolSatOrbit.y * level.z));
                        float4 c = _SolSatClip.SampleLevel(sampler_SolSatClip, float3(frac(_SolSatLevelFrac[i].xy + d), i), lod);
                        float w = c.a * fade * (1.0 - result.a);
                        result.rgb += c.rgb * w;
                        result.a += w;
                    }
                }
                return result;
            }

            half4 frag(v2f i) : SV_Target
            {
                float4 fine = SolSatClipColor(i.world);
                if (fine.a < 0.01)
                    discard;
                float4 average = ClipAverage(i.world);
                half3 dst = tex2Dproj(_SolSatOrbitGrab, i.grab).rgb;

                float3 w = float3(0.30, 0.59, 0.11);
                float ratio = dot(fine.rgb / fine.a, w) / max(dot(average.rgb / max(average.a, 0.01), w), 0.03);
                ratio = clamp(ratio, 0.7, 1.4);

                float3 planetPos = mul(_SolSatWorldToPlanet, float4(i.world, 1.0)).xyz;
                float land = saturate(1.0 - texCUBE(_SolSatCube, planetPos).a * 1.05); // alpha: Sol's ocean mask
                float edge = saturate((_SolSatCap.w - distance(i.world, _SolSatCap.xyz)) / (0.45 * _SolSatCap.w));
                float3 up = normalize(i.world - _SolSatPlanetCentre.xyz);
                float3 toCamera = normalize(_WorldSpaceCameraPos - i.world);
                float view = saturate(dot(up, toCamera) * 4.0);                // fades towards the limb
                float day = saturate(dot(up, _SolSatSunDir.xyz) * 6.0 + 0.15);  // and into the night
                float luma = dot(dst, w);
                float clear = 1.0 - saturate((luma - 0.45) * 4.0);            // clouds and glare are bright
                float k = fine.a * average.a * land * edge * view * day * clear * _SolSatOrbit.x;
                return half4(dst * lerp(1.0, ratio, k), 1.0);
            }
            ENDCG
        }
    }
}
