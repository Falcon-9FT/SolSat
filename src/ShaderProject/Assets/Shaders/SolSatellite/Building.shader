// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

// Buildings extruded from OpenStreetMap / Overture Maps footprints by the SolSatellite plugin
// (BuildingMesher.cs).
//
// Every surface has a material - a layer of the CC0 texture arrays (Materials.cs): plaster,
// brick, concrete, glass curtain wall, corrugated metal, wood siding; clay tiles, slate or
// shingles, metal, gravel - whose texture is scaled from its own mean colour to the vertex
// colour, so one texture serves every colour of a material. uv0: metres along and up the
// surface, style (0 plain, 1 homes, 2 offices, 3 industry; 9..10 flat roof, 11..12 pitched
// roof, the fraction being how much satellite imagery it shows), material layer. Walls get
// windows by storey (uv1: window column along the wall, floor height, eaves, metres between
// windows - negative with shops on the ground floor): frames, glass that reflects the sky at
// grazing angles, shop windows, panel joints on concrete blocks. At night a random share of windows lights up - more in some
// buildings, none in others - and the street lamps' light (the terrain's night light map at
// the wall's foot) falls on the lower floors. Roofs show the satellite imagery of the ground
// clipmap - from the air that is the building's real roof - with their material's relief,
// pitched ones more of their tiles close up; tall buildings lean in the imagery and keep
// their own colour. Near the edge of the area buildings are loaded for (around the camera and
// around the vessel), they dissolve with a dither instead of popping.
Shader "SolSatellite/Building"
{
    Properties
    {
        _HomeLight ("Home window light", Color) = (1.0, 0.72, 0.42, 1)
        _OfficeLight ("Office window light", Color) = (0.82, 0.88, 1.0, 1)
        _WindowGlow ("Window light strength", Range(0, 4)) = 1.3
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }

        CGPROGRAM
        #pragma surface surf Lambert vertex:vert addshadow
        #pragma target 4.0
        #pragma require 2darray
        #include "../Terrain/SolSatellite.cginc"

        UNITY_DECLARE_TEX2DARRAY(_SolSatMatColour);
        UNITY_DECLARE_TEX2DARRAY(_SolSatMatNormal);
        float4 _SolSatMatAverage[16]; // each colour layer's mean colour
        float4 _SolSatBuild;   // x: night 0..1, y: radius buildings are loaded for (m), z: 1 / fade width (m)
        float4 _SolSatCamUp;   // world-space up at the camera
        float4 _SolSatFocus;   // xyz: world position of the vessel buildings are also loaded around, w: 1 if there is one
        fixed4 _HomeLight, _OfficeLight;
        half _WindowGlow;

        static const float StreetLightHeight = 14.0; // m up a wall the street lamps' light reaches
        static const float StreetLightOnWalls = 1.4;

        struct Input
        {
            float3 worldPos;
            float4 screenPos;
            float4 color : COLOR;   // rgb: the surface's colour, a: random value per building
            float4 tex;             // uv0: metres along, metres up, style, material layer
            float4 win;             // uv1: window column, floor height, eaves, bay width (< 0: shops)
            float3 viewDir;         // tangent space
        };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.tex = v.texcoord;
            o.win = v.texcoord1;
            // Textures run along the wall (or the roof's eaves) and up it: the tangent is
            // horizontal, the binormal up the surface. The mesher sets uv0 the same way.
            float3 t = cross(v.normal, float3(0.0, 1.0, 0.0));
            t = dot(t, t) > 1e-6 ? normalize(t) : float3(1.0, 0.0, 0.0);
            v.tangent = float4(t, -1.0);
        }

        #if !defined(SHADER_TARGET_SURFACE_ANALYSIS)
        float Hash(float2 p)
        {
            float3 p3 = frac(p.xyx * 0.1031);
            p3 += dot(p3, p3.yzx + 33.33);
            return frac((p3.x + p3.y) * p3.z);
        }

        // 4x4 ordered dither threshold: M4 = 4 * M2(p mod 2) + M2(p / 2), M2(x, y) = (2x + 3y) mod 4.
        float Bayer(float2 pixel)
        {
            float2 p = fmod(floor(pixel), 4.0);
            float2 lo = fmod(p, 2.0), hi = floor(p * 0.5);
            float m = 4.0 * fmod(2.0 * lo.x + 3.0 * lo.y, 4.0) + fmod(2.0 * hi.x + 3.0 * hi.y, 4.0);
            return (m + 0.5) / 16.0;
        }

        // Antialiased 1 inside |x| < half, a pixel being aa wide.
        float Box(float x, float half, float aa)
        {
            return saturate((half - abs(x)) / aa + 0.5);
        }

        // Metres per texture repeat of each layer (glass: ten panels of half a storey).
        float Repeat(float layer, float floorH)
        {
            if (layer < 0.5) return 2.5;   // asphalt
            if (layer < 1.5) return 1.5;   // paving
            if (layer < 2.5) return 3.0;   // concrete
            if (layer < 3.5) return 2.0;   // gravel
            if (layer < 4.5) return 1.0;   // steel
            if (layer < 5.5) return 3.0;   // plaster
            if (layer < 6.5) return 2.6;   // brick
            if (layer < 7.5) return 5.0 * max(floorH, 3.0); // glass curtain wall
            if (layer < 8.5) return 4.0;   // corrugated metal
            if (layer < 9.5) return 2.5;   // wood siding
            if (layer < 10.5) return 2.9;  // clay tiles
            return 2.0;                    // slate, shingles
        }
        #endif

        void surf(Input IN, inout SurfaceOutput o)
        {
            #if defined(SHADER_TARGET_SURFACE_ANALYSIS)
            // The analysis pass only needs to see which inputs are used.
            o.Albedo = IN.color.rgb * IN.tex.xyz * IN.win.xyz * IN.worldPos * IN.screenPos.xyz * IN.viewDir;
            o.Normal = float3(0.0, 0.0, 1.0);
            o.Emission = 0;
            #else
            // Horizontal distance from the camera, or from the vessel if nearer, against the loaded radius.
            float3 v = IN.worldPos - _WorldSpaceCameraPos;
            float3 up = _SolSatCamUp.xyz;
            float camDist = length(v - dot(v, up) * up);
            float dist = camDist;
            if (_SolSatFocus.w > 0.5)
            {
                float3 u = IN.worldPos - _SolSatFocus.xyz;
                dist = min(dist, length(u - dot(u, up) * up));
            }
            float fade = saturate((_SolSatBuild.y - dist) * _SolSatBuild.z);
            float2 pixel = IN.screenPos.xy / max(IN.screenPos.w, 1e-5) * _ScreenParams.xy;
            clip(fade - Bayer(pixel));

            // Everything that needs screen-space derivatives comes first, while all pixels of
            // a quad still run the same code.
            float4 imagery = _SolSatParams.z > 0.5 ? SolSatClipColor(IN.worldPos) : 0;
            float3 street = _SolSatParams.z > 0.5 ? SolSatNightLight(IN.worldPos) : 0;
            float2 metres = IN.tex.xy;
            float columnAA = max(fwidth(IN.win.x), 1e-4);
            float heightAA = max(fwidth(metres.y), 1e-4);
            float style = IN.tex.z;
            float layer = round(IN.tex.w);
            float floorH = IN.win.y;

            // The material, its texture scaled from its mean colour to this surface's.
            bool corrugated = layer > 7.5 && layer < 8.5;
            float2 uv = (corrugated ? metres.yx : metres) / Repeat(layer, floorH); // sheets' ribs run up walls and down roofs
            float3 uvw = float3(uv, layer);
            float3 tex = UNITY_SAMPLE_TEX2DARRAY(_SolSatMatColour, uvw).rgb;
            float3 normal = UNITY_SAMPLE_TEX2DARRAY(_SolSatMatNormal, uvw).rgb * 2.0 - 1.0;
            if (corrugated)
                normal.xy = normal.yx;
            float3 mean = max(_SolSatMatAverage[(int)layer].rgb, 0.02);
            float3 ratio = tex / mean;
            // Brick, glass and roof tiles keep their own shades of colour; painted and plain
            // surfaces only their light and dark, in the surface's colour.
            bool natural = layer > 5.5 && layer < 7.5 || layer > 9.5;
            if (layer > 10.5)
                ratio = lerp(1.0, ratio, 0.6); // slate and shingles: less contrast than the stone tiles' photo
            float3 albedo = (natural ? ratio : dot(ratio, float3(0.3, 0.59, 0.11)).xxx) * IN.color.rgb;
            float fresnel = pow(1.0 - saturate(normalize(IN.viewDir).z), 4.0);

            if (style > 8.5)
            {
                // Roofs: the imagery (premultiplied by its coverage; where there is none, the
                // material) with the material's relief on it. Pitched roofs show their tiles
                // more close up, where the imagery is a blur.
                bool pitched = style > 10.5;
                float share = saturate(style - (pitched ? 11.0 : 9.0)) * _SolSatParams.y;
                float detail = dot(tex / mean, float3(0.3, 0.59, 0.11));
                float3 photo = imagery.rgb * lerp(1.0, detail, 0.6) + albedo * (1.0 - imagery.a);
                float near = saturate((camDist - 60.0) / 300.0);
                share *= pitched ? lerp(0.45, 0.9, near) : 1.0;
                o.Albedo = lerp(albedo, photo, share);
                o.Normal = normalize(lerp(float3(0.0, 0.0, 1.0), normal, pitched ? 1.0 : 0.6));
                o.Emission = 0;
                return;
            }

            o.Albedo = albedo;
            o.Normal = normal;
            // Street lamps light the lower floors of the walls facing them.
            float3 streetLight = street * (StreetLightOnWalls * saturate(1.0 - metres.y / StreetLightHeight));
            o.Emission = albedo * streetLight;
            if (style < 0.5 || floorH <= 0.0)
                return;

            // Some buildings are busier than others, and a fifth have no lights on at all.
            float busy = frac(IN.color.a * 7.31);
            float litShare = style < 1.5 ? 0.32 : style < 2.5 ? 0.22 : 0.08;
            litShare *= frac(IN.color.a * 3.7) < 0.2 ? 0.0 : 0.3 + 1.4 * busy;
            float3 light = style < 1.5 || style > 2.5 ? _HomeLight.rgb : _OfficeLight.rgb;
            float night = _SolSatBuild.x * _WindowGlow;
            float eaves = IN.win.z;
            float y = metres.y;

            if (layer > 6.5 && layer < 7.5)
            {
                // A glass curtain wall: the texture is the glazing; it mirrors the sky at
                // grazing angles, and at night some of its panels (half a storey square) are lit.
                float panel = max(floorH, 3.0) * 0.5;
                float2 id = floor(metres / panel);
                float lit = Hash(id + IN.color.a * 131.7) < litShare ? 1.0 : 0.0;
                float aa = max(columnAA, heightAA / panel);
                lit = lerp(litShare * 0.6, lit, saturate(2.0 - aa * 4.0));
                float inside = saturate(y * 2.0) * saturate((eaves - y) * 2.0);
                o.Albedo = albedo + fresnel * float3(0.30, 0.36, 0.42);
                o.Emission = o.Albedo * streetLight + light * (0.85 * lit * inside * night);
                return;
            }

            // Storeys: the ground floor (taller with shops), then floor after floor.
            bool shops = IN.win.w < 0.0;
            float bay = max(abs(IN.win.w), 0.5); // m between windows
            float groundH = shops ? max(floorH * 1.3, 4.2) : floorH;
            float above = max(y - groundH, 0.0) / floorH;
            bool groundFloor = y < groundH;
            float fy = groundFloor ? saturate(y / groundH) : frac(above);   // 0..1 up the storey
            float row = groundFloor ? 0.0 : 1.0 + floor(above);
            float storey = groundFloor ? groundH : floorH;
            float cx = frac(IN.win.x) - 0.5;                                // -0.5..0.5 across the window's bay
            float2 id = float2(floor(IN.win.x), row);

            // The window in its bay: width (share of the bay), bottom and top (share of the storey).
            float halfW, bottom, top;
            if (groundFloor && shops)
            {
                halfW = 0.42; bottom = 0.08; top = 0.82;
            }
            else if (style < 1.5)
            {
                halfW = 0.21; bottom = 0.32; top = 0.82;
            }
            else if (style < 2.5)
            {
                halfW = 0.38; bottom = 0.26; top = 0.86;
            }
            else
            {
                halfW = 0.36; bottom = 0.55; top = 0.82;
            }
            float aaY = heightAA / storey;
            float halfH = 0.5 * (top - bottom), centre = 0.5 * (top + bottom);
            float outer = Box(cx, halfW, columnAA) * Box(fy - centre, halfH, aaY);
            float glass = Box(cx, halfW - 0.07 / bay, columnAA) * Box(fy - centre, halfH - 0.07 / storey, aaY); // 7 cm frames
            // No windows under the ground, nor above the eaves (gables).
            float allowed = saturate(y * 2.0) * saturate((eaves - 0.25 - y) * 4.0);
            outer *= allowed;
            glass *= allowed;
            // Once a bay shrinks under a couple of pixels, its average instead of shimmering.
            float detailFade = saturate(2.0 - max(columnAA, aaY) * 4.0);
            float coverage = 2.0 * halfW * 2.0 * halfH * allowed;
            outer = lerp(coverage, outer, detailFade);
            glass = lerp(coverage * 0.8, glass, detailFade);

            float seed = frac(IN.color.a * 13.1);
            float3 frameColour = style < 1.5 ? (seed < 0.75 ? float3(0.86, 0.86, 0.84) : float3(0.30, 0.22, 0.16)) : float3(0.28, 0.29, 0.30);
            float3 glassColour = lerp(float3(0.10, 0.12, 0.14), float3(0.36, 0.44, 0.52), saturate(fresnel * 1.5 + (fy - bottom) / (top - bottom) * 0.25));
            float3 wall = albedo;
            if (layer > 1.5 && layer < 2.5 && style > 0.5 && style < 1.5)
            {
                // Panel blocks: 3 cm joints at every storey and between the bays.
                float seamX = Box(0.5 - abs(cx), 0.015 / bay, columnAA);
                float seamY = Box(min(fy, 1.0 - fy), 0.015 / storey, aaY);
                wall *= 1.0 - 0.35 * saturate(seamX + seamY) * detailFade;
            }
            o.Albedo = lerp(lerp(wall, frameColour, outer), glassColour, glass);
            o.Normal = normalize(lerp(normal, float3(0.0, 0.0, 1.0), outer));

            // Lit windows at night: a point of light each, not lit wall; far off, their average
            // and dimmer, so the walls don't seem to glow by themselves.
            float lit = Hash(id + IN.color.a * 131.7) < litShare ? 1.0 : 0.0;
            lit = lerp(litShare * 0.6, lit, detailFade);
            float warmth = 0.75 + 0.5 * Hash(id.yx + IN.color.a * 17.3);
            o.Emission = o.Albedo * streetLight + light * (glass * lit * warmth * night);
            #endif
        }
        ENDCG
    }
    FallBack "Diffuse"
}
