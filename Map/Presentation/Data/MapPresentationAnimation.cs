using System;
using UnityEngine;

/// <summary>动画表现行为的数据字段。</summary>
[Serializable]
public partial class MapPresentationAnimation : MapPresentationBehavior
{
    [SerializableField] public float delay;
    [SerializableField] public string animationName = "idle";
    [SerializableField] public bool loop = true;

    /// <summary>动画的总时长；大于 0 时行为会在该时长后结束。</summary>
    [SerializableField] public float duration;
}