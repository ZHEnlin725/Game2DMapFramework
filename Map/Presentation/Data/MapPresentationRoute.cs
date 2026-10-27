using System;
using UnityEngine;

public enum MapPresentationMoveMode
{
    None,
    NavMesh,
    Waypoint
}

/// <summary>寻路表现行为的数据字段，waypoints 为相对表现根节点的坐标。</summary>
[Serializable]
public partial class MapPresentationRoute : MapPresentationBehavior
{
    [SerializableField] public MapPresentationMoveMode moveMode = MapPresentationMoveMode.Waypoint;
    [SerializableField] public bool loop;
    [SerializableField] public Vector3[] waypoints = Array.Empty<Vector3>();

    /// <summary>不循环时，走到末尾后沿原路线反向返回。</summary>
    [SerializableField] public bool reverseOnEnd = true;

    [SerializableField] public float speed = 1f;
    [SerializableField] public float arrivalDistance = 0.015f;
}