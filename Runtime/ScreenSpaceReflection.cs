using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
#endif

namespace UniversalScreenSpaceReflection
{
    public class ScreenSpaceReflection : ScriptableRendererFeature
    {
        // Renderer feature now only carries fixed package resources and renderer-setup info.
        // Per-scene/per-camera tunables live on ScreenSpaceReflectionVolume (Volume override).
        // The .meta file's defaultReferences pre-populates these slots when a fresh feature
        // instance is created.
        [SerializeField] private ComputeShader depthPyramidCS;
        [SerializeField] private ComputeShader screenSpaceReflectionsCS;
        [SerializeField] private Shader resolveShader;

        public RenderingMode renderingPath;

        private ScreenSpaceReflectionPass m_Pass;
        private bool m_HasLoggedMissingResources;
        private const string k_LogPrefix = "[UniversalScreenSpaceReflection]";

        /// <inheritdoc/>
        public override void Create()
        {
            if (m_Pass == null)
            {
                m_Pass = new ScreenSpaceReflectionPass();
                m_Pass.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
            }

            // Re-run Setup whenever resources change. Setup is idempotent: it disposes the
            // old engine material before creating a new one and reassigns the mip generator.
            if (depthPyramidCS != null && screenSpaceReflectionsCS != null && resolveShader != null)
                m_Pass.Setup(depthPyramidCS, screenSpaceReflectionsCS, resolveShader, renderingPath);
        }

#if UNITY_EDITOR
        // Inspector edits to the resource slots or renderingPath need to refresh the pass.
        // Mirrors the pattern used by URPForwardPlusVolumetricFog/FPVolumetricFog.OnValidate.
        private void OnValidate()
        {
            // Reset throttled warning so a re-assigned resource produces immediate feedback.
            m_HasLoggedMissingResources = false;
            Create();
        }
#endif

#if !UNITY_6000_4_OR_NEWER
#if UNITY_6000_2_OR_NEWER
        [Obsolete]
#endif
        public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
        {
#if UNITY_6000_0_OR_NEWER
    #pragma warning disable CS0618
#endif
            m_Pass.SetCameraColorTargetHandle(renderer.cameraColorTargetHandle);
#if UNITY_6000_0_OR_NEWER
    #pragma warning restore CS0618
#endif
        }
#endif

        // This method is called when setting up the renderer once per-camera.
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Skip if preview or reflection camera
            var cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
                return;

            if (!ValidateResources())
                return;

            // No active SSR Volume override -> do not enqueue. This is the migration path:
            // legacy projects that dropped a Settings ScriptableObject in but never added a
            // Volume override will now render without SSR (with no per-frame error spam).
            if (!TryResolveSettings(out var settings) || !settings.IsActiveForRendering)
                return;

