// EVE's city lights on the scaled planet (EVE/PlanetCityLight from EnvironmentalVisualEnhancements,
// MIT; includes in EVE/), for Earth under Parallax's scaled shader (2026-10-05). EVE draws the lights
// as one more material on the scaled planet's own mesh - a smooth sphere - without writing depth;
// Parallax tessellates that planet and raises the land by its heights, so the lights lay under the
// ground and failed the depth test: no city lights from orbit, once the terrain (PQS) is gone
// (75 km on Quarter Earth). Here every vertex is moved towards the camera along its own line of
// sight by _SolSatLift (world units, a few game km): the lights stay on the same pixels, only their
// depth comes in front of the land (the Moon in front of Earth still hides them). And EVE's ocean
// test on the planet's _MainTex alpha is gone: Parallax's material has no _MainTex, so EVE's white
// default (alpha 1) put every light out; the city map itself only has lights on land.
Shader "SolSatellite/PlanetCityLight" {
	Properties {
		_Color ("Color Tint", Color) = (1,1,1,1)
		_MainTex ("Main (RGB)", 2D) = "white" {}
		_BumpMap ("Normalmap", 2D) = "bump" {}
		_SpecularColor ("Specular tint", Color) = (1,1,1,1)
		_SpecularPower ("Shininess", Float) = 0.078125
		_DetailDist ("Detail Distance", Range(0,1)) = 0.00875
		_CityOverlayTex ("Overlay (RGB)", 2D) = "white" {}
		_CityOverlayDetailScale ("Overlay Detail Scale", Range(0,1000)) = 80
		_CityDarkOverlayDetailTex ("Overlay Detail (RGB) (A)", 2D) = "white" {}
		_CityLightOverlayDetailTex ("Overlay Detail (RGB) (A)", 2D) = "white" {}
		_PlanetOpacity ("PlanetOpacity", Float) = 1
		_SunDir("SunDir", Vector) = (0,0,0,1)
		_SolSatLift ("Lift towards the camera (world units)", Float) = 0
	}
	Category {
		Lighting On
		ZWrite Off
		Cull Back
		Offset 0, 0
		Blend SrcAlpha OneMinusSrcAlpha
		Tags {
			"Queue"="Geometry+1"
			"RenderMode"="Transparent"
			"IgnoreProjector"="True"
		}
		SubShader {
			Pass {

				Lighting Off

				CGPROGRAM

				#include "EVE/EVEUtils.cginc"
				#pragma target 3.0
				#pragma glsl
				#pragma vertex vert
				#pragma fragment frag
				#pragma fragmentoption ARB_precision_hint_fastest
				#pragma multi_compile_fwdbase
#pragma multi_compile MAP_TYPE_1 MAP_TYPE_CUBE_1 MAP_TYPE_CUBE2_1 MAP_TYPE_CUBE6_1
#ifndef MAP_TYPE_CUBE2_1
#pragma multi_compile ALPHAMAP_N_1 ALPHAMAP_1
#endif
#include "EVE/alphaMap.cginc"
#include "EVE/cubeMap.cginc"

				CUBEMAP_DEF_1(_CityOverlayTex)

				fixed4 _Color;
				float _SpecularPower;
				half4 _SpecularColor;
				sampler2D _BumpMap;
				float _DetailDist;
				float3 _SunDir;
				float _SolSatLift;

				float _CityOverlayDetailScale;
				sampler2D _CityDarkOverlayDetailTex;
				sampler2D _CityLightOverlayDetailTex;

				struct appdata_t {
					float4 vertex : POSITION;
					float3 normal : NORMAL;
					float4 tangent : TANGENT;
				};

				struct v2f {
					float4 pos : SV_POSITION;
					float viewDist : TEXCOORD0;
					float3 viewDirT : TEXCOORD1;
					float3 sphereNormal : TEXCOORD2;
					float3 worldNormal : TEXCOORD3;
					float3 lightDirT : TEXCOORD5;
					LIGHTING_COORDS(6,7)
				};


				v2f vert (appdata_t v)
				{
					v2f o;
					float3 vertexPos = mul(unity_ObjectToWorld, v.vertex).xyz;
					float3 toCamera = _WorldSpaceCameraPos - vertexPos;
					float dist = length(toCamera);
					// Along the line of sight: same pixel, nearer depth (never past half the way to the camera).
					float3 lifted = vertexPos + toCamera / max(dist, 1e-6) * min(_SolSatLift, 0.5 * dist);
					o.pos = mul(UNITY_MATRIX_VP, float4(lifted, 1.0));
					o.viewDist = dist;

					o.sphereNormal = -normalize(v.vertex);

					o.worldNormal = normalize(mul( unity_ObjectToWorld, float4( v.normal, 0.0 ) ).xyz);
					float3 objLight = mul(unity_WorldToObject, float4(_SunDir.xyz,0));

					TANGENT_SPACE_ROTATION;
					o.lightDirT = normalize(mul(rotation, objLight));
					o.viewDirT = normalize(mul(rotation, ObjSpaceViewDir(v.vertex)));
					TRANSFER_VERTEX_TO_FRAGMENT(o);

					return o;
				}


				fixed4 frag (v2f IN) : COLOR
				{
					half4 color;
					half3 normT = UnpackNormal(GetCubeMap(_BumpMap, IN.sphereNormal));

					half4 cityoverlay = GET_CUBE_MAP_1(_CityOverlayTex, IN.sphereNormal);
					cityoverlay = ALPHA_COLOR_1(cityoverlay);

					half4 citydarkoverlaydetail = GetCubeDetailMap(_CityDarkOverlayDetailTex, IN.sphereNormal, _CityOverlayDetailScale);
					half4 citylightoverlaydetail = GetCubeDetailMap(_CityLightOverlayDetailTex, IN.sphereNormal, _CityOverlayDetailScale);

					citydarkoverlaydetail.a *= cityoverlay.a;
					citylightoverlaydetail.a *= cityoverlay.a;
					color.rgb = lerp( half3(0,0,0), citylightoverlaydetail.rgb, citylightoverlaydetail.a);
					color.a = 0;

					//lighting
					half4 specColor = _SpecularColor;

					color = SpecularColorLight( IN.lightDirT, IN.viewDirT, normT, color, specColor, _SpecularPower, LIGHT_ATTENUATION(IN) );
					color *= Terminator( normalize(_SunDir), IN.worldNormal);

					citydarkoverlaydetail.a *= 1-saturate(color.a);
					color = lerp(citylightoverlaydetail, citydarkoverlaydetail, citydarkoverlaydetail.a);

					return color;
				}
				ENDCG

			}
		}
	}
}
