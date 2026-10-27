#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 地图表现层烘焙器：为指定地图编辑并生成 MapPresentationDefinition，
/// 输出 Assets/Data/MapData/mapPresentation@{mapId}.bytes；支持从已有 .bytes 读回继续编辑。
/// 可视化编辑由 Assets/Editor/MapEdit/MapPresentationBakerEditor.cs 提供。
/// </summary>
public class MapPresentationBaker : MonoBehaviour
{
    public int mapId;
    public TextAsset data;

    public MapPresentationRule[] rules;

    public void Load()
    {
        byte[] bytes = null;
        if (data != null)
        {
            bytes = data.bytes;
        }
        else
        {
            var path = string.Format(MapDefine.PresentationDataFormat, mapId);
            if (File.Exists(path)) bytes = File.ReadAllBytes(path);
        }

        if (bytes == null || bytes.Length == 0)
        {
            Debug.LogWarning($"[MapPresentationBaker] 未找到地图 {mapId} 的表现层数据，规则已重置为空。");
            rules = Array.Empty<MapPresentationRule>();
            return;
        }

        var definition = MapPresentationDefinition.CreateInstance(bytes);
        rules = definition?.rules ?? Array.Empty<MapPresentationRule>();
    }

    public void Save()
    {
        var definition = new MapPresentationDefinition
        {
            rules = rules ?? Array.Empty<MapPresentationRule>()
        };
        var bytes = definition.SaveToBytes();

        if (!Directory.Exists(MapDefine.DataDirectory))
            Directory.CreateDirectory(MapDefine.DataDirectory);

        var path = string.Format(MapDefine.PresentationDataFormat, mapId);
        File.WriteAllBytes(path, bytes);
        AssetDatabase.Refresh();

        data = AssetDatabase.LoadAssetAtPath<TextAsset>(path.Replace('\\', '/'));
        Debug.Log($"[MapPresentationBaker] 已保存地图 {mapId} 表现层数据: {path}");
    }
}
#endif