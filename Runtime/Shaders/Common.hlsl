#ifndef SSR_TEXTURECOMMON_INCLUDED
#define SSR_TEXTURECOMMON_INCLUDED

#if defined(UNITY_TEXTURE2D_X_ARRAY_SUPPORTED)
    #define COORD_TEXTURE2D_X(pixelCoord)       uint3(pixelCoord, SLICE_ARRAY_INDEX)
    #define RW_TEXTURE2D_X(type, textureName)   RW_TEXTURE2D_ARRAY(type, textureName)
#else
    #define COORD_TEXTURE2D_X(pixelCoord)       pixelCoord
    #define RW_TEXTURE2D_X                      RW_TEXTURE2D
#endif

#endif