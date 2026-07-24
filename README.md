# AmbientCGImporter
Import materials and HDRI environments directly from [AmbientCG](https://ambientcg.com/) into a Unity URP project as quickly as possible. The importer detects the ambientCG asset type, downloads the appropriate files, and creates either a ready-to-use `Universal Render Pipeline/Lit` material or a panoramic HDR skybox.

# Requirements
- Unity with the Universal Render Pipeline (URP) installed and active.
- The editor platform must support `System.Drawing`, which is used to pack the source maps.

# Installation
You can find the latest version as a Unity package in the [release tab](https://github.com/agroth01/AmbientCGImporter/releases/tag/Unity). Then just drag the .unitypackage file into your project and follow the wizard.

# Usage
Open the importer from `Tools > AmbientCG Importer`, paste an ambientCG asset URL, choose a resolution, and press **Import**. Full URLs, ambientCG shortlinks, and raw asset IDs are supported.

For a surface material, use a URL such as:

```text
https://ambientcg.com/view?id=Rock050
```

For an HDRI environment, use a URL such as:

```text
https://ambientcg.com/view?id=NightSkyHDRI006
```

HDRIs are imported from the archive's linear EXR panorama and assigned to a generated `Skybox/Panoramic` material. Enable **Apply HDRI To Scene** if the importer should also assign the generated material to the active scene and refresh its environment lighting. This option has no effect on surface materials.

HDRI Elements are isolated lighting elements rather than 360-degree panoramas, so the importer reports them as unsupported for skybox creation.

Generated assets are saved under `Assets/AmbientCGImporter/Imported/<AssetName>`.

The generated MOS texture is packed for URP as:

- Red: metallic
- Green: ambient occlusion
- Blue: unused
- Alpha: smoothness (inverted roughness)

The MOS texture is assigned to both the URP Lit Metallic Map and Occlusion Map slots. Albedo is imported as sRGB, while normal, MOS, and displacement data are imported in linear space. The displacement texture is preserved for custom shaders but is not assigned because URP Lit does not expose a height-map input.

# Contributions
The tool is very rough and could probably be improved upon and optimized. If you want to help, feel free to create a pull request.

# Credits
The original code for extracting and converting the textures into Unity format is taken from [this](https://forum.unity.com/threads/free-ambientcg-to-unity-texture-converter-1500-free-pbr-materials.1219455/) post. The creator deserves most of the credit, as this is based on his/her work!  
  Also a big thank you to the developer of AmbientCG for such an amazing service. Definitely a great resource for any gamedev!
