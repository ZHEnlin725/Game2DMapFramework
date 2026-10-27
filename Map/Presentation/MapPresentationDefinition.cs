using System;
using UnityEngine;

/// <summary>一张地图全部表现规则的集合。</summary>
[Serializable]
public partial class MapPresentationDefinition
{
    [SerializableField] public MapPresentationRule[] rules = Array.Empty<MapPresentationRule>();

    public static MapPresentationDefinition CreateInstance(byte[] bytes)
    {
        var instance = new MapPresentationDefinition();
        if (bytes == null || bytes.Length == 0)
        {
            instance.rules = Array.Empty<MapPresentationRule>();
            return instance;
        }

        using var stream = new ReadWriteStream(bytes);
        instance.Deserialize(stream);
        MapPresentationBehaviorSerialization.Read(stream, instance.rules);
        return instance;
    }

    /// <summary>保存规则字段和多态行为附加块。</summary>
    public byte[] SaveToBytes()
    {
        using var stream = new ReadWriteStream();
        Serialize(stream);
        MapPresentationBehaviorSerialization.Write(stream, rules);
        return stream.ToArray();
    }
}