            m_Pass.UpdateSettings(settings);
            m_Pass.ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Color | ScriptableRenderPassInput.Normal);
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass?.Dispose();
        }

        private bool ValidateResources()
        {
            if (depthPyramidCS != null && screenSpaceReflectionsCS != null && resolveShader != null)
            {
                m_HasLoggedMissingResources = false;
                return true;
            }

            if (!m_HasLoggedMissingResources)
            {
                Debug.LogWarning($"{k_LogPrefix} Missing required shader resources on {nameof(ScreenSpaceReflection)} renderer feature. " +
                                 $"Assign 'Depth Pyramid CS', 'Screen Space Reflections CS', and 'Resolve Shader' in the renderer feature inspector.", this);
                m_HasLoggedMissingResources = true;
            }

            return false;
        }

        private static bool TryResolveSettings(out ScreenSpaceReflectionRuntimeSettings settings)
        {
            var stack = VolumeManager.instance?.stack;
            var volume = stack?.GetComponent<ScreenSpaceReflectionVolume>();
            if (volume != null && volume.UsesVolumeSource())
            {
                settings = volume.ToSettings();
                return true;
            }

            settings = default;
            return false;
        }


        private class ScreenSpaceReflectionPass : ScriptableRenderPass
        {
            private RenderingMode m_RenderingMode;
            private ComputeShader m_DepthPyramidCS;
            private ComputeShader m_ScreenSpaceReflectionsCS;
            private RTHandle m_CameraColorTargetHandle;
            private RTHandle m_DepthPyramidHandle;
            private RTHandle m_ColorPyramidHandle;
            private RTHandle m_HitPointsHandle;
            private RTHandle m_LightingHandle;
            private MipGenerator m_MipGenerator;
            private PassData m_PassData = new PassData();
            private SSRUtils.PackedMipChainInfo m_DepthBufferMipChainInfo = new SSRUtils.PackedMipChainInfo();
            private GraphicsBuffer m_DepthPyramidMipLevelOffsetsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 15, sizeof(int) * 2);
            private Material m_ResolveMat;

            private ScreenSpaceReflectionRuntimeSettings m_Settings;

            private int m_TracingKernel;
            private int m_ReprojectionKernel;
            private int m_CopyColorKernel;

            private ProfilingSampler m_ProfilingSampler = new ProfilingSampler("ScreenSpaceReflection");
            private static readonly float[] s_ColorPyramidSizeFactor = new float[] { 1.0f, 0.5f, 0.25f, 0.25f };

            private static bool ValidatePass(PassData data, bool useRenderGraph = false)
            {
                if (!data.runtimeSettings.IsActiveForRendering)
                    return false;

                if (data.computeShader == null)
                    return false;

                if (data.renderingMode == RenderingMode.Deferred)
                {
                    if (!useRenderGraph && Shader.GetGlobalTexture("_GBuffer2") == null)
                        return false;
                }
                else
                {
                    if (Shader.GetGlobalTexture("_CameraDepthTexture") == null)
                        Shader.SetGlobalTexture("_CameraDepthTexture", Texture2D.blackTexture);

                    if (Shader.GetGlobalTexture("_CameraNormalsTexture") == null)
                        Shader.SetGlobalTexture("_CameraNormalsTexture", Texture2D.blackTexture);
                }

                return true;
            }

            /// <summary>
            /// Called once per package load and again whenever the renderer feature inspector
            /// changes a resource slot. Safely re-creates the engine material and mip generator.
            /// </summary>
            public void Setup(ComputeShader depthPyramidCS, ComputeShader ssrCS, Shader resolveShader, RenderingMode renderingMode)
            {
                if (depthPyramidCS == null || ssrCS == null || resolveShader == null)
                    return;

                // Destroy any previously-created engine material so OnValidate re-Setup doesn't leak.
                if (m_ResolveMat != null)
                {
                    CoreUtils.Destroy(m_ResolveMat);
                    m_ResolveMat = null;
                }

                m_ResolveMat = CoreUtils.CreateEngineMaterial(resolveShader);
                if (m_ResolveMat == null)
                    return;

                m_DepthPyramidCS = depthPyramidCS;
                m_ScreenSpaceReflectionsCS = ssrCS;
                m_RenderingMode = renderingMode;

                // Allocate is one-time. Re-Setup (e.g. via OnValidate) must not wipe the cached
                // mip chain because ComputePackedMipChainInfo early-outs when the viewport size
                // is unchanged, which would leave the freshly-zeroed offsets/sizes in place.
                if (m_DepthBufferMipChainInfo.mipLevelOffsets == null)
                    m_DepthBufferMipChainInfo.Allocate();

                m_MipGenerator = new MipGenerator(depthPyramidCS);
            }

            /// <summary>
            /// Called every frame from <see cref="ScreenSpaceReflection.AddRenderPasses"/> with
            /// the resolved Volume settings for the current camera.
            /// </summary>
            public void UpdateSettings(in ScreenSpaceReflectionRuntimeSettings settings)
            {
                m_Settings = settings;
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

#if !UNITY_6000_4_OR_NEWER
#if UNITY_6000_0_OR_NEWER
            [Obsolete]
#endif
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                // Enable normal texture if forward(+).
                if (m_RenderingMode == RenderingMode.Forward || m_RenderingMode == RenderingMode.ForwardPlus)
                {
                    ConfigureInput(ScriptableRenderPassInput.Normal);
                }

                // Populate runtime fields onto m_PassData for the validation check below.
                m_PassData.runtimeSettings = m_Settings;
                m_PassData.renderingMode = m_RenderingMode;
                m_PassData.computeShader = m_ScreenSpaceReflectionsCS;

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

                var colorPyramidSizeFactor = s_ColorPyramidSizeFactor[(int)UniversalRenderPipeline.asset.opaqueDownsampling];
                var colorPyramidDesc = new RenderTextureDescriptor((int)(desc.width * colorPyramidSizeFactor), (int)(desc.height * colorPyramidSizeFactor), desc.colorFormat, 0, 11);
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

                m_TracingKernel = m_ScreenSpaceReflectionsCS.FindKernel("ScreenSpaceReflectionsTracing");
                m_ReprojectionKernel = m_ScreenSpaceReflectionsCS.FindKernel("ScreenSpaceReflectionsReprojection");
                m_CopyColorKernel = m_ScreenSpaceReflectionsCS.FindKernel("CopyColorTarget");
            }

