// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

// SolSatellite additions to Parallax's terrain shader: terrain colour from satellite imagery.
//
// Everything here is set by Plugins/SolSatellite.dll on Earth's Parallax terrain materials
// only. While _SolSatParams.x and .z are 0 (plugin missing, other planets) the shader behaves
// exactly like the original Parallax shader.
//
// Two sources, blended fine-to-coarse:
//  * a clipmap: up to 16 nested square textures around the camera, finest first (from tens of
//    metres - as sharp as the finest zoom streamed - to thousands of km), filled with web-map
//    tiles as they stream in. Levels live on one tangent plane of the planet; a world position
//    is projected onto it gnomonically relative to the plane's anchor, which keeps everything in
//    small numbers (float-exact to centimetres) despite the planet's radius.
//  * the orbital colour cube (~0.6 km per texel) everywhere else.
// At night a second texture array with the same levels, the night light map (street lamps'
// pools of light and the glow of lit streets, drawn by the plugin), is added after lighting:
// SolSatNightLight. A third, the road layer, paints OpenStreetMap roads over the imagery:
// asphalt as wide as the road, and its painted lines (SolSatRoads).

#ifndef SOLSATELLITE_INCLUDED
#define SOLSATELLITE_INCLUDED

#define SOLSAT_LEVELS 16 // Clipmap.MaxLevels in the plugin

samplerCUBE _SolSatCube;          // Earth's orbital colour cube, indexed by PQS-local direction
float4x4 _SolSatWorldToPlanet;    // world space -> PQS local space
float4 _SolSatParams;             // x: cube enabled, y: imagery strength, z: clipmap enabled
float4 _SolSatNight;              // x: street lights on (0..1), y: their strength, z: radius lamps are loaded in (m), w: 1 / fade width
float4 _SolSatNightUp;            // world-space up at the camera
float4 _SolSatNightFocus;         // xyz: world position of the vessel lamps are also loaded around, w: 1 if there is one
float4 _SolSatRoadParams;         // x: road layer strength (0: none)

#if defined(SHADER_TARGET_SURFACE_ANALYSIS)
// Surface shaders (SolSatellite/Building) are first analysed by a compiler that knows no DX11
// texture objects; that pass only looks at which inputs are used, so stand-ins will do.
float4 SolSatClipColor(float3 worldPos)
{
    return 0;
}

float3 SolSatNightLight(float3 worldPos)
{
    return 0;
}

float4 SolSatRoadSample(float3 worldPos)
{
    return 0;
}

float SolSatFlatten(float3 worldPos)
{
    return 1;
}
#else
Texture2DArray _SolSatClip;       // clipmap levels (toroidal, wrap sampling); alpha = "imagery present"
SamplerState sampler_SolSatClip;
float4 _SolSatAxisE;              // world-space east at the plane's anchor
float4 _SolSatAxisN;              // world-space north at the anchor
float4 _SolSatAxisU;              // world-space up at the anchor
float4 _SolSatRef;                // xyz: world position plane offsets are measured from (the camera), w: plane radius
float4 _SolSatRefPlane;           // xy: its plane position / z, z: its distance from the centre along the anchor's up
float4 _SolSatLevels[SOLSAT_LEVELS];    // xy: reference point relative to the level's centre / level width, z: 1 / level width (m), w: active
float4 _SolSatLevelFrac[SOLSAT_LEVELS]; // xy: frac(reference point's plane position / level width)

// Plane metres from the reference point to a world position: the difference of their gnomonic
// projections, worked out from small numbers (positions near the camera, offsets the plugin
// computes in double precision), so the finest levels - a few centimetres a texel - stay exact
// even a thousand kilometres from the plane's anchor.
float2 SolSatPlaneDelta(float3 worldPos)
{
    float3 d = worldPos - _SolSatRef.xyz;
    float du = dot(d, _SolSatAxisU.xyz);
    float2 dp = float2(dot(d, _SolSatAxisE.xyz), dot(d, _SolSatAxisN.xyz));
    return (dp - _SolSatRefPlane.xy * du) * (_SolSatRef.w / (_SolSatRefPlane.z + du));
}

