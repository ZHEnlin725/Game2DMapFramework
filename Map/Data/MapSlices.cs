using UnityEngine;

/// <summary>分块地图的全局尺寸、原点、偏移和二维切片数组。</summary>
public partial class MapSlices
{
    [SerializableField] public Vector2Int mapSize, sliceSize;
    [SerializableField] public Vector2Int origin, offset;
    [SerializableField] public Slice[,] slices;
}