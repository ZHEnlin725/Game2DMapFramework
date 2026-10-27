using System;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>一份地图的全部普通资源记录。</summary>
[Serializable]
public partial class MapReslist
{
    [SerializableField] public MapRes[] resources;

    public static MapReslist CreateInstance(byte[] bytes)
    {
        var instance = new MapReslist();
        if (bytes == null || bytes.Length == 0)
        {
            instance.resources = Array.Empty<MapRes>();
            return instance;
        }

        using var stream = new ReadWriteStream(bytes);
        instance.Deserialize(stream);
        MapResBehaviorSerialization.ReadBehaviors(stream, instance.resources);
#if UNITY_EDITOR
        instance.SyncEditorTags();
#endif
        return instance;
    }

    /// <summary>保存资源主体和行为附加块，保证旧格式 mapRes 数据可以完整往返。</summary>
    public void SaveTo(ReadWriteStream stream)
    {
#if UNITY_EDITOR
        SyncEditorTags();
#endif
        Serialize(stream);
        MapResBehaviorSerialization.WriteBehaviors(stream, resources);
    }

    /// <summary>从已打开的二进制流读取资源主体和行为附加块。</summary>
    public static MapReslist ReadFrom(ReadWriteStream stream)
    {
        var instance = new MapReslist();
        instance.Deserialize(stream);
        MapResBehaviorSerialization.ReadBehaviors(stream, instance.resources);
#if UNITY_EDITOR
        instance.SyncEditorTags();
#endif
        return instance;
    }

    /// <summary>异步反序列化；异常会在 await 处抛出，便于调用方处理损坏数据。</summary>
    public static async Task<MapReslist> CreateInstanceAsync(byte[] bytes)
    {
        var instance = new MapReslist();
        if (bytes == null || bytes.Length == 0)
        {
            instance.resources = Array.Empty<MapRes>();
            return instance;
        }

        await Task.Run(() =>
        {
            using var stream = new ReadWriteStream(bytes);
            instance.Deserialize(stream);
            MapResBehaviorSerialization.ReadBehaviors(stream, instance.resources);
#if UNITY_EDITOR
            instance.SyncEditorTags();
#endif
        }).ConfigureAwait(false);
        return instance;
    }

    /// <summary>旧回调风格的入口，内部走 Task 版本，异常不会静默丢失。</summary>
    public static async void CreateInstanceAsync(byte[] bytes, Action<MapReslist> callback)
    {
        MapReslist instance;
        try
        {
            instance = await CreateInstanceAsync(bytes);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            instance = new MapReslist {resources = Array.Empty<MapRes>()};
        }

        callback?.Invoke(instance);
    }

#if UNITY_EDITOR
    private void SyncEditorTags()
    {
        if (resources == null) return;
        for (var i = 0; i < resources.Length; i++)
        {
            if (resources[i] == null) continue;
            resources[i].tag = (int)resources[i].resTag;
        }
    }
#endif
}