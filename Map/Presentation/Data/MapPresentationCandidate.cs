using System;

using UnityEngine;

/// <summary>带权重和生命周期配置的候选 Prefab。</summary>
[Serializable]
public partial class MapPresentationCandidate
{
    [SerializableField] public string assetpath;

    /// <summary>候选的出生位置，坐标相对表现根节点；存在 Route 时默认取其首个 Waypoint。</summary>
    [SerializableField] public Vector3[] anchorPoints = Array.Empty<Vector3>();

    /// <summary>同一 Candidate 内是否避免重复使用同一个锚点。</summary>
    [SerializableField] public bool uniqueAnchors = true;

    [SerializableField] public float weight = 1f;
    [SerializableField] public Vector3 localScale = Vector3.one;
    [SerializableField] public Vector3 localEulerAngles;

    /// <summary>候选人自己的表现行为；会与规则级行为合并执行。</summary>
    [SerializeReference] public MapPresentationBehavior[] behaviors = Array.Empty<MapPresentationBehavior>();

    /// <summary>生命周期秒数；小于等于 0 表示不按时间销毁。</summary>
    [SerializableField] public float lifetime;

    /// <summary>所有表现行为完成后是否销毁该候选人实例。</summary>
    [SerializableField] public bool destroyWhenBehaviorsFinished = true;
}