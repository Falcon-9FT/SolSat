// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

// Replacement shader for HeightField.cs: the terrain (our Parallax build tags its shaders
// "SolSatHeight" = "Terrain") seen from straight above, each texel holding how far below the
// orthographic camera the ground is - the PQS mesh as drawn, without Parallax's displacement
// (the terrain shader leaves that out under roads anyway).
Shader "SolSatellite/HeightCapture"
{
    SubShader
    {
        Tags { "SolSatHeight" = "Terrain" }
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float depth : TEXCOORD0;
            };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.depth = -UnityObjectToViewPos(vertex).z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.depth, 0.0, 0.0, 0.0);
            }
            ENDCG
        }
    }
}
