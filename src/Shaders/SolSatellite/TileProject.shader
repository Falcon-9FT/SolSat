// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

// Clipmap maintenance for the SolSatellite plugin. Levels are toroidal: a point with absolute
// level coordinate a (tangent-plane metres / level width) lives at texel frac(a), so when a
// level follows the camera only the newly exposed strips need drawing.
Shader "SolSatellite/TileProject"
{
    Properties
    {
        _MainTex("Tile", 2D) = "white" {}
    }
    CGINCLUDE
    #include "UnityCG.cginc"

    float4 ToClip(float2 uv)
    {
        float4 pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
        // Unity renders into textures upside down on D3D-like APIs so that sampling
        // conventions match OpenGL; with positions given directly we have to do the same.
        #if UNITY_UV_STARTS_AT_TOP
        pos.y = -pos.y;
        #endif
        return pos;
    }

    // Light pools, for passes 2 and 3.
    float4 _PoolWindow;  // where to draw: absolute level coordinates (min x, min y, max x, max y)
    float4 _LightLevel;  // x: 1 / level width (m), y: texel size (m)
    float4 _LightOffset; // xy: whole levels to shift by, for the toroidal wrap

    struct PoolIn
    {
        float4 vertex : POSITION;  // xy: pool centre, plane metres
        half4 color : COLOR;
        float4 corner : TEXCOORD0; // xy: corner (-1..1), z: radius (m)
    };

    struct PoolOut
    {
        float4 pos : SV_POSITION;
        float2 corner : TEXCOORD0;
        float2 level : TEXCOORD1;
        half3 color : TEXCOORD2;
    };

    PoolOut PoolVert(PoolIn v)
    {
        PoolOut o;
        float radius = max(v.corner.z, _LightLevel.y);
        float2 a = (v.vertex.xy + v.corner.xy * radius) * _LightLevel.x;
        o.pos = ToClip(a - _LightOffset.xy);
        o.level = a;
        o.corner = v.corner.xy;
        float k = v.corner.z / radius;
        o.color = v.color.rgb * (k * k);
        return o;
    }

    fixed4 PoolFrag(PoolOut i) : SV_Target
    {
        if (any(i.level < _PoolWindow.xy) || any(i.level > _PoolWindow.zw))
            discard;
        half f = saturate(1.0 - dot(i.corner, i.corner));
        return fixed4(i.color * f * f, 0.0);
    }

    // Ribbons along lines (roads, painted lines, lit streets), for passes 4-6. Vertices come
    // in pairs on the line in the tile's flat frame; the object-to-world matrix takes them to
    // plane metres, and here they move apart along the miter vector by the half width plus a
    // texel, so every level antialiases its own edges. Two special cases for the road layer
    // (RibbonMeshData): fills (polygons) are triangles whose vertices have no
    // miter and a huge half width, wholly covered; and the antialiased edge of a fill is a
    // ribbon with half width -1, a texel either side of the edge, the fill on its left.
    struct RibbonIn
    {
        float4 vertex : POSITION;  // xy: point on the line, tile frame metres
        half4 color : COLOR;
        float4 ribbon : TEXCOORD0; // xy: miter vector, z: half width (m), w: side (-1, 1)
        float4 dash : TEXCOORD1;   // x: metres along, y: dash length (0: solid), z: gap, w: line length
    };

    struct RibbonOut
    {
        float4 pos : SV_POSITION;
        float2 level : TEXCOORD0;
        float3 across : TEXCOORD1; // x: metres from the line (signed), y: half width, z: texel
        half4 color : TEXCOORD2;
        float4 dash : TEXCOORD3;
    };

    RibbonOut RibbonVert(RibbonIn v)
    {
        RibbonOut o;
        float texel = _LightLevel.y;
        float reach = max(v.ribbon.z, 0.0) + texel;
        float2 centre = mul(unity_ObjectToWorld, float4(v.vertex.xy, 0.0, 1.0)).xy;
        float2 miter = mul((float3x3)unity_ObjectToWorld, float3(v.ribbon.xy, 0.0)).xy;
        float2 a = (centre + miter * (v.ribbon.w * reach)) * _LightLevel.x;
        o.pos = ToClip(a - _LightOffset.xy);
        o.level = a;
        o.across = float3(v.ribbon.w * reach, v.ribbon.z, texel);
        o.color = v.color;
        o.dash = v.dash;
        return o;
    }

    // Road layer: how much of the texel the ribbon covers (a texel-wide box filter over the
    // ribbon's width), dashes cut out. Thin lines on coarse levels fade instead of flickering.
    half4 RoadFrag(RibbonOut i) : SV_Target
    {
        if (any(i.level < _PoolWindow.xy) || any(i.level > _PoolWindow.zw))
            discard;
        float d = abs(i.across.x), w = i.across.y, t = i.across.z;
        float cover;
        if (w < 0.0)
        {
            // A fill's edge: the share of the texel on the fill's side (the left, negative).
            cover = saturate(0.5 - i.across.x / t);
        }
        else
        {
            // Main roads and railways (shade 0.74 and up) stay a line of their own on coarse
            // levels instead of fading into the fields, as they do seen from high up.
            if (i.color.r > 0.5 && i.color.g > 0.72)
                w = max(w, 0.3 * t);
            cover = saturate((min(d + 0.5 * t, w) - max(d - 0.5 * t, -w)) / t);
        }
        if (i.dash.y > 0.0)
            cover *= step(fmod(i.dash.x, i.dash.y + i.dash.z), i.dash.y);
        return i.color * cover;
    }

    // Night light map: a soft band across the street, dimmer when drawn wider than it is. Where
    // a row of lamps ends (dash.y: at the start, dash.z: at the end) it fades out over ~15 m.
    fixed4 GlowFrag(RibbonOut i) : SV_Target
    {
        if (any(i.level < _PoolWindow.xy) || any(i.level > _PoolWindow.zw))
            discard;
        float w = i.across.y, width = max(w, i.across.z);
        float s = i.across.x / width;
        half f = saturate(1.0 - s * s);
        float fade = 1.0;
        if (i.dash.y > 0.5)
            fade = min(fade, saturate(i.dash.x / 15.0));
        if (i.dash.z > 0.5)
            fade = min(fade, saturate((i.dash.w - i.dash.x) / 15.0));
        return fixed4(i.color.rgb * (f * fade * w / width), 0.0);
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off

        // Pass 0: draw a web-map tile. The plugin sends a grid of vertices already warped from
        // Mercator onto the level (in double precision on the CPU). Texels outside the level's
        // window are skipped: in a toroidal texture they belong to the opposite side.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            sampler2D _MainTex;
            float4 _Window; // xy: window min, zw: window max, in absolute level coordinates

            struct appdata
            {
                float4 vertex : POSITION;   // xy: texel position in the level (may exceed 0..1)
                float2 uv : TEXCOORD0;      // tile texture coordinate
                float2 level : TEXCOORD1;   // absolute level coordinate
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 level : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = ToClip(v.vertex.xy);
                o.uv = v.uv;
                o.level = v.level;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (any(i.level < _Window.xy) || any(i.level > _Window.zw))
                    discard;
                // Alpha is coverage: the terrain shader treats 0 as "no imagery here yet".
                return fixed4(tex2D(_MainTex, i.uv).rgb, 1.0);
            }
            ENDCG
        }

        // Pass 1: after a level moved, clear the texels that now belong to newly exposed ground
        // (inside the new window but outside the old one) so stale imagery never shows there.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 _Window;    // new window, absolute level coordinates
            float4 _OldWindow; // old window

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.pos = ToClip(vertex.xy);
                o.uv = vertex.xy;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // The one point of the new window that maps to this texel.
                float2 a = _Window.xy + frac(i.uv - _Window.xy);
                if (all(a >= _OldWindow.xy) && all(a <= _OldWindow.zw))
                    discard;
                return 0;
            }
            ENDCG
        }

        // Pass 2: pools of street light into the night light map (same levels and wrapping as the
        // imagery). Quads come around each pool's centre in tangent-plane metres; a pool smaller
        // than a texel is drawn a texel wide and dimmer, so coarse levels still get a town's glow.
        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma vertex PoolVert
            #pragma fragment PoolFrag
            ENDCG
        }

        // Pass 3: the same pools taken back out again (their lamps went away), so the light map
        // needn't be redrawn whole.
        Pass
        {
            BlendOp RevSub
            Blend One One
            CGPROGRAM
            #pragma vertex PoolVert
            #pragma fragment PoolFrag
            ENDCG
        }

        // Pass 4: roads into the road layer. Every channel keeps its maximum (r: road, g: its
        // shade times r, b: white paint, a: yellow paint), so overlapping ribbons and the same
        // road drawn by neighbouring tiles change nothing.
        Pass
        {
            BlendOp Max
            Blend One One
            CGPROGRAM
            #pragma vertex RibbonVert
            #pragma fragment RoadFrag
            ENDCG
        }

        // Pass 5: lit streets into the night light map.
        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma vertex RibbonVert
            #pragma fragment GlowFrag
            ENDCG
        }

        // Pass 6: lit streets taken back out.
        Pass
        {
            BlendOp RevSub
            Blend One One
            CGPROGRAM
            #pragma vertex RibbonVert
            #pragma fragment GlowFrag
            ENDCG
        }
    }
}
