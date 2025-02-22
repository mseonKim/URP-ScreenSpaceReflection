using UnityEngine;

namespace UniversalScreenSpaceReflection
{
    internal struct ShaderVariablesScreenSpaceReflection
    {
        public float _SsrThicknessScale;
        public float _SsrThicknessBias;
        public int _SsrIterLimit;
        public int _SsrPad0_;

        public float _SsrRoughnessFadeEnd;
        public float _SsrRoughnessFadeRcpLength;
        public float _SsrRoughnessFadeEndTimesRcpLength;
        public float _SsrEdgeFadeRcpLength;

        public int _SsrDepthPyramidMaxMip;
        public int _SsrColorPyramidMaxMip;
        public int _SsrReflectsSky;
        public int _SsrPad1_;

        public Matrix4x4 _CameraViewProjMatrix;
        public Matrix4x4 _InvCameraViewProjMatrix;
        // public Vector4 _ColorPyramidUvScaleAndLimitPrevFrame;
    }
}
