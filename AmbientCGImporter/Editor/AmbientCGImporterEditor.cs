using System;
using System.Net;
using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace MG
{
    /// <summary>
    /// This script was made in order to reduce the amount of steps required for developers to import textures from ambientcg.com into Unity.
    /// Contains a modified version of script to convert textures to unity format found here: 
    /// https://forum.unity.com/threads/free-ambientcg-to-unity-texture-converter-1500-free-pbr-materials.1219455/
    /// 
    /// For any issues, you can contact me here:
    /// Discord: Groth#0604
    /// Email: software.agroth@gmail.com
    /// </summary>
    public class AmbientCGImporterEditor : EditorWindow
    {
        /// <summary>
        /// Used for caching input from gui elements in window
        /// </summary>
        private struct UserInput
        {
            public string textureUrl;
            public int resolutionIndex;
            public bool logging;
            public bool applySkybox;
        }
        private UserInput m_userInput = new UserInput();

        [Serializable]
        private sealed class AmbientCGApiResponse
        {
            public AmbientCGAssetInfo[] foundAssets;
        }

        [Serializable]
        private sealed class AmbientCGAssetInfo
        {
            public string assetId;
            public string dataType;
            public string displayName;
        }

        private sealed class ImportJob
        {
            public string AssetId;
            public string DataType;
            public string Resolution;
            public bool Logging;
            public bool ApplySkybox;

            public string AbsoluteFolderPath =>
                Path.Combine(UnityEngine.Application.dataPath, "AmbientCGImporter", "Imported", AssetId);

            public string RelativeFolderPath =>
                "Assets/AmbientCGImporter/Imported/" + AssetId;
        }

        private const string m_downloadBaseUrl = "https://ambientcg.com/get?file=";
        private const string m_metadataBaseUrl = "https://ambientcg.com/api/v2/full_json?id=";
        private static readonly Regex m_assetIdPattern =
            new Regex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);
        private readonly string[] m_resolutions = { "1K", "2K", "4K", "8K", "12K", "16K" };

        private bool m_isImporting;
        private string m_statusMessage;
        private MessageType m_statusType = MessageType.Info;

        [MenuItem("Tools/AmbientCG Importer")]
        public static void OpenWindow()
        {
            EditorWindow ew = EditorWindow.GetWindow(typeof(AmbientCGImporterEditor));
            ew.titleContent = new UnityEngine.GUIContent("AmbientCG Importer");
        }

        private void OnGUI()
        {
            // Draw the custom gui elements and get the user input
            UnityEngine.GUILayout.Space(10);
            m_userInput.textureUrl = EditorGUILayout.TextField("Url", m_userInput.textureUrl);
            UnityEngine.GUILayout.Space(5);
            m_userInput.resolutionIndex = EditorGUILayout.Popup("Resolution", m_userInput.resolutionIndex, m_resolutions);
            UnityEngine.GUILayout.Space(5);
            m_userInput.logging = EditorGUILayout.Toggle("Logging", m_userInput.logging);
            m_userInput.applySkybox = EditorGUILayout.Toggle(
                new UnityEngine.GUIContent(
                    "Apply HDRI To Scene",
                    "When importing an HDRI, assign the generated skybox to the active scene."),
                m_userInput.applySkybox);
            UnityEngine.GUILayout.Space(20);

            using (new EditorGUI.DisabledScope(m_isImporting))
            {
                if (UnityEngine.GUILayout.Button(m_isImporting ? "Importing..." : "Import"))
                    Import();
            }

            if (!string.IsNullOrEmpty(m_statusMessage))
            {
                UnityEngine.GUILayout.Space(10);
                EditorGUILayout.HelpBox(m_statusMessage, m_statusType);
            }
        }

        /// <summary>
        /// Main method for logic of importing
        /// </summary>
        private async void Import()
        {
            if (!TryParseAssetId(m_userInput.textureUrl, out string requestedAssetId, out string parseError))
            {
                SetStatus(parseError, MessageType.Error);
                return;
            }

            m_isImporting = true;
            SetStatus("Looking up " + requestedAssetId + " on ambientCG...", MessageType.Info);

            WebClient client = null;
            string temporaryZipPath = null;
            string networkOperation = "metadata lookup";
            try
            {
                client = new WebClient();
                client.Headers[HttpRequestHeader.UserAgent] = "AmbientCGImporter-Unity";

                AmbientCGAssetInfo asset = await FetchAssetInfo(client, requestedAssetId);
                if (asset == null)
                    throw new InvalidOperationException(
                        "ambientCG did not return an asset named '" + requestedAssetId + "'.");

                if (string.Equals(asset.dataType, "HDRIElement", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException(
                        asset.assetId + " is an HDRI Element, not a 360-degree panorama. " +
                        "HDRI Elements cannot be imported as Unity skyboxes.");
                }

                bool isHdri = string.Equals(asset.dataType, "HDRI", StringComparison.OrdinalIgnoreCase);
                bool isMaterial = string.Equals(asset.dataType, "Material", StringComparison.OrdinalIgnoreCase);
                if (!isHdri && !isMaterial)
                {
                    throw new NotSupportedException(
                        "ambientCG asset type '" + asset.dataType + "' is not supported. " +
                        "This importer currently supports Material and HDRI assets.");
                }

                ImportJob job = new ImportJob
                {
                    AssetId = asset.assetId,
                    DataType = asset.dataType,
                    Resolution = m_resolutions[m_userInput.resolutionIndex],
                    Logging = m_userInput.logging,
                    ApplySkybox = m_userInput.applySkybox
                };

                string downloadUrl = CreateDownloadLink(job);
                temporaryZipPath = CreateTemporaryZipPath(job);
                SetStatus(
                    "Detected " + (isHdri ? "HDRI environment" : "PBR material") +
                    ". Downloading " + job.Resolution + "...",
                    MessageType.Info);

                if (job.Logging)
                    UnityEngine.Debug.Log("Downloading " + job.AssetId + " from " + downloadUrl);

                networkOperation = "asset download";
                await client.DownloadFileTaskAsync(new Uri(downloadUrl), temporaryZipPath);

                SetStatus("Download complete. Importing " + job.AssetId + "...", MessageType.Info);
                EnsureImportFolders(job);
                using (ZipArchive archive = ZipFile.OpenRead(temporaryZipPath))
                {
                    if (isHdri)
                        ImportHdriArchive(archive, job);
                    else
                        ImportMaterialArchive(archive, job);
                }

                string successMessage = isHdri
                    ? "HDRI skybox imported to " + job.RelativeFolderPath
                    : "URP material imported to " + job.RelativeFolderPath;
                SetStatus(successMessage, MessageType.Info);

                if (job.Logging)
                    UnityEngine.Debug.Log(successMessage);
            }
            catch (WebException exception)
            {
                string message =
                    "ambientCG " + networkOperation + " failed. " +
                    (networkOperation == "asset download"
                        ? "The selected resolution may not be available. "
                        : string.Empty) +
                    GetWebExceptionDetails(exception);
                SetStatus(message, MessageType.Error);
                UnityEngine.Debug.LogError(message);
            }
            catch (Exception exception)
            {
                SetStatus(exception.Message, MessageType.Error);
                UnityEngine.Debug.LogException(exception);
            }
            finally
            {
                client?.Dispose();
                if (!string.IsNullOrEmpty(temporaryZipPath) && File.Exists(temporaryZipPath))
                    File.Delete(temporaryZipPath);

                m_isImporting = false;
                Repaint();
            }
        }

        internal static bool TryParseAssetId(string input, out string assetId, out string error)
        {
            assetId = null;
            error = null;
            string trimmed = input?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                error = "Paste an ambientCG asset URL or asset ID.";
                return false;
            }

            if (m_assetIdPattern.IsMatch(trimmed))
            {
                assetId = trimmed;
                return true;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !(string.Equals(uri.Host, "ambientcg.com", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(uri.Host, "www.ambientcg.com", StringComparison.OrdinalIgnoreCase)))
            {
                error = "Enter a valid ambientCG HTTPS URL or asset ID.";
                return false;
            }

            string path = uri.AbsolutePath.Trim('/');
            if (path.StartsWith("a/", StringComparison.OrdinalIgnoreCase))
            {
                string[] pathParts = path.Split('/');
                if (pathParts.Length >= 2)
                    assetId = Uri.UnescapeDataString(pathParts[1]);
            }
            else if (string.Equals(path, "view", StringComparison.OrdinalIgnoreCase))
            {
                assetId = GetQueryParameter(uri.Query, "id");
            }

            if (string.IsNullOrEmpty(assetId) || !m_assetIdPattern.IsMatch(assetId))
            {
                assetId = null;
                error = "The URL does not contain a valid ambientCG asset ID.";
                return false;
            }

            return true;
        }

        private static string GetQueryParameter(string query, string parameterName)
        {
            if (string.IsNullOrEmpty(query))
                return null;

            foreach (string item in query.TrimStart('?').Split('&'))
            {
                string[] parts = item.Split(new[] { '=' }, 2);
                if (parts.Length == 2 &&
                    string.Equals(
                        Uri.UnescapeDataString(parts[0]),
                        parameterName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(parts[1].Replace("+", " "));
                }
            }

            return null;
        }

        private static async Task<AmbientCGAssetInfo> FetchAssetInfo(
            WebClient client,
            string requestedAssetId)
        {
            string metadataUrl = m_metadataBaseUrl + Uri.EscapeDataString(requestedAssetId);
            string json = await client.DownloadStringTaskAsync(new Uri(metadataUrl));
            AmbientCGApiResponse response =
                UnityEngine.JsonUtility.FromJson<AmbientCGApiResponse>(json);
            if (response?.foundAssets == null)
                return null;

            return response.foundAssets.FirstOrDefault(
                       asset => string.Equals(
                           asset.assetId,
                           requestedAssetId,
                           StringComparison.OrdinalIgnoreCase))
                   ?? response.foundAssets.FirstOrDefault();
        }

        private static string CreateDownloadLink(ImportJob job)
        {
            string suffix = string.Equals(job.DataType, "HDRI", StringComparison.OrdinalIgnoreCase)
                ? ".zip"
                : "-PNG.zip";
            return m_downloadBaseUrl + Uri.EscapeDataString(
                job.AssetId + "_" + job.Resolution + suffix);
        }

        private static string CreateTemporaryZipPath(ImportJob job)
        {
            string temporaryFolder = Path.Combine(Path.GetTempPath(), "AmbientCGImporter");
            Directory.CreateDirectory(temporaryFolder);
            return Path.Combine(
                temporaryFolder,
                job.AssetId + "_" + job.Resolution + "_" + Guid.NewGuid().ToString("N") + ".zip");
        }

        private static void EnsureImportFolders(ImportJob job)
        {
            const string importedRoot = "Assets/AmbientCGImporter/Imported";
            if (!AssetDatabase.IsValidFolder(importedRoot))
                AssetDatabase.CreateFolder("Assets/AmbientCGImporter", "Imported");

            if (!AssetDatabase.IsValidFolder(job.RelativeFolderPath))
                AssetDatabase.CreateFolder(importedRoot, job.AssetId);
        }

        private void ImportMaterialArchive(ZipArchive archive, ImportJob job)
        {
            ExtractAmbientCG(archive, job.AbsoluteFolderPath, job.AssetId);
            CreateMaterial(job);
        }

        private static void ImportHdriArchive(ZipArchive archive, ImportJob job)
        {
            string expectedSuffix = "_" + job.Resolution + "_HDR.exr";
            ZipArchiveEntry hdriEntry = archive.Entries.FirstOrDefault(
                entry => entry.Name.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase));
            if (hdriEntry == null)
            {
                throw new InvalidDataException(
                    "The downloaded archive does not contain an HDR panorama ending in '" +
                    expectedSuffix + "'.");
            }

            string fileName = job.AssetId + "_" + job.Resolution + "_HDR.exr";
            string absoluteTexturePath = Path.Combine(job.AbsoluteFolderPath, fileName);
            ExtractEntry(hdriEntry, absoluteTexturePath);

            AssetDatabase.Refresh();
            string relativeTexturePath = job.RelativeFolderPath + "/" + fileName;
            ConfigureHdriTextureImporter(relativeTexturePath, job.Resolution);
            CreateSkyboxMaterial(job, relativeTexturePath);
        }

        private void CreateMaterial(ImportJob job)
        {
            AssetDatabase.Refresh();

            string albedoPath = job.RelativeFolderPath + "/" + job.AssetId + "_alb.png";
            string normalPath = job.RelativeFolderPath + "/" + job.AssetId + "_nml.png";
            string maskPath = job.RelativeFolderPath + "/" + job.AssetId + "_mos.png";
            string displacementPath = job.RelativeFolderPath + "/" + job.AssetId + "_plx.png";
            string materialPath = job.RelativeFolderPath + "/" + job.AssetId + ".mat";

            // Color textures use sRGB. PBR data maps must be sampled in linear space.
            ConfigureTextureImporter(albedoPath, TextureImporterType.Default, true);
            ConfigureTextureImporter(normalPath, TextureImporterType.NormalMap, false);
            ConfigureTextureImporter(maskPath, TextureImporterType.Default, false);
            ConfigureTextureImporter(displacementPath, TextureImporterType.Default, false);

            UnityEngine.Shader shader = UnityEngine.Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "Could not find 'Universal Render Pipeline/Lit'. " +
                    "Install and activate URP before importing an ambientCG material.");
            }

            UnityEngine.Material material =
                AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(materialPath);
            bool isNewMaterial = material == null;

            if (isNewMaterial)
            {
                material = new UnityEngine.Material(shader);
                material.name = job.AssetId;
            }
            else
            {
                material.shader = shader;
            }

            UnityEngine.Texture2D albedo =
                AssetDatabase.LoadAssetAtPath<UnityEngine.Texture2D>(albedoPath);
            UnityEngine.Texture2D normal =
                AssetDatabase.LoadAssetAtPath<UnityEngine.Texture2D>(normalPath);
            UnityEngine.Texture2D mask =
                AssetDatabase.LoadAssetAtPath<UnityEngine.Texture2D>(maskPath);

            // The generated MOS map uses R = metallic, G = ambient occlusion,
            // B = unused, and A = smoothness. URP reads the same texture through
            // both slots to access the metallic/smoothness and occlusion channels.
            SetTextureIfSupported(material, "_BaseMap", albedo);
            SetTextureIfSupported(material, "_BumpMap", normal);
            SetTextureIfSupported(material, "_MetallicGlossMap", mask);
            SetTextureIfSupported(material, "_OcclusionMap", mask);

            SetFloatIfSupported(material, "_Metallic", 1f);
            SetFloatIfSupported(material, "_Smoothness", 1f);
            SetFloatIfSupported(material, "_BumpScale", 1f);
            SetFloatIfSupported(material, "_OcclusionStrength", 1f);

            if (normal != null)
                material.EnableKeyword("_NORMALMAP");
            if (mask != null)
            {
                material.EnableKeyword("_METALLICSPECGLOSSMAP");
                material.EnableKeyword("_OCCLUSIONMAP");
            }

            if (isNewMaterial)
                AssetDatabase.CreateAsset(material, materialPath);
            else
                EditorUtility.SetDirty(material);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (job.Logging)
                UnityEngine.Debug.Log("URP material successfully created at " + materialPath);
        }

        private static void ConfigureHdriTextureImporter(string assetPath, string resolution)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                throw new InvalidOperationException("Unity could not import the HDR texture at " + assetPath);

            importer.textureType = TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.sRGBTexture = false;
            importer.mipmapEnabled = true;
            importer.wrapModeU = UnityEngine.TextureWrapMode.Repeat;
            importer.wrapModeV = UnityEngine.TextureWrapMode.Clamp;
            importer.wrapModeW = UnityEngine.TextureWrapMode.Clamp;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = GetMaximumTextureSize(resolution);
            importer.SaveAndReimport();
        }

        private static int GetMaximumTextureSize(string resolution)
        {
            switch (resolution)
            {
                case "1K": return 1024;
                case "2K": return 2048;
                case "4K": return 4096;
                case "8K": return 8192;
                case "12K":
                case "16K":
                    return 16384;
                default:
                    return 2048;
            }
        }

        private static void CreateSkyboxMaterial(ImportJob job, string texturePath)
        {
            const string shaderName = "Skybox/Panoramic";
            UnityEngine.Shader shader = UnityEngine.Shader.Find(shaderName);
            if (shader == null)
                throw new InvalidOperationException("Could not find Unity shader '" + shaderName + "'.");

            UnityEngine.Texture2D hdri =
                AssetDatabase.LoadAssetAtPath<UnityEngine.Texture2D>(texturePath);
            if (hdri == null)
                throw new InvalidOperationException("Could not load the imported HDR texture at " + texturePath);

            string materialPath =
                job.RelativeFolderPath + "/" + job.AssetId + "_Skybox.mat";
            UnityEngine.Material material =
                AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(materialPath);
            bool isNewMaterial = material == null;

            if (isNewMaterial)
            {
                material = new UnityEngine.Material(shader)
                {
                    name = job.AssetId + "_Skybox"
                };
            }
            else
            {
                material.shader = shader;
            }

            SetTextureIfSupported(material, "_MainTex", hdri);
            SetFloatIfSupported(material, "_Mapping", 1f);
            SetFloatIfSupported(material, "_ImageType", 0f);

            if (isNewMaterial)
            {
                if (material.HasProperty("_Tint"))
                    material.SetColor("_Tint", UnityEngine.Color.white);
                SetFloatIfSupported(material, "_Exposure", 1f);
                SetFloatIfSupported(material, "_Rotation", 0f);
                AssetDatabase.CreateAsset(material, materialPath);
            }
            else
            {
                EditorUtility.SetDirty(material);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (job.ApplySkybox)
            {
                UnityEngine.RenderSettings.skybox = material;
                UnityEngine.DynamicGI.UpdateEnvironment();

                Scene activeScene = SceneManager.GetActiveScene();
                if (activeScene.IsValid())
                    EditorSceneManager.MarkSceneDirty(activeScene);
            }
        }

        private static void ExtractEntry(ZipArchiveEntry entry, string outputPath)
        {
            string parentDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parentDirectory))
                Directory.CreateDirectory(parentDirectory);

            using (Stream input = entry.Open())
            using (FileStream output = new FileStream(
                       outputPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            {
                input.CopyTo(output);
            }
        }

        private static string GetWebExceptionDetails(WebException exception)
        {
            if (exception.Response is HttpWebResponse response)
                return "HTTP " + (int)response.StatusCode + " (" + response.StatusDescription + ").";

            return exception.Message;
        }

        private void SetStatus(string message, MessageType type)
        {
            m_statusMessage = message;
            m_statusType = type;
            Repaint();
        }

        private static void ConfigureTextureImporter(
            string assetPath,
            TextureImporterType textureType,
            bool sRgb)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                return;

            bool changed = importer.textureType != textureType || importer.sRGBTexture != sRgb;
            importer.textureType = textureType;
            importer.sRGBTexture = sRgb;

            if (changed)
                importer.SaveAndReimport();
        }

        private static void SetTextureIfSupported(
            UnityEngine.Material material,
            string propertyName,
            UnityEngine.Texture texture)
        {
            if (texture != null && material.HasProperty(propertyName))
                material.SetTexture(propertyName, texture);
        }

        private static void SetFloatIfSupported(
            UnityEngine.Material material,
            string propertyName,
            float value)
        {
            if (material.HasProperty(propertyName))
                material.SetFloat(propertyName, value);
        }
        #region Imported Methods
        /// <summary>
        /// Slightly modified code from https://forum.unity.com/threads/free-ambientcg-to-unity-texture-converter-1500-free-pbr-materials.1219455/ in order to fit need of program.
        /// All credits goes to original creator.
        /// </summary>
        private void ExtractAmbientCG(ZipArchive arc, string dir, string name)
        {
            static bool tryFindEntry(ZipArchive arc, string suffix, out ZipArchiveEntry e)
            {
                e = arc.Entries.FirstOrDefault(x => x.Name.ToLowerInvariant().EndsWith(suffix)); return e != null;
            }

            static ZipArchiveEntry findEntryOrNull(ZipArchive arc, string suffix)
            {
                tryFindEntry(arc, suffix, out ZipArchiveEntry e); return e;
            }

            static void copyEntry(ZipArchive arc, string suffix, string outFile, bool throwIfNotFound)
            {
                if (File.Exists(outFile)) File.Delete(outFile);
                if (tryFindEntry(arc, suffix, out ZipArchiveEntry e))
                {
                    using Stream IN = e.Open();
                    using Stream OUT = File.OpenWrite(outFile);
                    IN.CopyTo(OUT);
                }
                else if (throwIfNotFound)
                    throw new Exception($"Could not find an entry ending with {suffix} in [{string.Join(", ", arc.Entries.Select(x => x.Name))}]");
            }

            static byte[] readStreamBytes(ZipArchiveEntry e)
            {
                if (e == null) return null;
                using Stream es = e.Open();
                using MemoryStream ms = new();
                es.CopyTo(ms);
                return ms.ToArray();
            }

            string colorOut = $"{dir}/{name}_alb.png";
            string mosOut = $"{dir}/{name}_mos.png";
            string normalOut = $"{dir}/{name}_nml.png";
            string plxOut = $"{dir}/{name}_plx.png";
            ZipArchiveEntry metalness = findEntryOrNull(arc, "_metalness.png");
            ZipArchiveEntry roughness = findEntryOrNull(arc, "_roughness.png");
            ZipArchiveEntry ao = findEntryOrNull(arc, "_ambientocclusion.png");
            if (File.Exists(mosOut)) File.Delete(mosOut);
            makeMosMap(readStreamBytes(metalness), readStreamBytes(roughness), readStreamBytes(ao), mosOut);
            copyEntry(arc, "_color.png", colorOut, true);
            copyEntry(arc, "_normalgl.png", normalOut, true);
            copyEntry(arc, "_displacement.png", plxOut, false);

            // close zip file after use
            arc.Dispose();
        }

        /// <summary>
        /// Function taken directly from the AmbientCGToUnity.cs file. All credit goes to original creator
        /// </summary>
        private void makeMosMap(byte[] metalBytes, byte[] roughBytes, byte[] aoBytes, string outFile)
        {
            static Bitmap bytesToBitmap(byte[] bytes)
            {
                if (bytes == null) return null;
                using MemoryStream ms = new(bytes);
                return new Bitmap(ms, false);
            }

            // https://stackoverflow.com/questions/1922040/how-to-resize-an-image-c-sharp
            static Bitmap resize(Bitmap b, int w, int h)
            {
                Rectangle destRect = new(0, 0, w, h);
                Bitmap destImage = new(w, h);
                using var graphics = Graphics.FromImage(destImage);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using ImageAttributes wrapMode = new();
                wrapMode.SetWrapMode(WrapMode.TileFlipXY);
                graphics.DrawImage(b, destRect, 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, wrapMode);
                return destImage;
            }

            static void matchSizes(ref Bitmap a, ref Bitmap b, ref Bitmap c, out int w, out int h)
            {
                w = 0; h = 0;
                if (a != null) { w = Math.Max(w, a.Width); h = Math.Max(h, a.Height); }
                if (b != null) { w = Math.Max(w, b.Width); h = Math.Max(h, b.Height); }
                if (c != null) { w = Math.Max(w, c.Width); h = Math.Max(h, c.Height); }
                if (a != null && (a.Width != w || a.Height != h)) { Bitmap n = resize(a, w, h); a.Dispose(); a = n; }
                if (b != null && (b.Width != w || b.Height != h)) { Bitmap n = resize(b, w, h); b.Dispose(); b = n; }
                if (c != null && (c.Width != w || c.Height != h)) { Bitmap n = resize(c, w, h); c.Dispose(); c = n; }
            }

            // TODO this is MUCH slower than it needs to be; see LockPixels()
            static Color[] readColors(Bitmap bmp, int w, int h)
            {
                Color[] a = new Color[w * h];
                for (int y = 0; y < h; ++y)
                    for (int x = 0; x < w; ++x)
                        a[(y * w) + x] = bmp.GetPixel(x, y);
                return a;
            }

            // TODO this is MUCH slower than it needs to be; see LockPixels()
            static void writeColors(Bitmap bmp, int w, int h, Color[] a)
            {
                for (int y = 0; y < h; ++y)
                    for (int x = 0; x < w; ++x)
                        bmp.SetPixel(x, y, a[(y * w) + x]);
            }

            static Color[] fakeColors(int len, int b)
            {
                Color[] a = new Color[len];
                for (int i = 0; i < len; ++i)
                    a[i] = Color.FromArgb(255, b, b, b);
                return a;
            }

            static Color[] combineMosColors(Color[] metal, Color[] rough, Color[] ao)
            {
                Debug.Assert(metal != null && rough != null && ao != null && metal.Length > 0 && rough.Length == metal.Length && ao.Length == metal.Length);
                int len = metal.Length;
                Color[] mos = new Color[len];
                for (int i = 0; i < len; ++i)
                    mos[i] = combineMosColor(metal[i], rough[i], ao[i]);
                return mos;
            }

            // this is the unity mask map format -- red is metallic, green is AO, and alpha is smoothness (just inverted
            // roughness). In HDRP and Better Lit, the Blue channel is used for detail mask. Currently, we are not using
            // the detail mask at all, and could possibly repurpose this for parallax or something, but for now I'm leaving
            // it just as 0 in case some materials need detial masks someday and to keep compatibility with other shaders
            // Note that Better Lit uses albedo alpha for parallax and URP default needs a separate parallax map, so there's no
            // real standard for where parallax should be.
            static Color combineMosColor(Color metal, Color rough, Color ao) =>
                Color.FromArgb(
                    red: metal.R,
                    green: ao.R,
                    blue: 0,
                    alpha: 255 - rough.R);

            Bitmap metalBmp = null, roughBmp = null, aoBmp = null;
            Color[] metalColors, roughColors, aoColors;
            int width, height;
            try
            {
                metalBmp = bytesToBitmap(metalBytes);
                roughBmp = bytesToBitmap(roughBytes);
                aoBmp = bytesToBitmap(aoBytes);
                matchSizes(ref metalBmp, ref roughBmp, ref aoBmp, out width, out height);
                metalColors = metalBmp != null ? readColors(metalBmp, width, height) : fakeColors(width * height, 0);
                roughColors = roughBmp != null ? readColors(roughBmp, width, height) : fakeColors(width * height, 127);
                aoColors = aoBmp != null ? readColors(aoBmp, width, height) : fakeColors(width * height, 255);
            }
            finally
            {
                metalBmp?.Dispose();
                roughBmp?.Dispose();
                aoBmp?.Dispose();
            }

            using Bitmap mosBmp = new(width, height);
            writeColors(mosBmp, width, height, combineMosColors(metalColors, roughColors, aoColors));
            mosBmp.Save(outFile);
        }
        #endregion
    }

}
