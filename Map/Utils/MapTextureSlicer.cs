#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 编辑器专用工具：把大尺寸地图贴图切分为切片纹理，并记录运行时加载所需的切片数据。
/// </summary>
public class MapTextureSlicer : MonoBehaviour
{
    private const int MaxSliceDimension = 8192;
    private const int SlicesPerFrame = 16;
    private const int PrefabsPerProgressTick = 32;

    [SerializeField] private int mapId;
    [SerializeField] private Texture2D mapTexture;
    [SerializeField] private int sliceWidth = 128, sliceHeight = 128;
    [SerializeField] private int sliceOffsetX, sliceOffsetY;

    private string textureOutputRoot = "Assets/Art/Map/Slices/Texture";
    private string prefabOutputRoot = "Assets/Art/Map/Slices/Prefab";
    private readonly List<RectInt> previewSliceRects = new List<RectInt>();

    public void SliceMap() => SliceMapAsync();

    private async void SliceMapAsync()
    {
        try
        {
            ResolveMapTexture();
            if (mapTexture == null)
            {
                Debug.LogWarning("[MapTextureSlicer] No map texture found.");
                return;
            }

            var outputFolderName = $"map_{mapId}";
            var textureDirectory = Path.Combine(textureOutputRoot, outputFolderName);
            if (Directory.Exists(textureDirectory) && !EditorUtility.DisplayDialog(
                    "Map Folder Exists",
                    $"The folder {outputFolderName} already exists. Overwrite it?",
                    "Overwrite",
                    "Cancel"))
            {
                return;
            }

            previewSliceRects.Clear();
            EditorUtility.DisplayProgressBar("MapTextureSlicer", "Slicing map texture...", 0.5f);

            Color[] pixels = null;
            if (mapTexture.width > MaxSliceDimension ||
                mapTexture.height > MaxSliceDimension ||
                !mapTexture.isReadable)
            {
                pixels = CaptureReadablePixels(mapTexture);
            }

            var slicingGridInfo = pixels != null
                ? PictureUtils.SliceTexture(
                    pixels,
                    mapTexture.width,
                    mapTexture.height,
                    sliceWidth,
                    sliceHeight,
                    sliceOffsetX,
                    sliceOffsetY)
                : PictureUtils.SliceTexture(
                    mapTexture,
                    sliceWidth,
                    sliceHeight,
                    sliceOffsetX,
                    sliceOffsetY);

            Debug.Log(
                $"Grid: width={slicingGridInfo.width}, height={slicingGridInfo.height}\n" +
                $"Origin=({slicingGridInfo.originX}, {slicingGridInfo.originY})\n" +
                $"Offset=({slicingGridInfo.offsetX}, {slicingGridInfo.offsetY})");

            EnsureDirectory(textureOutputRoot);
            EnsureDirectory(textureDirectory);

            var sliceGrid = slicingGridInfo.slicings;
            var rowCount = sliceGrid.GetLength(0);
            var columnCount = sliceGrid.GetLength(1);
            var totalSliceCount = rowCount * columnCount;
            var dataDirectory = MapDefine.DataDirectory;
            var dataPath = string.Format(MapDefine.SliceDataFormat, mapId);

            var slicingData = new MapSlices
            {
                mapSize = new Vector2Int(mapTexture.width, mapTexture.height),
                sliceSize = new Vector2Int(sliceWidth, sliceHeight),
                origin = new Vector2Int(slicingGridInfo.originX, slicingGridInfo.originY),
                offset = new Vector2Int(slicingGridInfo.offsetX, slicingGridInfo.offsetY),
                slices = new Slice[rowCount, columnCount]
            };

            CleanupStaleSliceTextures(textureDirectory);
            EnsureDirectory(dataDirectory);

            var exportedCount = 0;
            for (var row = 0; row < rowCount; row++)
            {
                for (var column = 0; column < columnCount; column++)
                {
                    var slicePiece = sliceGrid[row, column];
                    var piecePosition = slicePiece.position;
                    var sliceName = $"slice_{piecePosition.x}x{piecePosition.y}";
                    var prefabPath = NormalizeAssetPath(
                        Path.Combine(prefabOutputRoot, outputFolderName, $"{sliceName}.prefab"));
                    var texturePath = Path.Combine(textureDirectory, $"{sliceName}.png");

                    slicingData.slices[row, column] = new Slice
                    {
                        position = piecePosition,
                        assetpath = prefabPath
                    };

                    await File.WriteAllBytesAsync(texturePath, slicePiece.texture.EncodeToPNG());
                    exportedCount++;

                    if (exportedCount % SlicesPerFrame != 0)
                        continue;

                    EditorUtility.DisplayProgressBar(
                        "MapTextureSlicer",
                        $"Exporting slices {exportedCount}/{totalSliceCount}",
                        exportedCount / (float)totalSliceCount);
                    await Task.Yield();
                }
            }

            var serializedData = ReadWriteStream.Serialize(slicingData);
            await File.WriteAllBytesAsync(dataPath, serializedData);

            AssetDatabase.Refresh();
            Debug.Log($"[MapTextureSlicer] Exported {totalSliceCount} slices to {textureDirectory}.");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private static Color[] CaptureReadablePixels(Texture2D texture)
    {
        if (texture.isReadable)
            return texture.GetPixels();

        var previousRenderTarget = RenderTexture.active;
        var renderTexture = RenderTexture.GetTemporary(
            texture.width,
            texture.height,
            0,
            RenderTextureFormat.Default,
            RenderTextureReadWrite.Linear);

        try
        {
            Graphics.Blit(texture, renderTexture);
            RenderTexture.active = renderTexture;

            var capturedTexture = new Texture2D(
                texture.width,
                texture.height,
                TextureFormat.RGBA32,
                false);

            try
            {
                capturedTexture.ReadPixels(
                    new Rect(0f, 0f, texture.width, texture.height),
                    0,
                    0);
                capturedTexture.Apply();
                return capturedTexture.GetPixels();
            }
            finally
            {
                DestroyImmediate(capturedTexture);
            }
        }
        finally
        {
            RenderTexture.ReleaseTemporary(renderTexture);
            RenderTexture.active = previousRenderTarget;
        }
    }


    /// <summary>不切割，直接把整张大图作为单块地图切片生成 Sprite 预制体和切片数据。</summary>
    public void ApplyWholeImageAsMap()
    {
        ResolveMapTexture();
        if (mapTexture == null)
        {
            Debug.LogWarning("[MapTextureSlicer] No map texture found.");
            return;
        }

        var texturePath = AssetDatabase.GetAssetPath(mapTexture);
        if (string.IsNullOrEmpty(texturePath))
        {
            Debug.LogWarning("[MapTextureSlicer] Map texture is not an imported asset: " + mapTexture.name);
            return;
        }

        sliceWidth = mapTexture.width;
        sliceHeight = mapTexture.height;
        EnsureWholeTextureSprite(texturePath);

        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(texturePath);
        if (sprite == null)
        {
            Debug.LogWarning("[MapTextureSlicer] Failed to load sprite from texture: " + texturePath);
            return;
        }

        try
        {
            var outputFolderName = $"map_{mapId}";
            var prefabDirectory = Path.Combine(prefabOutputRoot, outputFolderName);
            EnsureDirectory(prefabDirectory);

            var prefabPath = NormalizeAssetPath(
                Path.Combine(prefabDirectory, outputFolderName + "_whole.prefab"));

            var prefabRoot = new GameObject("map_whole");
            prefabRoot.hideFlags |= HideFlags.HideInHierarchy;
            var spriteRenderer = prefabRoot.AddComponent<SpriteRenderer>();
            spriteRenderer.sprite = sprite;
            spriteRenderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                "Packages/com.unity.render-pipelines.universal/Runtime/Materials/Sprite-Unlit-Default.mat");

            PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
            DestroyImmediate(prefabRoot);

            var width = mapTexture.width;
            var height = mapTexture.height;
            var data = new MapSlices
            {
                mapSize = new Vector2Int(width, height),
                sliceSize = new Vector2Int(width, height),
                origin = Vector2Int.zero,
                offset = Vector2Int.zero,
                slices = new Slice[1, 1]
            };
            data.slices[0, 0] = new Slice
            {
                position = new RectInt(-(width / 2), -(height / 2), width, height),
                assetpath = prefabPath
            };

            var dataPath = string.Format(MapDefine.SliceDataFormat, mapId);
            EnsureDirectory(MapDefine.DataDirectory);
            File.WriteAllBytes(dataPath, ReadWriteStream.Serialize(data));
            AssetDatabase.Refresh();

            Debug.Log($"[MapTextureSlicer] Applied whole image as map {mapId}: {prefabPath}");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
    }

    private static void EnsureWholeTextureSprite(string texturePath)
    {
        if (!(AssetImporter.GetAtPath(texturePath) is TextureImporter importer))
            return;

        if (importer.textureType == TextureImporterType.Sprite &&
            importer.spriteImportMode == SpriteImportMode.Single)
            return;

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.SaveAndReimport();
    }
    public void GenerateSlicePrefabs()
    {
        var outputFolderName = $"map_{mapId}";
        var textureDirectory = Path.Combine(textureOutputRoot, outputFolderName);
        if (!Directory.Exists(textureDirectory))
        {
            Debug.LogWarning(
                $"[MapTextureSlicer] Texture directory does not exist: {textureDirectory}. Run SliceMapAsync first.");
            return;
        }

        var prefabDirectory = Path.Combine(prefabOutputRoot, outputFolderName);
        EnsureDirectory(prefabDirectory);

        var texturePaths = Directory.GetFiles(
            textureDirectory,
            "slice_*.png",
            SearchOption.TopDirectoryOnly);
        if (texturePaths.Length == 0)
        {
            Debug.LogWarning(
                $"[MapTextureSlicer] No slice textures found in {textureDirectory}.");
            return;
        }

        Array.Sort(texturePaths, StringComparer.Ordinal);
        var sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(
            "Packages/com.unity.render-pipelines.universal/Runtime/Materials/Sprite-Unlit-Default.mat");

        try
        {
            AssetDatabase.StartAssetEditing();

            for (var index = 0; index < texturePaths.Length; index++)
            {
                var sliceName = Path.GetFileNameWithoutExtension(texturePaths[index]);
                var prefabPath = NormalizeAssetPath(Path.Combine(prefabDirectory, $"{sliceName}.prefab"));

                var prefabRoot = new GameObject(sliceName);
                prefabRoot.hideFlags |= HideFlags.HideInHierarchy;

                var spriteRenderer = prefabRoot.AddComponent<SpriteRenderer>();
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(
                    NormalizeAssetPath(texturePaths[index]));
                spriteRenderer.sprite = sprite;
                spriteRenderer.sharedMaterial = sharedMaterial;

                PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
                DestroyImmediate(prefabRoot);

                if ((index + 1) % PrefabsPerProgressTick != 0)
                    continue;

                EditorUtility.DisplayProgressBar(
                    "MapTextureSlicer",
                    $"Generating prefab {index + 1}/{texturePaths.Length}",
                    (index + 1f) / texturePaths.Length);
            }
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            EditorUtility.ClearProgressBar();
        }
    }

    public void PreviewSliceRects()
    {
        ResolveMapTexture();
        if (mapTexture == null)
        {
            Debug.LogWarning("[MapTextureSlicer] No map texture found.");
            return;
        }

        previewSliceRects.Clear();
        var rects = PictureUtils.SliceToRects(
            mapTexture.width,
            mapTexture.height,
            sliceWidth,
            sliceHeight,
            sliceOffsetX,
            sliceOffsetY);

        for (var row = 0; row < rects.GetLength(0); row++)
        {
            for (var column = 0; column < rects.GetLength(1); column++)
                previewSliceRects.Add(rects[row, column]);
        }
    }

    private void ResolveMapTexture()
    {
        if (mapTexture != null)
            return;

        var spriteRenderer = GetComponent<SpriteRenderer>();
        if (spriteRenderer != null && spriteRenderer.sprite != null)
            mapTexture = spriteRenderer.sprite.texture;
    }

    private static void EnsureDirectory(string path)
    {
        if (string.IsNullOrEmpty(path) || Directory.Exists(path))
            return;

        Directory.CreateDirectory(path);
    }

    private static void CleanupStaleSliceTextures(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        var staleTexturePaths = Directory.GetFiles(
            directory,
            "slice_*.png",
            SearchOption.TopDirectoryOnly);

        foreach (var staleTexturePath in staleTexturePaths)
            File.Delete(staleTexturePath);
    }

    private static string NormalizeAssetPath(string path)
    {
        return path.Replace('\\', '/');
    }

    private void OnDrawGizmos()
    {
        if (previewSliceRects.Count == 0)
            return;

        var localToWorldMatrix = transform.localToWorldMatrix;
        var previousGizmoMatrix = Gizmos.matrix;
        Gizmos.matrix *= localToWorldMatrix;

        foreach (var sliceRect in previewSliceRects)
        {
            Gizmos.DrawWireCube(
                sliceRect.center / (MapDefine.DefaultPixelsPerUnit * 1f),
                new Vector3(sliceRect.size.x, sliceRect.size.y) / (MapDefine.DefaultPixelsPerUnit * 1f));
        }

        Gizmos.matrix = previousGizmoMatrix;
    }
}
#endif