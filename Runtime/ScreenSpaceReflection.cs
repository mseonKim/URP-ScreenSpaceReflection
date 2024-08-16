using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UniversalScreenSpaceReflection
{
    public class ScreenSpaceReflection : ScriptableRendererFeature
    {
        public ScreenSpaceReflectionSettings settings;
        public RenderingMode renderingPath;
        private ScreenSpaceReflectionPass m_Pass;

        /// <inheritdoc/>
        public override void Create()
        {
            m_Pass = new ScreenSpaceReflectionPass();
            m_Pass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
            m_Pass.Setup(settings, renderingPath);
        }

        public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
        {
            m_Pass.SetCameraColorTargetHandle(renderer.cameraColorTargetHandle);
        }

        // This method is called when setting up the renderer once per-camera.
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(m_Pass);
        }
        
        protected override void Dispose(bool disposing)
        {
            m_Pass.Dispose();
        }


        private class ScreenSpaceReflectionPass : ScriptableRenderPass
        {
            private RenderingMode m_RenderingMode;
            private RTHandle m_CameraColorTargetHandle;
            private RTHandle m_DepthPyramidHandle;
            private RTHandle m_ColorPyramidHandle;
            private RTHandle m_HitPointsHandle;
            private RTHandle m_LightingHandle;
            private MipGenerator m_MipGenerator;
            private PassData m_PassData = new PassData();
            private SSRUtils.PackedMipChainInfo m_DepthBufferMipChainInfo = new SSRUtils.PackedMipChainInfo();
            private ComputeBuffer m_DepthPyramidMipLevelOffsetsBuffer = new ComputeBuffer(15, sizeof(int) * 2);
            private Material m_ResolveMat;

            private int m_TracingKernel;
            private int m_ReprojectionKernel;
            private int m_CopyColorKernel;

            private ProfilingSampler m_ProfilingSampler = new ProfilingSampler("ScreenSpaceReflection");

            private bool ValidatePass(PassData data)
            {
                if (data.settings == null || !data.settings.enabled)
                    return false;
                
                if (data.renderingMode != RenderingMode.Deferred)
                {
                    var isValid = Shader.GetGlobalTexture("_CameraDepthTexture") != null && Shader.GetGlobalTexture("_CameraNormalsTexture") != null;
                    if (!isValid)
                        return false;
                }

                return true;
            }

            public void Setup(ScreenSpaceReflectionSettings settings, RenderingMode renderingMode)
            {
                if (settings == null || settings.depthPyramidCS == null || settings.screenSpaceReflectionsCS == null)
                    return;

                m_ResolveMat = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/SSR_Resolver"));
                if (m_ResolveMat == null)
                    return;
                    
                m_PassData.settings = settings;
                m_RenderingMode = renderingMode;
                m_DepthBufferMipChainInfo.Allocate();
                m_MipGenerator = new MipGenerator(settings.depthPyramidCS);
            }

            public void SetCameraColorTargetHandle(RTHandle colorTargetHandle)
            {
                m_CameraColorTargetHandle = colorTargetHandle;
            }

            public void Dispose()
            {
                CoreUtils.SafeRelease(m_DepthPyramidMipLevelOffsetsBuffer);
                CoreUtils.Destroy(m_ResolveMat);
                m_DepthPyramidHandle?.Release();
                m_ColorPyramidHandle?.Release();
                m_HitPointsHandle?.Release();
                m_LightingHandle?.Release();
            }

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                // Enable normal texture if forward(+).
                if (m_RenderingMode == RenderingMode.Forward || m_RenderingMode == RenderingMode.ForwardPlus)
                {
                    ConfigureInput(ScriptableRenderPassInput.Normal);
                }
                
                if (!ValidatePass(m_PassData))
                    return;

                var desc = m_CameraColorTargetHandle.rt.descriptor;
                var nonScaledViewport = new Vector2Int(desc.width, desc.height);
                m_DepthBufferMipChainInfo.ComputePackedMipChainInfo(nonScaledViewport);
                
                var depthMipchainSize = m_DepthBufferMipChainInfo.textureSize;
                var depthPyramidDesc = new RenderTextureDescriptor(depthMipchainSize.x, depthMipchainSize.y, RenderTextureFormat.RFloat);
                depthPyramidDesc.enableRandomWrite = true;
                depthPyramidDesc.sRGB = false;
                RenderingUtils.ReAllocateIfNeeded(ref m_DepthPyramidHandle, depthPyramidDesc, name:"CameraDepthBufferMipChain");

                var colorPyramidDesc = new RenderTextureDescriptor(desc.width, desc.height, desc.colorFormat, 0);
                colorPyramidDesc.enableRandomWrite = true;
                colorPyramidDesc.useMipMap = true;
                colorPyramidDesc.autoGenerateMips = false;
                RenderingUtils.ReAllocateIfNeeded(ref m_ColorPyramidHandle, colorPyramidDesc, name:"CameraColorBufferMipChain");

                var hitPointsDesc = new RenderTextureDescriptor(desc.width, desc.height, colorFormat:UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16_UNorm, 0);
                hitPointsDesc.enableRandomWrite = true;
                hitPointsDesc.sRGB = false;
                RenderingUtils.ReAllocateIfNeeded(ref m_HitPointsHandle, hitPointsDesc, name:"SSR_Hit_Point_Texture");

                var lightingDesc = new RenderTextureDescriptor(desc.width, desc.height, colorFormat:UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, 0);
                lightingDesc.enableRandomWrite = true;
                lightingDesc.sRGB = false;
                RenderingUtils.ReAllocateIfNeeded(ref m_LightingHandle, lightingDesc, name:"SSR_Lighting_Texture");

                m_TracingKernel = m_PassData.settings.screenSpaceReflectionsCS.FindKernel("ScreenSpaceReflectionsTracing");
                m_ReprojectionKernel = m_PassData.settings.screenSpaceReflectionsCS.FindKernel("ScreenSpaceReflectionsReprojection");
                m_CopyColorKernel = m_PassData.settings.screenSpaceReflectionsCS.FindKernel("CopyColorTarget");
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (!ValidatePass(m_PassData))
                    return;

                m_PassData.cb = new ShaderVariablesScreenSpaceReflection();
                UpdateSSRConstantBuffer(renderingData.cameraData.camera, m_PassData.settings, ref m_PassData.cb, m_PassData.mipInfo);
                m_PassData.renderingMode = m_RenderingMode;
                m_PassData.cameraColorTargetHandle = m_CameraColorTargetHandle;
                m_PassData.depthTexture = m_DepthPyramidHandle;
                m_PassData.colorTexture = m_ColorPyramidHandle;
                m_PassData.hitPointsTexture = m_HitPointsHandle;
                m_PassData.lightingTexture = m_LightingHandle;
                m_PassData.mipInfo = m_DepthBufferMipChainInfo;
                m_PassData.mipGenerator = m_MipGenerator;
                m_PassData.offsetBufferData = m_DepthBufferMipChainInfo.GetOffsetBufferData(m_DepthPyramidMipLevelOffsetsBuffer);
                m_PassData.tracingKernel = m_TracingKernel;
                m_PassData.reprojectionKernel = m_ReprojectionKernel;
                m_PassData.copyColorKernel = m_CopyColorKernel;
                var cameraTargetDescriptor = m_PassData.cameraColorTargetHandle.rt.descriptor;
                m_PassData.viewportSize = new Vector2Int(cameraTargetDescriptor.width, cameraTargetDescriptor.height);
                m_PassData.resolveMat = m_ResolveMat;

                CommandBuffer cmd = CommandBufferPool.Get();
                using (new ProfilingScope(cmd, m_ProfilingSampler))
                {
                    context.ExecuteCommandBuffer(cmd);
                    cmd.Clear();
                    ExecutePass(cmd, m_PassData);
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }

            public override void OnCameraCleanup(CommandBuffer cmd)
            {
            }


            private static void ExecutePass(CommandBuffer cmd, PassData data)
            {
                var cs = data.settings.screenSpaceReflectionsCS;
                if (cs == null)
                    return;

                using (new ProfilingScope(cmd, new ProfilingSampler("Depth Pyramid")))
                {
                    data.mipGenerator.RenderMinDepthPyramid(cmd, data.depthTexture, data.mipInfo, false);
                }
                
                var deferredKeyword = new LocalKeyword(cs, "SSR_DEFERRED");

                using (new ProfilingScope(cmd, new ProfilingSampler("SSR Tracing")))
                {
                    // Set keyword to use different normal texture based on rendering mode.
                    cmd.SetKeyword(cs, deferredKeyword, data.renderingMode == RenderingMode.Deferred);

                    cmd.SetComputeTextureParam(cs, data.tracingKernel, ShaderIDs._DepthPyramidTexture, data.depthTexture);
                    cmd.SetComputeTextureParam(cs, data.tracingKernel, ShaderIDs._SsrHitPointTexture, data.hitPointsTexture);

                    cmd.SetComputeBufferParam(cs, data.tracingKernel, ShaderIDs._DepthPyramidMipLevelOffsets, data.offsetBufferData);

                    ConstantBuffer.Push(cmd, data.cb, cs, ShaderIDs._ShaderVariablesScreenSpaceReflection);

                    cmd.DispatchCompute(cs, data.tracingKernel, SSRUtils.DivRoundUp(data.viewportSize.x, 8), SSRUtils.DivRoundUp(data.viewportSize.y, 8), 1);
                }

                using (new ProfilingScope(cmd, new ProfilingSampler("SSR Reprojection")))
                {
                    // Set keyword to use different normal texture based on rendering mode.
                    cmd.SetKeyword(cs, deferredKeyword, data.renderingMode == RenderingMode.Deferred);

                    // Create color mip chain
                    cmd.SetComputeTextureParam(cs, data.copyColorKernel, ShaderIDs._CameraColorTexture, data.cameraColorTargetHandle);
                    cmd.SetComputeTextureParam(cs, data.copyColorKernel, ShaderIDs._CopiedColorPyramidTexture, data.colorTexture);
                    cmd.DispatchCompute(cs, data.copyColorKernel, SSRUtils.DivRoundUp(data.viewportSize.x, 8), SSRUtils.DivRoundUp(data.viewportSize.y, 8), 1);
                    cmd.GenerateMips(data.colorTexture);

                    // Bind resources
                    cmd.SetComputeTextureParam(cs, data.reprojectionKernel, ShaderIDs._DepthPyramidTexture, data.depthTexture);
                    cmd.SetComputeTextureParam(cs, data.reprojectionKernel, ShaderIDs._ColorPyramidTexture, data.colorTexture);
                    cmd.SetComputeTextureParam(cs, data.reprojectionKernel, ShaderIDs._SsrHitPointTexture, data.hitPointsTexture);
                    cmd.SetComputeTextureParam(cs, data.reprojectionKernel, ShaderIDs._SSRAccumTexture, data.lightingTexture);

                    cmd.SetComputeBufferParam(cs, data.reprojectionKernel, ShaderIDs._DepthPyramidMipLevelOffsets, data.offsetBufferData);

                    ConstantBuffer.Push(cmd, data.cb, cs, ShaderIDs._ShaderVariablesScreenSpaceReflection);

                    cmd.DispatchCompute(cs, data.reprojectionKernel, SSRUtils.DivRoundUp(data.viewportSize.x, 8), SSRUtils.DivRoundUp(data.viewportSize.y, 8), 1);
                }

                // Resolve
                if (data.resolveMat != null)
                {
                    Blitter.BlitCameraTexture(cmd, data.lightingTexture, data.cameraColorTargetHandle, data.resolveMat, 0);
                }
            }

            private void UpdateSSRConstantBuffer(Camera camera, ScreenSpaceReflectionSettings settings, ref ShaderVariablesScreenSpaceReflection cb, SSRUtils.PackedMipChainInfo mipChainInfo)
            {
                float n = camera.nearClipPlane;
                float f = camera.farClipPlane;
                float thickness = settings.objectThickness;

                cb._SsrThicknessScale = 1.0f / (1.0f + thickness);
                cb._SsrThicknessBias = -n / (f - n) * (thickness * cb._SsrThicknessScale);
                cb._SsrIterLimit = settings.rayMaxIterations;
                // Note that the sky is still visible, it just takes its value from reflection probe/skybox rather than on screen.
                cb._SsrReflectsSky = settings.reflectSky ? 1 : 0;
                float roughnessFadeStart = 1 - settings.smoothnessFadeStart;
                cb._SsrRoughnessFadeEnd = 1 - settings.minSmoothness;
                float roughnessFadeLength = cb._SsrRoughnessFadeEnd - roughnessFadeStart;
                cb._SsrRoughnessFadeEndTimesRcpLength = (roughnessFadeLength != 0) ? (cb._SsrRoughnessFadeEnd * (1.0f / roughnessFadeLength)) : 1;
                cb._SsrRoughnessFadeRcpLength = (roughnessFadeLength != 0) ? (1.0f / roughnessFadeLength) : 0;
                cb._SsrEdgeFadeRcpLength = Mathf.Min(1.0f / settings.screenFadeDistance, float.MaxValue);
                // cb._ColorPyramidUvScaleAndLimitPrevFrame = SSRUtils.ComputeViewportScaleAndLimit(camera.historyRTHandleProperties.previousViewportSize, camera.historyRTHandleProperties.previousRenderTargetSize);
                cb._SsrColorPyramidMaxMip = m_ColorPyramidHandle.rt.mipmapCount - 1;
                cb._SsrDepthPyramidMaxMip = mipChainInfo.mipLevelCount - 1;
            }


            private class PassData
            {
                public ScreenSpaceReflectionSettings settings;
                public ShaderVariablesScreenSpaceReflection cb;
                public RenderingMode renderingMode;
                public RTHandle cameraColorTargetHandle;
                public RTHandle depthTexture;
                public RTHandle colorTexture;
                public RTHandle hitPointsTexture;
                public RTHandle lightingTexture;
                public SSRUtils.PackedMipChainInfo mipInfo;
                public MipGenerator mipGenerator;
                public ComputeBuffer offsetBufferData;
                public int tracingKernel;
                public int reprojectionKernel;
                public int copyColorKernel;
                public Vector2Int viewportSize;
                public Material resolveMat;
            }
        }
    }
}


