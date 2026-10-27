#if UNITY_EDITOR
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Inspector for MapTextureSlicer with buttons for its public editor operations.
/// </summary>
[CustomEditor(typeof(MapTextureSlicer))]
public sealed class MapTextureSlicerEditor : Editor
{
    private void OnEnable()
    {
        AutoFillMapIdFromSource();
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        AutoFillMapIdFromSource();
        serializedObject.ApplyModifiedPropertiesWithoutUndo();

        DrawDefaultInspector();

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Operations", EditorStyles.boldLabel);

        if (GUILayout.Button("Slice Map") &&
            TryConfirmOverwriteBytes(GetSliceDataPath()))
            ExecuteMethod("Slice Map", slicer => slicer.SliceMap());

        if (GUILayout.Button("Apply Whole Image As Map") &&
            TryConfirmOverwriteBytes(GetSliceDataPath()))
            ExecuteMethod("Apply Whole Image As Map", slicer => slicer.ApplyWholeImageAsMap());

        if (GUILayout.Button("Generate Sprite Prefabs"))
            ExecuteMethod("Generate Sprite Prefabs", slicer => slicer.GenerateSlicePrefabs());

        if (GUILayout.Button("Preview Slice Rects"))
            ExecuteMethod("Preview Slice Rects", slicer => slicer.PreviewSliceRects());
    }

    private void AutoFillMapIdFromSource()
    {
        if (target is not MapTextureSlicer slicer) return;

        var mapId = ExtractMapIdFromName(GetSourceAssetName(slicer));
        if (mapId == null) return;

        var property = serializedObject.FindProperty("mapId");
        if (property == null) return;

        property.intValue = mapId.Value;
        serializedObject.ApplyModifiedPropertiesWithoutUndo();
    }

    private static string GetSourceAssetName(MapTextureSlicer slicer)
    {
        var texture = GetMapTexture(slicer);
        if (texture != null) return texture.name;

        var spriteRenderer = slicer.GetComponent<SpriteRenderer>();
        return spriteRenderer != null && spriteRenderer.sprite != null
            ? spriteRenderer.sprite.texture?.name
            : null;
    }

    private static Texture2D GetMapTexture(MapTextureSlicer slicer)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        return typeof(MapTextureSlicer).GetField("mapTexture", flags)?.GetValue(slicer) as Texture2D;
    }

    private static int? ExtractMapIdFromName(string assetName)
    {
        if (string.IsNullOrEmpty(assetName)) return null;

        var separatorIndex = assetName.LastIndexOf('@');
        if (separatorIndex < 0 || separatorIndex == assetName.Length - 1) return null;

        return int.TryParse(assetName.Substring(separatorIndex + 1), out var value)
            ? value
            : (int?)null;
    }

    private string GetSliceDataPath()
    {
        var mapIdProperty = serializedObject.FindProperty("mapId");
        var mapId = mapIdProperty?.intValue ?? 0;
        return string.Format(MapDefine.SliceDataFormat, mapId);
    }

    private static bool TryConfirmOverwriteBytes(string outputPath)
    {
        if (!System.IO.File.Exists(outputPath)) return true;

        return EditorUtility.DisplayDialog(
            "Map Bytes Exists",
            $"目标文件已存在：\n{outputPath}\n\n是否覆盖写入？",
            "确认",
            "取消");
    }

    private void ExecuteMethod(string operationName, System.Action<MapTextureSlicer> execute)
    {
        if (target is not MapTextureSlicer slicer)
            return;

        Undo.RecordObject(slicer, operationName);
        execute(slicer);
        EditorUtility.SetDirty(slicer);
    }
}
#endif