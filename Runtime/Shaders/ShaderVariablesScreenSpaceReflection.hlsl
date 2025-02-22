#ifndef SHADERVARIABLESSCREENSPACEREFLECTION_HLSL
#define SHADERVARIABLESSCREENSPACEREFLECTION_HLSL

CBUFFER_START(_ShaderVariablesScreenSpaceReflection)
    float       _SsrThicknessScale;
    float       _SsrThicknessBias;
    int         _SsrIterLimit;
    int         _SsrPad0_;
    float       _SsrRoughnessFadeEnd;
    float       _SsrRoughnessFadeRcpLength;
    float       _SsrRoughnessFadeEndTimesRcpLength;
    float       _SsrEdgeFadeRcpLength;
    int         _SsrDepthPyramidMaxMip;
    int         _SsrColorPyramidMaxMip;
    int         _SsrReflectsSky;
    int         _SsrPad1_;
    float4x4    _CameraViewProjMatrix;
    float4x4    _InvCameraViewProjMatrix;
    // float4      _ColorPyramidUvScaleAndLimitPrevFrame;
CBUFFER_END


#endif
