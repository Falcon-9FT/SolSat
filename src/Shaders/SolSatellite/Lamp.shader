// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

// Street lamps drawn by the SolSatellite plugin (LampMesher.cs). Each lamp is a quad expanded
// here into a camera-facing sprite that never gets smaller than about a pixel, so from the air
// a town is a sea of points; it dims as it is drawn larger than it really is. Added on top of
// what is behind it. Night only.
//
// Far from the camera the terrain is drawn from coarse meshes that can bulge a few metres
// above the true ground the lamps stand on, so every lamp is pulled towards the camera along
// its line of sight (which doesn't move it on screen), more the further away it is.
//
// uv0: xy corner (-1..1), w size of the halo (m). The halo keeps its real size on a scaled-down
// planet (a lamp is a point of light; at a quarter of its size it would be lost), and a few
// kilometres off the points fade out, handing over to the glow of their streets on the ground:
// thousands of them squeezed into a line along the horizon would add up to a white blur.
Shader "SolSatellite/Lamp"
{
    Properties
    {
        _Glow ("Light strength", Range(0, 8)) = 0.55
        _MinPixels ("Smallest light, pixels", Range(0.5, 6)) = 1.0
        _Pull ("Pull towards the camera, per metre of distance", Range(0, 0.02)) = 0.004
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend One One
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _SolSatLights;  // x: night 0..1, y: radius street lamps are shown in (m), z: 1 / fade width (m), w: strength
            float4 _SolSatCamUp;   // world-space up at the camera
            float4 _SolSatFocus;   // xyz: world position of the vessel lamps are also loaded around, w: 1 if there is one
            float4 _SolSatLampRange; // x: distance from the camera points are drawn up to (m), y: 1 / their fade width
            half _Glow, _MinPixels, _Pull;

            struct appdata
            {
                float4 vertex : POSITION;
                half4 color : COLOR;
                float4 corner : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 corner : TEXCOORD0;
                half3 color : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 world = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz;
                float3 fromCam = world - _WorldSpaceCameraPos;
                float3 fromFocus = world - _SolSatFocus.xyz;
                float dist = max(length(fromCam), 0.01);
                float3 toCam = -fromCam / dist;
                float night = _SolSatLights.x;

                // Metres per pixel at this distance, from the projection's vertical scale.
                float pixel = 2.0 * dist / (unity_CameraProjection._m11 * _ScreenParams.y);
                float size = v.corner.w;
                float radius = max(size, pixel * _MinPixels);
                float shrink = size / radius;
                world += (UNITY_MATRIX_V[0].xyz * v.corner.x + UNITY_MATRIX_V[1].xyz * v.corner.y) * radius;
                world += toCam * min(dist * _Pull, dist * 0.5);

                // Lamps fade out towards the edge of the area they are loaded for, around the
                // camera and around the vessel.
                float3 up = _SolSatCamUp.xyz;
                float horizontal = length(fromCam - dot(fromCam, up) * up);
                if (_SolSatFocus.w > 0.5)
                    horizontal = min(horizontal, length(fromFocus - dot(fromFocus, up) * up));
                float fade = saturate((_SolSatLights.y - horizontal) * _SolSatLights.z);
                float camera = length(fromCam - dot(fromCam, up) * up);
                if (_SolSatLampRange.y > 0.0)
                    fade *= saturate((_SolSatLampRange.x - camera) * _SolSatLampRange.y);

                o.pos = mul(UNITY_MATRIX_VP, float4(world, 1.0));
                o.corner = v.corner.xy;
                o.color = v.color.rgb * (_Glow * _SolSatLights.w * night * fade * max(shrink * shrink, 0.12));
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float d2 = dot(i.corner, i.corner);
                half falloff = saturate(1.0 - d2);
                // A bright core in a soft halo.
                half shape = falloff * falloff * 0.6 + saturate(1.0 - d2 * 8.0);
                return half4(i.color * shape, 1.0);
            }
            ENDCG
        }
    }
}
