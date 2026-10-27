using System;
using UnityEngine;

/// <summary>处理多态表现行为数组的附加二进制块；其余字段序列化由生成器完成。</summary>
public static class MapPresentationBehaviorSerialization
{
    /// <summary>紧跟规则数组的行为附加块标记。</summary>
    private const int BehaviorBlockMagic = 0x4D504252; // "MPBR"

    private const int MaxBehaviorCount = 64;

    public static void Write(ReadWriteStream stream, MapPresentationRule[] rules)
    {
        var ruleCount = rules?.Length ?? 0;
        stream.WriteInt32(BehaviorBlockMagic);
        stream.WriteInt32(ruleCount);

        for (var i = 0; i < ruleCount; i++)
            WriteRule(stream, rules?[i]);
    }

    public static void Read(ReadWriteStream stream, MapPresentationRule[] rules)
    {
        if (rules == null || stream.Length - stream.Position < 8) return;
        if (stream.ReadInt32() != BehaviorBlockMagic) return;

        var ruleCount = stream.ReadInt32();
        if (ruleCount != rules.Length)
        {
            Debug.LogWarning($"[MapPresentationBehaviorSerialization] 规则行为块数量不一致（{ruleCount}/{rules.Length}），忽略行为数据");
            return;
        }

        for (var i = 0; i < ruleCount; i++)
        {
            if (rules[i] == null)
            {
                SkipRule(stream);
                continue;
            }

            rules[i].behaviors = ReadBehaviorArray(stream);
            var candidateCount = stream.ReadInt32();
            var candidateList = rules[i].candidates;
            for (var j = 0; j < candidateCount; j++)
            {
                if (candidateList == null || j >= candidateList.Length || candidateList[j] == null)
                {
                    SkipBehaviorArray(stream);
                    continue;
                }

                candidateList[j].behaviors = ReadBehaviorArray(stream);
            }
        }
    }

    private static void WriteRule(ReadWriteStream stream, MapPresentationRule rule)
    {
        WriteBehaviorArray(stream, rule?.behaviors);

        var candidates = rule?.candidates;
        stream.WriteInt32(candidates?.Length ?? 0);
        if (candidates == null) return;

        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            WriteBehaviorArray(stream, candidate?.behaviors);
        }
    }

    private static void WriteBehaviorArray(ReadWriteStream stream, MapPresentationBehavior[] behaviors)
    {
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

    private static void SkipRule(ReadWriteStream stream)
    {
        SkipBehaviorArray(stream);
        var candidateCount = stream.ReadInt32();
        for (var i = 0; i < candidateCount; i++)
            SkipBehaviorArray(stream);
    }

    private static void SkipBehaviorArray(ReadWriteStream stream)
    {
        var count = stream.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            stream.ReadString();
            stream.ReadBytes();
        }
    }

    private static MapPresentationBehavior[] ReadBehaviorArray(ReadWriteStream stream)
    {
        var count = stream.ReadInt32();
        if (count < 0 || count > MaxBehaviorCount)
        {
            Debug.LogWarning($"[MapPresentationBehaviorSerialization] 行为数量异常（{count}），忽略该组行为");
            return Array.Empty<MapPresentationBehavior>();
        }

        var result = new MapPresentationBehavior[count];
        for (var i = 0; i < count; i++)
        {
            var typeKey = stream.ReadString();
            var payload = stream.ReadBytes();
            if (string.IsNullOrEmpty(typeKey)) continue;

            var behavior = MapPresentationBehavior.Create(typeKey);
            if (behavior == null) continue;

            using var dataStream = new ReadWriteStream(payload ?? Array.Empty<byte>());
            if (behavior is ISerializable serializable) serializable.Deserialize(dataStream);
            result[i] = behavior;
        }

        return result;
    }
}