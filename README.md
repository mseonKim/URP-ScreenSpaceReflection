# Universal ScreenSpace Reflection
![SSR Sample](./Documentation~/SSR.png)

This repository is ported from Unity HDRP ScreenSpace Reflection.

## How To Use

![HowToUse](./Documentation~/HowToUse.png)

1. Add `ScreenSpaceReflection` renderer feature to Renderer data.
2. Set the rendering path to your rendering mode.
3. Link the settings asset to the renderer feature.
(You can create the settings asset via `Create/UniversalSSR/Settings`.)

    ![HowToUse_Settings](./Documentation~/HowToUse_Settings.png)


### In Forward(+) Rendering Mode

Since the URP Default Lit shader does not save the smoothness value to the normal texture in Forward Rendering, it needs to customize `DepthNormalsPass`. It also means that you need to customize all shaders that you want to draw the reflection for forward.

However, it's pretty simple to handle this with a custom shader that stores the smoothness value into `_CameraNormalsTexture.a`.
See the reference shader(`SSRForwardLit.shader`) in the sample project.

Note) Unfortunately, shaders created with `ShaderGraph` are not supported as there's no way to customize the `DepthNormalsPass` without customizing the URP package.


## Limitations

1. XR Not supported.
2. Transparent Objects Not supported.
3. ShaderGraph shaders are not reflected in Forward(+) rendering.

## Differences from HDRP

1. Assume SSR_APPROX keyword is enabled. (No support PBR Accumulation mode due to performance.)
2. Stencil Check is excluded.
3. ClearCoatMask is excluded since it's only in HDRP.
4. Motion Vector is excluded.
