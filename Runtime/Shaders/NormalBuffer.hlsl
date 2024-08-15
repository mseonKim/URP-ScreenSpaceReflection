#ifndef SSR_NORMAL_BUFFER_INCLUDED
#define SSR_NORMAL_BUFFER_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"

// ----------------------------------------------------------------------------
// Encoding/decoding normal buffer functions
// ----------------------------------------------------------------------------

struct NormalData
{
    float3 normalWS;
    float  perceptualRoughness;
};

// NormalBuffer texture declaration
#ifdef SSR_DEFERRED
    TEXTURE2D_X_HALF(_GBuffer2);
    #define LOAD_NORMAL_TEXTURE2D(positionSS)                 LOAD_TEXTURE2D_X(_GBuffer2, positionSS)
#else
    TEXTURE2D_X_FLOAT(_CameraNormalsTexture);
    #define LOAD_NORMAL_TEXTURE2D(positionSS)                 LOAD_TEXTURE2D_X(_CameraNormalsTexture, positionSS)
#endif

void EncodeIntoNormalBuffer(NormalData normalData, out float4 outNormalBuffer0)
{
#if defined(_GBUFFER_NORMALS_OCT)
    // The sign of the Z component of the normal MUST round-trip through the G-Buffer, otherwise
    // the reconstruction of the tangent frame for anisotropic GGX creates a seam along the Z axis.
    // The constant was eye-balled to not cause artifacts.
    // TODO: find a proper solution. E.g. we could re-shuffle the faces of the octahedron
    // s.t. the sign of the Z component round-trips.
    const float seamThreshold = 1.0 / 1024.0;
    normalData.normalWS.z = CopySign(max(seamThreshold, abs(normalData.normalWS.z)), normalData.normalWS.z);

    // RT1 - 8:8:8:8
    // Our tangent encoding is based on our normal.
    float2 octNormalWS = PackNormalOctQuadEncode(normalData.normalWS);
    float3 packNormalWS = PackFloat2To888(saturate(octNormalWS * 0.5 + 0.5));
    // We store perceptualRoughness instead of roughness because it is perceptually linear.
    outNormalBuffer0 = float4(packNormalWS, normalData.perceptualRoughness);
#else
    outNormalBuffer0 = float4(normalData.normalWS, normalData.perceptualRoughness);
#endif
}

void DecodeFromNormalBuffer(float4 normalBuffer, out NormalData normalData)
{
#if defined(_GBUFFER_NORMALS_OCT)
    float3 packNormalWS = normalBuffer.rgb;
    float2 octNormalWS = Unpack888ToFloat2(packNormalWS);
    normalData.normalWS = UnpackNormalOctQuadEncode(octNormalWS * 2.0 - 1.0);
#else
    normalData.normalWS = normalBuffer.rgb;
#endif
    normalData.perceptualRoughness = 1.0 - normalBuffer.a;  // TODO: check if remove oneminus in deferred
}

void DecodeFromNormalBuffer(uint2 positionSS, out NormalData normalData)
{
    float4 normalBuffer = LOAD_NORMAL_TEXTURE2D(positionSS);
    DecodeFromNormalBuffer(normalBuffer, normalData);
}

#endif // SSR_NORMAL_BUFFER_INCLUDED