#if UNITY_6000_0_OR_NEWER
            [Obsolete]
#endif
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (!ValidatePass(m_PassData))
                    return;

                var cameraData = renderingData.cameraData;
                m_PassData.cb = new ShaderVariablesScreenSpaceReflection();
                m_PassData.mipInfo = m_DepthBufferMipChainInfo;
                UpdateSSRConstantBuffer(renderingData.cameraData.camera, m_Settings, ref m_PassData.cb, m_ColorPyramidHandle.rt.mipmapCount, m_PassData.mipInfo, cameraData.GetViewMatrix(), cameraData.GetProjectionMatrix());
                m_PassData.cameraColorTargetHandle = m_CameraColorTargetHandle;
                m_PassData.depthTexture = m_DepthPyramidHandle;
                m_PassData.colorTexture = m_ColorPyramidHandle;
                m_PassData.hitPointsTexture = m_HitPointsHandle;
                m_PassData.lightingTexture = m_LightingHandle;
                m_PassData.mipGenerator = m_MipGenerator;
                m_PassData.offsetBufferData = m_DepthBufferMipChainInfo.GetOffsetBufferData(m_DepthPyramidMipLevelOffsetsBuffer);
                m_PassData.tracingKernel = m_TracingKernel;
                m_PassData.reprojectionKernel = m_ReprojectionKernel;
                m_PassData.copyColorKernel = m_CopyColorKernel;
                m_PassData.computeShader = m_ScreenSpaceReflectionsCS;
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
#endif

            public override void OnCameraCleanup(CommandBuffer cmd)
            {
            }


            private static void ExecutePass(CommandBuffer cmd, PassData data)
            {
                var cs = data.computeShader;
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

            private static void UpdateSSRConstantBuffer(Camera camera, in ScreenSpaceReflectionRuntimeSettings settings, ref ShaderVariablesScreenSpaceReflection cb, int mipmapCount, SSRUtils.PackedMipChainInfo mipChainInfo, in Matrix4x4 viewMatrix, in Matrix4x4 projMatrix)
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
                cb._SsrColorPyramidMaxMip = mipmapCount - 1;
                cb._SsrDepthPyramidMaxMip = mipChainInfo.mipLevelCount - 1;

                var jitterMatrix = projMatrix * camera.nonJitteredProjectionMatrix.inverse;
                cb._CameraViewProjMatrix = jitterMatrix * GL.GetGPUProjectionMatrix(projMatrix, true) * viewMatrix;
                cb._InvCameraViewProjMatrix = cb._CameraViewProjMatrix.inverse;
            }


            private class PassData
            {
                public ScreenSpaceReflectionRuntimeSettings runtimeSettings;
                public ShaderVariablesScreenSpaceReflection cb;
                public RenderingMode renderingMode;
                public ComputeShader computeShader;
                public RTHandle cameraColorTargetHandle;
                public RTHandle depthTexture;
                public RTHandle colorTexture;
                public RTHandle hitPointsTexture;
                public RTHandle lightingTexture;
                public SSRUtils.PackedMipChainInfo mipInfo;
                public MipGenerator mipGenerator;
                public GraphicsBuffer offsetBufferData;
                public int tracingKernel;
                public int reprojectionKernel;
                public int copyColorKernel;
                public Vector2Int viewportSize;
                public Material resolveMat;
            }

