using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace UniversalScreenSpaceReflection
{
    internal class MipGenerator
    {
        private ComputeShader m_DepthPyramidCS;
        private int m_CopyDepthToMip0Kernel;
        private int m_DepthDownsampleKernel;

        private int[] m_SrcOffset;
        private int[] m_DstOffset;

        public MipGenerator(ComputeShader depthPyramidCS)
        {
            m_DepthPyramidCS = depthPyramidCS;
            m_CopyDepthToMip0Kernel = m_DepthPyramidCS.FindKernel("KCopyDepthTextureToMipChain");
            m_DepthDownsampleKernel = m_DepthPyramidCS.FindKernel("KDepthDownsample8DualUav");

            m_SrcOffset = new int[4];
            m_DstOffset = new int[4];
        }

        // Generates an in-place depth pyramid
        // TODO: Mip-mapping depth is problematic for precision at lower mips, generate a packed atlas instead
        public void RenderMinDepthPyramid(CommandBuffer cmd, RenderTexture texture, SSRUtils.PackedMipChainInfo info, bool mip1AlreadyComputed)
        {
            SSRUtils.CheckRTCreated(texture);

            var cs = m_DepthPyramidCS;
            int kernel = m_CopyDepthToMip0Kernel;

            // Copy depth texture to mip0.
            {
                Vector2Int dstSize = info.mipLevelSizes[0];
                cmd.SetComputeIntParams(cs, ShaderIDs._SrcOffsetAndLimit, m_SrcOffset);
                cmd.SetComputeIntParams(cs, ShaderIDs._DstOffset, m_DstOffset);
                cmd.SetComputeTextureParam(cs, kernel, ShaderIDs._DepthMipChain, texture);

                cmd.DispatchCompute(cs, kernel, SSRUtils.DivRoundUp(dstSize.x, 8), SSRUtils.DivRoundUp(dstSize.y, 8), texture.volumeDepth);
            }

            kernel = m_DepthDownsampleKernel;
            // TODO: Do it 1x MIP at a time for now. In the future, do 4x MIPs per pass, or even use a single pass.
            // Note: Gather() doesn't take a LOD parameter and we cannot bind an SRV of a MIP level,
            // and we don't support Min samplers either. So we are forced to perform 4x loads.
            for (int i = 1; i < info.mipLevelCount; i++)
            {
                if (mip1AlreadyComputed && i == 1) continue;

                Vector2Int dstSize = info.mipLevelSizes[i];
                Vector2Int dstOffset = info.mipLevelOffsets[i];
                Vector2Int srcSize = info.mipLevelSizes[i - 1];
                Vector2Int srcOffset = info.mipLevelOffsets[i - 1];
                Vector2Int srcLimit = srcOffset + srcSize - Vector2Int.one;

                m_SrcOffset[0] = srcOffset.x;
                m_SrcOffset[1] = srcOffset.y;
                m_SrcOffset[2] = srcLimit.x;
                m_SrcOffset[3] = srcLimit.y;

                m_DstOffset[0] = dstOffset.x;
                m_DstOffset[1] = dstOffset.y;
                m_DstOffset[2] = 0;
                m_DstOffset[3] = 0;

                cmd.SetComputeIntParams(cs, ShaderIDs._SrcOffsetAndLimit, m_SrcOffset);
                cmd.SetComputeIntParams(cs, ShaderIDs._DstOffset, m_DstOffset);
                cmd.SetComputeTextureParam(cs, kernel, ShaderIDs._DepthMipChain, texture);

                cmd.DispatchCompute(cs, kernel, SSRUtils.DivRoundUp(dstSize.x, 8), SSRUtils.DivRoundUp(dstSize.y, 8), texture.volumeDepth);
            }
        }

#if UNITY_6000_0_OR_NEWER
        #region RenderGraph
        public void RenderMinDepthPyramid(ComputeCommandBuffer cmd, TextureHandle texture, SSRUtils.PackedMipChainInfo info, int volumeDepth, bool mip1AlreadyComputed)
        {
            SSRUtils.CheckRTCreated(texture);

            var cs = m_DepthPyramidCS;
            int kernel = m_CopyDepthToMip0Kernel;

            // Copy depth texture to mip0.
            {
                Vector2Int dstSize = info.mipLevelSizes[0];
                cmd.SetComputeIntParams(cs, ShaderIDs._SrcOffsetAndLimit, m_SrcOffset);
                cmd.SetComputeIntParams(cs, ShaderIDs._DstOffset, m_DstOffset);
                cmd.SetComputeTextureParam(cs, kernel, ShaderIDs._DepthMipChain, texture);

                cmd.DispatchCompute(cs, kernel, SSRUtils.DivRoundUp(dstSize.x, 8), SSRUtils.DivRoundUp(dstSize.y, 8), volumeDepth);
            }

            kernel = m_DepthDownsampleKernel;
            // TODO: Do it 1x MIP at a time for now. In the future, do 4x MIPs per pass, or even use a single pass.
            // Note: Gather() doesn't take a LOD parameter and we cannot bind an SRV of a MIP level,
            // and we don't support Min samplers either. So we are forced to perform 4x loads.
            for (int i = 1; i < info.mipLevelCount; i++)
            {
                if (mip1AlreadyComputed && i == 1) continue;

                Vector2Int dstSize = info.mipLevelSizes[i];
                Vector2Int dstOffset = info.mipLevelOffsets[i];
                Vector2Int srcSize = info.mipLevelSizes[i - 1];
                Vector2Int srcOffset = info.mipLevelOffsets[i - 1];
                Vector2Int srcLimit = srcOffset + srcSize - Vector2Int.one;

                m_SrcOffset[0] = srcOffset.x;
                m_SrcOffset[1] = srcOffset.y;
                m_SrcOffset[2] = srcLimit.x;
                m_SrcOffset[3] = srcLimit.y;

                m_DstOffset[0] = dstOffset.x;
                m_DstOffset[1] = dstOffset.y;
                m_DstOffset[2] = 0;
                m_DstOffset[3] = 0;

                cmd.SetComputeIntParams(cs, ShaderIDs._SrcOffsetAndLimit, m_SrcOffset);
                cmd.SetComputeIntParams(cs, ShaderIDs._DstOffset, m_DstOffset);
                cmd.SetComputeTextureParam(cs, kernel, ShaderIDs._DepthMipChain, texture);

                cmd.DispatchCompute(cs, kernel, SSRUtils.DivRoundUp(dstSize.x, 8), SSRUtils.DivRoundUp(dstSize.y, 8), volumeDepth);
            }
        }
        #endregion
#endif
    } 
}