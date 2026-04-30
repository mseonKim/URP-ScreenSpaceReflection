using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UniversalScreenSpaceReflection
{
    /// <summary>
    /// Volume override for the URP Screen Space Reflection renderer feature.
    /// All user-tunable parameters are exposed here so they can be authored per-scene/area
    /// via the URP Volume framework. Resource references (compute shaders, resolve shader)
    /// remain on the <see cref="ScreenSpaceReflection"/> renderer feature itself.
    /// </summary>
    [Serializable]
    [VolumeComponentMenu("Lighting/Screen Space Reflection (URP)")]
#if UNITY_6000_0_OR_NEWER
    // VolumeRequiresRendererFeatures is only available in URP/Core for Unity 6+. In earlier
    // versions (2022 LTS / 2023) the menu still shows the override but the feature-presence
    // hint is unavailable; the renderer feature's TryResolveSettings still gates execution.
    [VolumeRequiresRendererFeatures(typeof(ScreenSpaceReflection))]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
#endif
    public sealed class ScreenSpaceReflectionVolume : VolumeComponent
    {
        public ScreenSpaceReflectionVolume()
        {
            displayName = "Screen Space Reflection (URP)";
        }

        [Tooltip("Enable Screen Space Reflections.")]
        public BoolParameter enabled = new BoolParameter(true);

        [Tooltip("Surfaces below this smoothness do not receive reflections.")]
        public ClampedFloatParameter minSmoothness = new ClampedFloatParameter(0.5f, 0f, 1f);

        [Tooltip("Smoothness value at which reflections start to fade out.")]
        public ClampedFloatParameter smoothnessFadeStart = new ClampedFloatParameter(0.9f, 0f, 1f);

        [Tooltip("When enabled, SSR handles sky reflection for opaque objects (not supported for SSR on transparent).")]
        public BoolParameter reflectSky = new BoolParameter(true);

        [Tooltip("Controls the typical thickness of objects the reflection rays may pass behind.")]
        public ClampedFloatParameter objectThickness = new ClampedFloatParameter(0.01f, 0f, 1f);

        [Tooltip("Controls the distance at which URP fades out SSR near the edge of the screen.")]
        public ClampedFloatParameter screenFadeDistance = new ClampedFloatParameter(0.1f, 0f, 1f);

        // NoInterp: integer iteration count, blending it per-frame would only introduce
        // visual noise. Volume blends step between profiles instead of interpolating.
        [Tooltip("Maximum number of ray marching iterations. Higher values trace farther reflections at increased cost.")]
        public NoInterpClampedIntParameter rayMaxIterations = new NoInterpClampedIntParameter(256, 0, 512);

        /// <summary>
        /// True when the volume entry is active in the stack and its <see cref="enabled"/>
        /// parameter has been explicitly overridden. Mirrors the pattern used by the
        /// reference URPForwardPlusVolumetricFog package.
        /// </summary>
        internal bool UsesVolumeSource()
        {
            return active && enabled.overrideState;
        }

        internal ScreenSpaceReflectionRuntimeSettings ToSettings()
        {
            return new ScreenSpaceReflectionRuntimeSettings
            {
                enabled             = enabled.value,
                minSmoothness       = minSmoothness.value,
                smoothnessFadeStart = smoothnessFadeStart.value,
                reflectSky          = reflectSky.value,
                objectThickness     = objectThickness.value,
                screenFadeDistance  = screenFadeDistance.value,
                rayMaxIterations    = rayMaxIterations.value,
            };
        }
    }

    /// <summary>
    /// Plain runtime data carrier passed from <see cref="ScreenSpaceReflectionVolume"/>
    /// into the render pass. Decouples the pass from VolumeParameter accessors and
    /// from the legacy <c>ScreenSpaceReflectionSettings</c> ScriptableObject.
    /// </summary>
    internal struct ScreenSpaceReflectionRuntimeSettings
    {
        public bool  enabled;
        public float minSmoothness;
        public float smoothnessFadeStart;
        public bool  reflectSky;
        public float objectThickness;
        public float screenFadeDistance;
        public int   rayMaxIterations;

        public bool IsActiveForRendering => enabled;
    }
}