#if UNITY_6000_0_OR_NEWER
            #region RenderGraph
            // This method adds and configures one or more render passes in the render graph.
            // This process includes declaring their inputs and outputs,
            // but does not include adding commands to command buffers.
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameContext)
            {
                // Enable normal texture if forward(+).
                if (m_RenderingMode == RenderingMode.Forward || m_RenderingMode == RenderingMode.ForwardPlus)
                {
                    ConfigureInput(ScriptableRenderPassInput.Normal);
                }

                // Populate the validation-side fields up-front so the early-out below sees them.
                m_PassData.runtimeSettings = m_Settings;
                m_PassData.renderingMode = m_RenderingMode;
                m_PassData.computeShader = m_ScreenSpaceReflectionsCS;

                if (!ValidatePass(m_PassData, true))
                    return;

                var blitParameters = new RenderGraphUtils.BlitMaterialParameters();

                using (var builder = renderGraph.AddComputePass<RenderGraphPassData>("ScreenSpace Reflection", out var passData))
                {
                    passData.runtimeSettings = m_Settings;
                    passData.computeShader = m_ScreenSpaceReflectionsCS;

                    // Get the data needed to create the list of objects to draw
                    UniversalRenderingData renderingData = frameContext.Get<UniversalRenderingData>();
                    UniversalCameraData cameraData = frameContext.Get<UniversalCameraData>();
                    UniversalLightData lightData = frameContext.Get<UniversalLightData>();
                    UniversalResourceData resourceData = frameContext.Get<UniversalResourceData>();

                    builder.AllowGlobalStateModification(true);
                    builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                    builder.UseTexture(resourceData.cameraNormalsTexture, AccessFlags.Read);
                    builder.UseTexture(resourceData.cameraColor, AccessFlags.Read);
                    if (resourceData.gBuffer != null && resourceData.gBuffer[2].IsValid())
                    {
                        builder.UseTexture(resourceData.gBuffer[2], AccessFlags.Read);
                        passData.gBuffer2 = resourceData.gBuffer[2];
                    }

                    var desc = resourceData.cameraColor.GetDescriptor(renderGraph);
                    var nonScaledViewport = new Vector2Int(desc.width, desc.height);
                    m_DepthBufferMipChainInfo.ComputePackedMipChainInfo(nonScaledViewport);

                    var depthMipchainSize = m_DepthBufferMipChainInfo.textureSize;
                    var depthPyramidDesc = new RenderTextureDescriptor(depthMipchainSize.x, depthMipchainSize.y, RenderTextureFormat.RFloat);
                    depthPyramidDesc.enableRandomWrite = true;
                    depthPyramidDesc.sRGB = false;
                    var depthPyramidHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthPyramidDesc, "CameraDepthBufferMipChain", false);
                    builder.UseTexture(depthPyramidHandle, AccessFlags.ReadWrite);

                    var colorPyramidSizeFactor = s_ColorPyramidSizeFactor[(int)UniversalRenderPipeline.asset.opaqueDownsampling];
                    var colorPyramidDesc = new RenderTextureDescriptor((int)(desc.width * colorPyramidSizeFactor), (int)(desc.height * colorPyramidSizeFactor), desc.colorFormat, 0, 11);
                    var colorPyramidTextureDesc = new TextureDesc(colorPyramidDesc);
                    colorPyramidTextureDesc.enableRandomWrite = true;
                    colorPyramidTextureDesc.useMipMap = true;
                    colorPyramidTextureDesc.autoGenerateMips = false;
                    colorPyramidTextureDesc.name = "CameraColorBufferMipChain";
                    var colorPyramidHandle = renderGraph.CreateTexture(colorPyramidTextureDesc);
                    builder.UseTexture(colorPyramidHandle, AccessFlags.ReadWrite);

                    var hitPointsDesc = new RenderTextureDescriptor(desc.width, desc.height, colorFormat:UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16_UNorm, 0);
                    hitPointsDesc.enableRandomWrite = true;
                    hitPointsDesc.sRGB = false;
                    var hitPointsHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, hitPointsDesc, "SSR_Hit_Point_Texture", false);
                    builder.UseTexture(hitPointsHandle, AccessFlags.ReadWrite);

                    var lightingDesc = new RenderTextureDescriptor(desc.width, desc.height, colorFormat:UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, 0);
                    lightingDesc.enableRandomWrite = true;
                    lightingDesc.sRGB = false;
                    var lightingHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, lightingDesc, "SSR_Lighting_Texture", false);
                    builder.UseTexture(lightingHandle, AccessFlags.ReadWrite);

                    m_TracingKernel = m_ScreenSpaceReflectionsCS.FindKernel("ScreenSpaceReflectionsTracing");
                    m_ReprojectionKernel = m_ScreenSpaceReflectionsCS.FindKernel("ScreenSpaceReflectionsReprojection");
                    m_CopyColorKernel = m_ScreenSpaceReflectionsCS.FindKernel("CopyColorTarget");

                    passData.cb = new ShaderVariablesScreenSpaceReflection();
                    passData.mipInfo = m_DepthBufferMipChainInfo;
                    UpdateSSRConstantBuffer(cameraData.camera, m_Settings, ref passData.cb, colorPyramidDesc.mipCount, passData.mipInfo, cameraData.GetViewMatrix(), cameraData.GetProjectionMatrix());
                    passData.renderingMode = m_RenderingMode;
                    passData.cameraColorTargetHandle = resourceData.cameraColor;
                    passData.depthTexture = depthPyramidHandle;
                    passData.colorTexture = colorPyramidHandle;
                    passData.hitPointsTexture = hitPointsHandle;
                    passData.lightingTexture = lightingHandle;
                    passData.mipGenerator = m_MipGenerator;
                    passData.tracingKernel = m_TracingKernel;
                    passData.reprojectionKernel = m_ReprojectionKernel;
                    passData.copyColorKernel = m_CopyColorKernel;
                    passData.depthVolumeDepth = depthPyramidDesc.volumeDepth;
                    var cameraTargetDescriptor = passData.cameraColorTargetHandle.GetDescriptor(renderGraph);
                    passData.viewportSize = new Vector2Int(cameraTargetDescriptor.width, cameraTargetDescriptor.height);
                    passData.resolveMat = m_ResolveMat;

                    // Import the structured-buffer once per frame and store the BufferHandle (not
                    // the raw GraphicsBuffer) on passData. RenderGraph validates compute resource
                    // bindings against the registered handles; passing a raw GraphicsBuffer
                    // captured at recording time triggers
                    //   "Compute shader (...): Property (_DepthPyramidMipLevelOffsets) at kernel
                    //    index (0) is not set"
                    // even when SetComputeBufferParam was called.
                    passData.offsetBufferHandle = renderGraph.ImportBuffer(
                        m_DepthBufferMipChainInfo.GetOffsetBufferData(m_DepthPyramidMipLevelOffsetsBuffer));
                    builder.UseBuffer(passData.offsetBufferHandle, AccessFlags.Read);
                    builder.SetRenderFunc((RenderGraphPassData data, ComputeGraphContext context) => ExecutePass(context.cmd, data));

                    // Use member pass data to transfer blit parameters.
                    blitParameters.source = passData.lightingTexture;
                    blitParameters.destination = passData.cameraColorTargetHandle;
                    blitParameters.material = passData.resolveMat;
                }

                using (var builder = renderGraph.AddRasterRenderPass<RenderGraphPassData>("ScreenSpace Reflection", out var passData))
                {
                    // Resolve
                    builder.UseTexture(blitParameters.source, AccessFlags.Read);
                    builder.SetRenderAttachment(blitParameters.destination, 0);
                    builder.SetRenderFunc((RenderGraphPassData data, RasterGraphContext context) =>
                    {
                        if (blitParameters.material != null)
                        {
                            Blitter.BlitTexture(context.cmd, blitParameters.source, Vector2.one, blitParameters.material, 0);
                        }
                    });
                }
            }

            private static void ExecutePass(ComputeCommandBuffer cmd, RenderGraphPassData data)
            {
                var cs = data.computeShader;
                if (cs == null)
                    return;

                using (new ProfilingScope(cmd, new ProfilingSampler("Depth Pyramid")))
                {
                    data.mipGenerator.RenderMinDepthPyramid(cmd, data.depthTexture, data.mipInfo, data.depthVolumeDepth, false);
                }

                var deferredKeyword = new LocalKeyword(cs, "SSR_DEFERRED");

                using (new ProfilingScope(cmd, new ProfilingSampler("SSR Tracing")))
                {
                    // Set keyword to use different normal texture based on rendering mode.
                    cmd.SetKeyword(cs, deferredKeyword, data.renderingMode == RenderingMode.Deferred);

                    if (data.renderingMode == RenderingMode.Deferred)
                    {
                        cmd.SetComputeTextureParam(cs, data.tracingKernel, "_GBuffer2", data.gBuffer2);
                    }

                    cmd.SetComputeTextureParam(cs, data.tracingKernel, "_DepthPyramidTexture", data.depthTexture);
                    cmd.SetComputeTextureParam(cs, data.tracingKernel, "_SsrHitPointTexture", data.hitPointsTexture);

                    // BufferHandle has implicit operator GraphicsBuffer that resolves through
                    // RenderGraphResourceRegistry at execute time, so SetComputeBufferParam
                    // sees the same buffer the RG validator tracks.
                    cmd.SetComputeBufferParam(cs, data.tracingKernel, ShaderIDs._DepthPyramidMipLevelOffsets, data.offsetBufferHandle);

                    ConstantBuffer.Push(data.cb, cs, ShaderIDs._ShaderVariablesScreenSpaceReflection);

                    cmd.DispatchCompute(cs, data.tracingKernel, SSRUtils.DivRoundUp(data.viewportSize.x, 8), SSRUtils.DivRoundUp(data.viewportSize.y, 8), 1);
                }

                using (new ProfilingScope(cmd, new ProfilingSampler("SSR Reprojection")))
                {
                    // Set keyword to use different normal texture based on rendering mode.
                    cmd.SetKeyword(cs, deferredKeyword, data.renderingMode == RenderingMode.Deferred);

                    if (data.renderingMode == RenderingMode.Deferred)
                    {
                        cmd.SetComputeTextureParam(cs, data.reprojectionKernel, "_GBuffer2", data.gBuffer2);
                    }

                    // Create color mip chain
                    cmd.SetComputeTextureParam(cs, data.copyColorKernel, ShaderIDs._CameraColorTexture, data.cameraColorTargetHandle);
                    cmd.SetComputeTextureParam(cs, data.copyColorKernel, ShaderIDs._CopiedColorPyramidTexture, data.colorTexture);
                    cmd.DispatchCompute(cs, data.copyColorKernel, SSRUtils.DivRoundUp(data.viewportSize.x, 8), SSRUtils.DivRoundUp(data.viewportSize.y, 8), 1);

                    RenderTexture rt = data.colorTexture;
                    rt.GenerateMips();

                    // Bind resources
                    cmd.SetComputeTextureParam(cs, data.reprojectionKernel, ShaderIDs._DepthPyramidTexture, data.depthTexture);
                    cmd.SetComputeTextureParam(cs, data.reprojectionKernel, ShaderIDs._ColorPyramidTexture, data.colorTexture);
                    cmd.SetComputeTextureParam(cs, data.reprojectionKernel, ShaderIDs._SsrHitPointTexture, data.hitPointsTexture);
                    cmd.SetComputeTextureParam(cs, data.reprojectionKernel, ShaderIDs._SSRAccumTexture, data.lightingTexture);

                    cmd.SetComputeBufferParam(cs, data.reprojectionKernel, ShaderIDs._DepthPyramidMipLevelOffsets, data.offsetBufferHandle);

                    ConstantBuffer.Push(data.cb, cs, ShaderIDs._ShaderVariablesScreenSpaceReflection);

                    cmd.DispatchCompute(cs, data.reprojectionKernel, SSRUtils.DivRoundUp(data.viewportSize.x, 8), SSRUtils.DivRoundUp(data.viewportSize.y, 8), 1);
                }
            }

            private class RenderGraphPassData
            {
                public ScreenSpaceReflectionRuntimeSettings runtimeSettings;
                public ShaderVariablesScreenSpaceReflection cb;
                public RenderingMode renderingMode;
                public ComputeShader computeShader;
                public TextureHandle cameraColorTargetHandle;
                public TextureHandle depthTexture;
                public TextureHandle colorTexture;
                public TextureHandle hitPointsTexture;
                public TextureHandle lightingTexture;
                public TextureHandle gBuffer2;
                public SSRUtils.PackedMipChainInfo mipInfo;
                public MipGenerator mipGenerator;
                public BufferHandle offsetBufferHandle;
                public int tracingKernel;
                public int reprojectionKernel;
                public int copyColorKernel;
                public int depthVolumeDepth;
                public Vector2Int viewportSize;
                public Material resolveMat;
            }
            #endregion
#endif
        }
    }
}
