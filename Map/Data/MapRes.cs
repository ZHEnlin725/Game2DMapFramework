using System;
using UnityEngine;

/// <summary>烘焙出的单个场景资源条目，记录局部变换、包围盒和需要驱动的行为。</summary>
[Serializable]
public partial class MapRes
{
    [HideInInspector]
    [SerializableField] public int tag;

    [SerializableField] public string assetpath;

    [SerializableField] public Vector3 localScale;
    [SerializableField] public Vector3 localPosition;
    [SerializableField] public Vector3 localEulerAngles;
    [SerializableField] public Bounds localBounds;

    /// <summary>资源实例化后由 MapResManager 按生命周期驱动的行为模板。</summary>
    [SerializeReference] public MapResBehavior[] behaviors;

#if UNITY_EDITOR
    /// <summary>编辑期用于选择标签，保存时会同步到 tag。</summary>
    public MapResTag resTag;

    /// <summary>仅供编辑期反向还原时暂存场景实例。</summary>
    public GameObject inst;
#endif
}