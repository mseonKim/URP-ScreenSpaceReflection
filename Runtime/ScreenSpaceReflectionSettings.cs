using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace UniversalScreenSpaceReflection
{
    /// <summary>
    /// Legacy settings ScriptableObject. Replaced by <see cref="ScreenSpaceReflectionVolume"/>
    /// (Volume override) for tunable parameters and by inline [SerializeField] slots on the
    /// <see cref="ScreenSpaceReflection"/> renderer feature for shader resource references.
    /// Retained as a type so existing .asset files continue to deserialize without breaking
    /// project-side compilation. The renderer feature no longer reads from this asset.
    /// </summary>
    [Obsolete("ScreenSpaceReflectionSettings is replaced by ScreenSpaceReflectionVolume (Volume override). " +
              "Move tunable parameters into a Volume profile. ComputeShader/Shader references now live on " +
              "the ScreenSpaceReflection renderer feature directly.")]
    public class ScreenSpaceReflectionSettings : ScriptableObject
    {
        [Header("Shaders")]
        public ComputeShader depthPyramidCS;
        public ComputeShader screenSpaceReflectionsCS;

        [Header("General")]
        [Tooltip("Enable Screen Space Reflections.")]
        public bool enabled = true;

        [Range(0.0f, 1.0f)] public float minSmoothness = 0.5f;
        [Range(0.0f, 1.0f)] public float smoothnessFadeStart = 0.9f;

        [Header("Ray Marching")]
        /// <summary>
        /// When enabled, SSR handles sky reflection for opaque objects (not supported for SSR on transparent).
        /// </summary>
        public bool reflectSky = true;

        // SSR Data
        /// <summary>
        /// Controls the distance at which URP fades out SSR near the edge of the screen.
        /// </summary>
        [Range(0.0f, 1.0f)] public float objectThickness = 0.01f;

        /// <summary>
        /// Controls the typical thickness of objects the reflection rays may pass behind.
        /// </summary>
        [Range(0.0f, 1.0f)] public float screenFadeDistance = 0.1f;

        [Range(0, 512)] public int rayMaxIterations = 256;

    }
}