// Premultiplied clipmap colour (rgb * a, a) at a world position, finest level first.
float4 SolSatClipColor(float3 worldPos)
{
    float2 delta = SolSatPlaneDelta(worldPos);
    // Gradients once, outside the loop: sampling inside a branchy loop can't use implicit ones.
    // They come from continuous coordinates, so the toroidal wrap doesn't upset mip selection.
    float2 dx = ddx(delta);
    float2 dy = ddy(delta);

    float4 result = 0;
    [unroll]
    for (int i = 0; i < SOLSAT_LEVELS; i++)
    {
        float4 level = _SolSatLevels[i];
        float2 d = delta * level.z;
        float2 edge = 0.5 - abs(level.xy + d); // distance to the level's window border
        // Fade out over the outer 1/16 of each level so levels blend instead of showing seams.
        float fade = saturate(min(edge.x, edge.y) * 16.0) * level.w;
        if (fade > 0.0 && result.a < 0.999)
        {
            float2 uv = frac(_SolSatLevelFrac[i].xy + d); // toroidal: absolute level coordinate, wrapped
            float4 c = _SolSatClip.SampleGrad(sampler_SolSatClip, float3(uv, i), dx * level.z, dy * level.z);
            float w = c.a * fade * (1.0 - result.a);
            result.rgb += c.rgb * w;
            result.a += w;
        }
    }
    return result;
}

Texture2DArray _SolSatLight;      // night light map: street lamps' pools of light, same levels as _SolSatClip
SamplerState sampler_SolSatLight;

// Light that street lamps throw on the ground here (added after lighting, like emission).
float3 SolSatNightLight(float3 worldPos)
{
    if (_SolSatNight.x <= 0.0 || _SolSatParams.z < 0.5)
        return 0;
    float2 delta = SolSatPlaneDelta(worldPos);
    float2 dx = ddx(delta);
    float2 dy = ddy(delta);

    float3 light = 0;
    float remaining = 1.0;
    [unroll]
    for (int i = 0; i < SOLSAT_LEVELS; i++)
    {
        float4 level = _SolSatLevels[i];
        float2 d = delta * level.z;
        float2 edge = 0.5 - abs(level.xy + d);
        float fade = saturate(min(edge.x, edge.y) * 16.0) * level.w;
        if (fade > 0.0 && remaining > 0.001)
        {
            float2 uv = frac(_SolSatLevelFrac[i].xy + d);
            // The light map adds up in floating point (the plugin's GroundLayer.LightFormat); where
            // much light overlaps it is shown as it was in 8 bits, at most 1.
            float3 c = min(_SolSatLight.SampleGrad(sampler_SolSatLight, float3(uv, i), dx * level.z, dy * level.z).rgb, 1.0);
            light += c * (fade * remaining);
            remaining *= 1.0 - fade;
        }
    }
    // Like the lamps themselves, their light fades out towards the edge of the area they are
    // loaded in (horizontally from the camera, or from the vessel if nearer).
    float fade = 1.0;
    if (_SolSatNight.w > 0.0)
    {
        float3 up = _SolSatNightUp.xyz;
        float3 v = worldPos - _WorldSpaceCameraPos;
        float d = length(v - dot(v, up) * up);
        if (_SolSatNightFocus.w > 0.5)
        {
            float3 f = worldPos - _SolSatNightFocus.xyz;
            d = min(d, length(f - dot(f, up) * up));
        }
        fade = saturate((_SolSatNight.z - d) * _SolSatNight.w);
    }
    return light * (_SolSatNight.x * _SolSatNight.y * fade);
}

Texture2DArray _SolSatRoad;       // road layer: r road, g its shade * r, b white paint, a yellow paint
SamplerState sampler_SolSatRoad;

// The road layer here, finest level first.
float4 SolSatRoadSample(float3 worldPos)
{
    float2 delta = SolSatPlaneDelta(worldPos);
    float2 dx = ddx(delta);
    float2 dy = ddy(delta);

    float4 road = 0;
    float remaining = 1.0;
    [unroll]
    for (int i = 0; i < SOLSAT_LEVELS; i++)
    {
        float4 level = _SolSatLevels[i];
        float2 d = delta * level.z;
        float2 edge = 0.5 - abs(level.xy + d);
        float fade = saturate(min(edge.x, edge.y) * 16.0) * level.w;
        if (fade > 0.0 && remaining > 0.001)
        {
            float2 uv = frac(_SolSatLevelFrac[i].xy + d);
            road += _SolSatRoad.SampleGrad(sampler_SolSatRoad, float3(uv, i), dx * level.z, dy * level.z) * (fade * remaining);
            remaining *= 1.0 - fade;
        }
    }
    return road;
}

