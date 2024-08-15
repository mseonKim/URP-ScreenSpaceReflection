# To Use in Forward(+) mode

Since URP Forward rendering does not store the smoothness value to the normal texture, it needs to customize `DepthNormalPass`. 
Use a custom shader to store smoothness value into `_CameraNormalsTexture.a`.
See the reference shader in the sample project.


# Limitations

1. XR Not supported
2. Transparent Objects Not supported

# Differences from HDRP

1. Assume SSR_APPROX keyword is enabled. (No support PBR Accumulation mode due to performance.)
2. Stencil Check is excluded.
3. ClearCoatMask is excluded since it's only in HDRP.
4. Motion Vector is excluded.