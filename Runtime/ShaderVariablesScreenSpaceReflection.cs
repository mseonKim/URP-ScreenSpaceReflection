using UnityEngine;

namespace UniversalScreenSpaceReflection
{
    internal struct ShaderVariablesScreenSpaceReflection
    {
        public float _SsrThicknessScale;
        public float _SsrThicknessBias;
        public int _SsrIterLimit;

        public float _SsrRoughnessFadeEnd;
        public float _SsrRoughnessFadeRcpLength;
        public float _SsrRoughnessFadeEndTimesRcpLength;
        public float _SsrEdgeFadeRcpLength;

        public int _SsrDepthPyramidMaxMip;
        public int _SsrColorPyramidMaxMip;
        public int _SsrReflectsSky;
        // public Vector4 _ColorPyramidUvScaleAndLimitPrevFrame;
    }
}