// How much of Parallax's displacement to keep at a vertex: none under roads, so they lie on the
// bare terrain mesh, flat - which is also what the 3D roads are draped on (HeightField.cs).
// For vertex and domain shaders: no derivatives, the finest level's own texels.
float SolSatFlatten(float3 worldPos)
{
    if (_SolSatRoadParams.x <= 0.0 || _SolSatParams.z < 0.5)
        return 1.0;
    float2 delta = SolSatPlaneDelta(worldPos);
    [unroll]
    for (int i = 0; i < SOLSAT_LEVELS; i++)
    {
        float4 level = _SolSatLevels[i];
        float2 d = delta * level.z;
        float2 edge = 0.5 - abs(level.xy + d);
        if (level.w > 0.5 && min(edge.x, edge.y) > 0.0)
            return 1.0 - saturate(_SolSatRoad.SampleLevel(sampler_SolSatRoad, float3(frac(_SolSatLevelFrac[i].xy + d), i), 0).r * 2.0);
    }
    return 1.0;
}
#endif

// The colour of a road's surface: asphalt from dark (motorways) to light (shade 0.3), then
// concrete up to pale (0.12: paving), railways as pale ballast with a dark
// track (shades: RoadMesher), with a little of the imagery's brightness there
// (sunlit and shaded parts, patches, cars) but not its colour.
float3 SolSatRoadColour(float4 road, float3 imagery)
{
    float shade = saturate(road.g / max(road.r, 1e-4));
    float3 surface = lerp(float3(0.46, 0.45, 0.43), float3(0.26, 0.265, 0.28), saturate((shade - 0.3) / 0.6));
    surface = lerp(float3(0.62, 0.61, 0.58), surface, saturate((shade - 0.12) / 0.18)); // concrete
    surface = lerp(surface, float3(0.24, 0.22, 0.20), saturate((shade - 0.93) * 14.0)); // track
    surface = lerp(surface, float3(0.50, 0.47, 0.42), saturate((0.12 - shade) * 14.0)); // ballast
    float luma = dot(imagery, float3(0.30, 0.59, 0.11));
    return lerp(surface, luma * float3(0.98, 0.98, 1.0), 0.3);
}

// Roads laid over the ground the terrain shader has made (Parallax's grass, sand and rock
// textures and their bumps included): where a road is, its asphalt and paint take the place of
// all that, and the surface is flat. Call once diffuse and normal are final, before lighting.
void SolSatApplyRoads(float3 worldPos, float3 imagery, inout float3 diffuse, inout float3 normal, float3 flatNormal)
{
    if (_SolSatRoadParams.x <= 0.0 || _SolSatParams.z < 0.5)
        return;
    float4 road = SolSatRoadSample(worldPos);
    float strength = _SolSatRoadParams.x;
    if (road.r > 0.002)
        diffuse = lerp(diffuse, SolSatRoadColour(road, imagery), saturate(road.r) * strength);
    diffuse = lerp(diffuse, float3(0.80, 0.80, 0.78), saturate(road.b) * strength);
    diffuse = lerp(diffuse, float3(0.82, 0.62, 0.16), saturate(road.a) * strength);
    normal = normalize(lerp(normal, flatNormal, saturate(road.r)));
}

float3 SolSatColor(float3 vertexColor, float3 worldPos)
{
    float3 color = vertexColor;
    if (_SolSatParams.x > 0.5)
    {
        float3 planetPos = mul(_SolSatWorldToPlanet, float4(worldPos, 1.0)).xyz;
        float4 sat = texCUBE(_SolSatCube, planetPos);
        // Alpha is Sol's ocean mask (~0.98 over water): keep Sol's own seabed colours under the sea.
        float land = saturate(1.0 - sat.a * 1.05);
        color = lerp(color, sat.rgb, land * _SolSatParams.y);
    }
    if (_SolSatParams.z > 0.5)
    {
        float4 clip = SolSatClipColor(worldPos);
        color = clip.rgb * _SolSatParams.y + color * (1.0 - clip.a * _SolSatParams.y);
    }
    return color;
}

#endif
