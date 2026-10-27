using System;
using UnityEngine;

/// <summary>处理地图资源行为附加块的二进制读写；资源主体字段序列化由生成器完成。</summary>
public static class MapResBehaviorSerialization
{
    /// <summary>紧跟资源数组的行为块标记，用于区分旧格式数据（旧数据结尾没有该标记）。</summary>
    private const int BehaviorBlockMagic = 0x4D42524D; // "MBRM"

    private const int MaxBehaviorCount = 64;

    public static void WriteBehaviors(ReadWriteStream stream, MapRes[] resources)
    {
        var length = resources?.Length ?? 0;
        stream.WriteInt32(BehaviorBlockMagic);
        stream.WriteInt32(length);

        for (var i = 0; i < length; i++)
            WriteResourceBehaviors(stream, resources?[i]);
    }

    public static void ReadBehaviors(ReadWriteStream stream, MapRes[] resources)
    {
        if (resources == null || stream.Length - stream.Position < 8) return;

        // 旧数据文件可能没有行为块；只有标记匹配时才继续读取。
        if (stream.ReadInt32() != BehaviorBlockMagic) return;

        var count = stream.ReadInt32();
        if (count < 0 || count > resources.Length)
        {
            Debug.LogWarning($"[MapResBehaviorSerialization] 行为块数量异常（{count}），忽略行为数据");
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var behaviors = ReadResourceBehaviors(stream);
            if (resources[i] != null) resources[i].behaviors = behaviors;
        }
    }

    private static void WriteResourceBehaviors(ReadWriteStream stream, MapRes resource)
    {
        var behaviors = resource?.behaviors;
        var count = behaviors?.Length ?? 0;
        stream.WriteInt32(count);

        for (var i = 0; i < count; i++)
        {
            var behavior = behaviors[i];
            if (behavior == null)
            {
                stream.WriteString(null);
                stream.WriteBytes(null);
                continue;
            }

            stream.WriteString(behavior.TypeKey);
            using var payload = new ReadWriteStream();
            if (behavior is ISerializable serializable) serializable.Serialize(payload);
            stream.WriteBytes(payload.ToArray());
        }
    }

    private static MapResBehavior[] ReadResourceBehaviors(ReadWriteStream stream)
    {
        var count = stream.ReadInt32();
        if (count < 0 || count > MaxBehaviorCount)
        {
            Debug.LogWarning($"[MapResBehaviorSerialization] 行为数量异常（{count}），忽略该条目的行为");
            return null;
        }

        var result = new MapResBehavior[count];
        for (var i = 0; i < count; i++)
        {
            var typeKey = stream.ReadString();
            var payload = stream.ReadBytes();
            if (string.IsNullOrEmpty(typeKey)) continue;

            var behavior = MapResBehavior.Create(typeKey);
            if (behavior == null) continue;

            // 零字段行为的数据长度可为 0，仍需要创建实例。
            using var dataStream = new ReadWriteStream(payload ?? Array.Empty<byte>());
            if (behavior is ISerializable serializable) serializable.Deserialize(dataStream);
            result[i] = behavior;
        }

        return result;
    }
}