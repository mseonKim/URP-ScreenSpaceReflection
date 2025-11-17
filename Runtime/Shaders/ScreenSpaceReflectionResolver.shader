Shader "Hidden/SSR_Resolver"
{
    SubShader
    {
        // No culling or depth
        Cull Off ZWrite Off ZTest Off
        
        Pass
        {
            Name "SSR Resolve"

            Blend One One
            BlendOp Add

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            half4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                half3 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).xyz;

                return half4(color, 0);
            }
            ENDHLSL
        }
    }
}
