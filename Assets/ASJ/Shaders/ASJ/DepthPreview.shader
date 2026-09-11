Shader "ASJ/DepthPreview"
{
    Properties
    {
        _MainTex ("Depth", 2D) = "black" {}
        _EncodedScale ("Encoded Scale", Float) = 65535
        _DepthUnitToMillimeters ("Unit To Millimeters", Float) = 1
        _MaxDepthMillimeters ("Max Depth Millimeters", Float) = 4000
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _EncodedScale;
            float _DepthUnitToMillimeters;
            float _MaxDepthMillimeters;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float encoded = tex2D(_MainTex, i.uv).r;
                float rawDepth = encoded * _EncodedScale;
                if (rawDepth <= 0.0)
                    return fixed4(0, 0, 0, 1);

                float depthMm = rawDepth * _DepthUnitToMillimeters;
                float v = saturate(depthMm / max(1.0, _MaxDepthMillimeters));
                return fixed4(v, v, v, 1);
            }
            ENDCG
        }
    }
}
