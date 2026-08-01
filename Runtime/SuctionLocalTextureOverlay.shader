Shader "Vacuum/Suction Local Texture Overlay"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _TintColor ("Tint", Color) = (1, 1, 1, 1)
        _SoftFadeDistance ("Soft Fade Distance (m)", Range(0, 4)) = 0.6
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Blend SrcAlpha One
            ZWrite Off
            ZTest LEqual
            Cull Off
            Lighting Off

            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _TintColor;
            float _SoftFadeDistance;

            // Kept in built-in CG on purpose: this package must stay render-pipeline agnostic.
            // _CameraDepthTexture / _ZBufferParams are supplied by both the built-in pipeline and URP.
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            struct Attributes
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 projPos : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.projPos = ComputeScreenPos(output.vertex);
                // COMPUTE_EYEDEPTH は展開時に入力構造体名を `v` とハードコードするマクロなので、
                // 引数名が `input` の本 shader では使えない。同じ計算を直接書く。
                output.projPos.z = -UnityObjectToViewPos(input.vertex).z;
                return output;
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                fixed4 color = tex2D(_MainTex, input.uv) * _TintColor;

                // Soft particle fade. The funnel routinely intersects the floor, and a plain ZTest cut
                // leaves a hard sliced edge there. Fading out as the surface approaches whatever is
                // behind it turns that slice into a soft dissolve instead.
                if (_SoftFadeDistance > 0.0)
                {
                    float sceneEyeDepth = LinearEyeDepth(
                        SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(input.projPos)));
                    float surfaceEyeDepth = input.projPos.z;
                    color.a *= saturate((sceneEyeDepth - surfaceEyeDepth) / _SoftFadeDistance);
                }

                return color;
            }
            ENDCG
        }
    }
}
