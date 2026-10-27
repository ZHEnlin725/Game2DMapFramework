using System;

using UnityEngine;

/// <summary>一条表现规则：从候选预设锚点随机选点，并按权重挑选 Prefab。</summary>
[Serializable]
public partial class MapPresentationRule
{
    [SerializableField] public int ruleId;

    [SerializableField] public MapPresentationCandidate[] candidates;

    [SerializableField] public int minCount = 1, maxCount = 1;

    /// <summary>0 表示本次会话随机；非 0 表示固定种子，便于复现问题。</summary>
    [SerializableField] public int seed;

    /// <summary>重复生成总次数（含首次）；小于等于 0 表示无限循环。</summary>
    [SerializableField] public int repeatCount = 1;

    /// <summary>重复生成前的最短等待间隔（秒）。</summary>
    [SerializableField] public float repeatIntervalMin = 1f;

    /// <summary>重复生成前的最长等待间隔（秒）；与 Min 相等时按固定间隔执行。</summary>
    [SerializableField] public float repeatIntervalMax = 1f;

    /// <summary>规则级表现行为，会与候选人行为合并后一起执行。</summary>
    [SerializeReference] public MapPresentationBehavior[] behaviors;
}