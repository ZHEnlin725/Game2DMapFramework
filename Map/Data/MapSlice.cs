using UnityEngine;

/// <summary>一个地图切片实体及其资源路径。</summary>
public partial class Slice
{
    [SerializableField] public RectInt position;
    [SerializableField] public string assetpath;
}