// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

// 3D roads and railways near the camera (Road3DMesher.cs). Each vertex is put on the terrain
// as it is drawn - the height field the plugin captures from above (HeightField.cs) - at its
// own height above it (uv1.x); bridge decks run between the ground at their ends instead,
// lifted over what they cross (and the ramps up to them by as much as they are lifted). Where no height field covers a vertex, the mesh's own position
// (from the tile's height grid) stays. Top faces take the terrain's slope for their normal.
// Surfaces are CC0 materials from PluginData/Materials (Materials.cs, a texture array); the
// lines on asphalt are worked out per pixel from the marking set (uv3), so they stay crisp
// close up and settle to their average far off; sleepers are drawn on the ballast bed's top.
// At night the street lamps' light (the terrain's light map) falls on them. Near the edge of
// the area they are built in, they dissolve with a dither (the road layer goes on beyond).
Shader "SolSatellite/Road3D"
{
    Properties
    {
        _Paint ("White paint", Color) = (0.86, 0.86, 0.84, 1)
        _Yellow ("Yellow paint", Color) = (0.86, 0.66, 0.18, 1)
        _Sleeper ("Sleepers", Color) = (0.62, 0.60, 0.57, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+1" }

        CGPROGRAM
        #pragma surface surf Lambert vertex:vert addshadow
        #pragma target 4.0
        #pragma require 2darray
        #include "../Terrain/SolSatellite.cginc"

        UNITY_DECLARE_TEX2DARRAY(_SolSatRoadColour);
        UNITY_DECLARE_TEX2DARRAY(_SolSatRoadNormal);
        sampler2D _SolSatHeightMap;
        float4x4 _SolSatHeightFrame;   // world -> height field: x east, y north (m from its middle), z depth below its camera
        float4 _SolSatHeightParams;    // x: 0.5 / half width, y: depth meaning "no ground", z: 1 when captured, w: texel (m)
        float4 _SolSatRoad3D;          // x: radius built in (m), y: 1 / fade width (m)
        float4 _SolSatCamUp;           // world-space up at the camera
        float4 _SolSatFocus;           // xyz: world position of the vessel, w: 1 if there is one
        fixed4 _Paint, _Yellow, _Sleeper;

        struct Input
        {
            float3 worldPos;
            float4 screenPos;
            float4 color : COLOR;
            float4 tex;    // uv0: texture u, v (m), material, markings on
            float4 marks;  // uv3: marking set
        };

        #if !defined(SHADER_TARGET_SURFACE_ANALYSIS)
        // Depth of the ground below the height field's camera at a world position's place
        // (its own depth in hz); inside: the field covers it and saw ground there.
        float GroundDepth(float3 world, out float hz, out bool inside)
        {
            float3 h = mul(_SolSatHeightFrame, float4(world, 1.0)).xyz;
            float2 uv = 0.5 + h.xy * _SolSatHeightParams.x;
            hz = h.z;
            float d = tex2Dlod(_SolSatHeightMap, float4(uv, 0.0, 0.0)).r;
            inside = all(uv > 0.001) && all(uv < 0.999) && d < _SolSatHeightParams.y;
            return d;
        }
        #endif

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.tex = v.texcoord;
            o.marks = v.texcoord3;
            #if !defined(SHADER_TARGET_SURFACE_ANALYSIS)
            if (_SolSatHeightParams.z < 0.5)
                return;
            float3 world = mul(unity_ObjectToWorld, v.vertex).xyz;
            float3 down = _SolSatHeightFrame[2].xyz;
            float hz;
            bool inside;
            float ground = GroundDepth(world, hz, inside);
            if (!inside)
                return;
            // Meshes are made at real size and their object scaled to the planet: heights too.
            float scale = length(unity_ObjectToWorld._m00_m10_m20);
            float offset = v.texcoord1.x * scale;
            if (v.texcoord1.y > 0.5)
            {
                // A bridge deck: on the line between the ground at its ends, lifted, never
                // below the ground under it.
                float3 a = mul(unity_ObjectToWorld, float4(v.texcoord2.x, 0.0, v.texcoord2.y, 1.0)).xyz;
                float3 b = mul(unity_ObjectToWorld, float4(v.texcoord2.z, 0.0, v.texcoord2.w, 1.0)).xyz;
                float hza, hzb;
                bool ia, ib;
                float da = GroundDepth(a, hza, ia), db = GroundDepth(b, hzb, ib);
                if (!ia)
                    da = ib ? db : ground;
                if (!ib)
                    db = ia ? da : ground;
                float t = saturate(v.texcoord1.z);
                float deck = lerp(da, db, t) - v.texcoord1.w * scale;
                ground = min(deck, ground);
            }
            // The mesh's own position comes from the tile's height grid, metres off the drawn
            // terrain at most: anything much bigger is a bad sample (the field's edge, where it
            // saw no terrain), and the vertex stays where it is.
            float shift = (ground - offset) - hz;
            if (abs(shift) > 80.0)
                return;
            world += down * shift;
            v.vertex.xyz = mul(unity_WorldToObject, float4(world, 1.0)).xyz;

            // Top faces lying on the ground: the terrain's slope there.
            if (v.color.a > 0.5 && v.texcoord1.y < 0.5)
            {
                float step = _SolSatHeightParams.w;
                float3 e = _SolSatHeightFrame[0].xyz, n = _SolSatHeightFrame[1].xyz;
                float h2;
                bool i2;
                float dx = GroundDepth(world + e * step, h2, i2) - GroundDepth(world - e * step, h2, i2);
                float dy = GroundDepth(world + n * step, h2, i2) - GroundDepth(world - n * step, h2, i2);
                float3 normal = normalize(-down * (2.0 * step) + e * dx + n * dy);
                v.normal = normalize(mul((float3x3)unity_WorldToObject, normal));
            }
            #endif
        }

        #if !defined(SHADER_TARGET_SURFACE_ANALYSIS)
        float Bayer(float2 pixel)
        {
            float2 p = fmod(floor(pixel), 4.0);
            float2 lo = fmod(p, 2.0), hi = floor(p * 0.5);
            float m = 4.0 * fmod(2.0 * lo.x + 3.0 * lo.y, 4.0) + fmod(2.0 * hi.x + 3.0 * hi.y, 4.0);
            return (m + 0.5) / 16.0;
        }

        // Coverage of a line (half width w) at x, centred at c, a pixel being aa wide.
        float Line(float x, float c, float w, float aa)
        {
            return saturate((w - abs(x - c)) / aa + 0.5);
        }

        // Dashes along a line: on for `dash`, off for `gap`; far off, their average.
        float Dashes(float along, float dash, float gap, float aa)
        {
            float period = dash + gap;
            float p = frac(along / period) * period;
            float on = saturate(p / aa + 0.5) * saturate((dash - p) / aa + 0.5);
            return lerp(on, dash / period, saturate(aa / period * 2.0 - 0.5));
        }

        void Markings(float u, float along, float4 marks, float aa, float aaAlong, inout float white, inout float yellow)
        {
            float halfPaint = marks.x, lane = marks.y, lanes = marks.z;
            int style = (int)(marks.w + 0.5);
            bool americas = (style & 16) != 0;
            const float w = 0.08;
            float dashed = americas ? Dashes(along, 3.0, 9.0, aaAlong) : Dashes(along, 4.0, 8.0, aaAlong);
            if ((style & 1) != 0)
            {
                float left = Line(u, -halfPaint, w, aa);
                if ((style & 32) != 0)
                    yellow = max(yellow, left);
                else
                    white = max(white, left);
                white = max(white, Line(u, halfPaint, w, aa));
            }
            if ((style & 2) != 0)
            {
                // One way: lane lines from the left edge.
                [loop]
                for (int k = 1; k < 8; k++)
                {
                    if (k >= lanes)
                        break;
                    white = max(white, Line(u, -halfPaint + k * lane, w, aa) * dashed);
                }
                return;
            }
            float centre = 0.0;
            if ((style & 4) != 0)
                centre = americas ? max(Line(u, -0.12, w, aa), Line(u, 0.12, w, aa)) : Line(u, 0.0, w, aa);
            else if ((style & 8) != 0)
                centre = Line(u, 0.0, w, aa) * dashed;
            if (americas)
                yellow = max(yellow, centre);
            else
                white = max(white, centre);
            [loop]
            for (int j = 1; j < 4; j++)
            {
                if (j >= lanes * 0.5)
                    break;
                white = max(white, max(Line(u, -j * lane, w, aa), Line(u, j * lane, w, aa)) * dashed);
            }
        }
        #endif

        void surf(Input IN, inout SurfaceOutput o)
        {
            #if defined(SHADER_TARGET_SURFACE_ANALYSIS)
            // The analysis pass only needs to see which inputs are used.
            o.Albedo = IN.color.rgb * IN.tex.xyz * IN.marks.xyz * IN.worldPos * IN.screenPos.xyz;
            o.Normal = float3(0.0, 0.0, 1.0);
            o.Emission = 0;
            #else
            // The area they are built in: around the camera, and around the vessel.
            float3 v = IN.worldPos - _WorldSpaceCameraPos;
            float3 up = _SolSatCamUp.xyz;
            float dist = length(v - dot(v, up) * up);
            if (_SolSatFocus.w > 0.5)
            {
                float3 f = IN.worldPos - _SolSatFocus.xyz;
                dist = min(dist, length(f - dot(f, up) * up));
            }
            float fade = saturate((_SolSatRoad3D.x - dist) * _SolSatRoad3D.y);
            float2 pixel = IN.screenPos.xy / max(IN.screenPos.w, 1e-5) * _ScreenParams.xy;
            clip(fade - Bayer(pixel));

            // What needs screen-space derivatives first, while every pixel runs the same code.
            float3 street = _SolSatParams.z > 0.5 ? SolSatNightLight(IN.worldPos) : 0;
            float2 metres = IN.tex.xy;
            float aaU = max(fwidth(metres.x), 1e-4), aaV = max(fwidth(metres.y), 1e-4);

            float material = round(IN.tex.z);
            // Metres per texture repeat: asphalt, paving, concrete, ballast, steel.
            float repeat = material < 0.5 ? 2.5 : material < 1.5 ? 1.5 : material < 2.5 ? 2.0 : material < 3.5 ? 1.2 : 1.0;
            float sleeper = 0.0;
            if (material > 2.5 && material < 3.5 && IN.tex.w > 0.5)
            {
                // Sleepers on the bed's top: 2.6 m long, 0.25 m wide, every 0.6 m.
                float across = Line(metres.x, 0.0, 1.3, aaU);
                float a = frac(metres.y / 0.6) * 0.6;
                float along = saturate(a / aaV + 0.5) * saturate((0.25 - a) / aaV + 0.5);
                sleeper = across * lerp(along, 0.25 / 0.6, saturate(aaV / 0.6 * 2.0 - 0.5));
            }
            float3 uvw = float3(metres / repeat, material);
            fixed3 albedo = UNITY_SAMPLE_TEX2DARRAY(_SolSatRoadColour, uvw).rgb * IN.color.rgb;
            if (material > 0.5 && material < 1.5)
                albedo = lerp(albedo, dot(albedo, fixed3(0.3, 0.59, 0.11)) * fixed3(0.96, 0.97, 1.0), 0.65); // grey paving
            fixed3 normal = UNITY_SAMPLE_TEX2DARRAY(_SolSatRoadNormal, uvw).rgb * 2.0 - 1.0;
            fixed3 concrete = UNITY_SAMPLE_TEX2DARRAY(_SolSatRoadColour, float3(metres / 2.0, 2.0)).rgb;
            albedo = lerp(albedo, concrete * _Sleeper.rgb, sleeper);

            float white = 0.0, yellow = 0.0;
            if (material < 0.5 || (material > 1.5 && material < 2.5))
            {
                if (IN.tex.w > 0.5 && IN.marks.x > 0.0)
                    Markings(metres.x, metres.y, IN.marks, aaU, aaV, white, yellow);
            }
            albedo = lerp(albedo, _Paint.rgb, saturate(white));
            albedo = lerp(albedo, _Yellow.rgb, saturate(yellow));
            normal = lerp(normal, fixed3(0.0, 0.0, 1.0), saturate(max(white, yellow) + sleeper));

            o.Albedo = albedo;
            o.Normal = normalize(normal);
            o.Emission = albedo * street * 1.3;
            #endif
        }
        ENDCG
    }
    FallBack "Diffuse"
}
