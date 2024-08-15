using UnityEngine;
using UnityEngine.Rendering;

namespace UniversalScreenSpaceReflection
{
    [CreateAssetMenu(menuName = "UniversalSSR/Settings")]
    public class ScreenSpaceReflectionSettings : ScriptableObject
    {
        [Header("Shaders")]
        public ComputeShader depthPyramidCS;
        public ComputeShader screenSpaceReflectionsCS;

        [Header("General")]
        [Tooltip("Enable Screen Space Reflections.")]
        public bool enabled = true;

        [Range(0.0f, 1.0f)] public float minSmoothness = 0.9f;
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

        [Range(0, 1024)] public int rayMaxIterations = 128;

    }
}
