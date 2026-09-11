Shader "ASJ/JointSphere"
{
    Properties { _Color ("Color", Color) = (0,1,0.7,1) }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Color;
            struct v2f { float4 pos:SV_POSITION; float3 normal:TEXCOORD0; };
            v2f vert(appdata_base v) { v2f o; o.pos=UnityObjectToClipPos(v.vertex); o.normal=UnityObjectToWorldNormal(v.normal); return o; }
            fixed4 frag(v2f i):SV_Target { float shade=.4+.6*abs(dot(normalize(i.normal),normalize(float3(-.4,.6,-1)))); return fixed4(_Color.rgb*shade,1); }
            ENDCG
        }
    }
}
