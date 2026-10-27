using System.Collections.Generic;
using UnityEngine;

/// <summary>地图静态设置入口；后续可以继续增加其他设置分组。</summary>
public static class MapSettings
{
    private static readonly MapCameraSetting DefaultCamera = CreateDefaultCamera();

    private static readonly Dictionary<int, MapCameraSetting> CameraOverrides = new()
    {
        [0] = CreateDefaultCamera(),
    };

    /// <summary>按 MapId 获取相机设置；未配置的地图使用默认值。</summary>
    public static MapCameraSetting GetCamera(int mapId) =>
        CameraOverrides.TryGetValue(mapId, out var setting) ? setting : DefaultCamera;

    private static MapCameraSetting CreateDefaultCamera() => new()
    {
        OrthographicSize = 6.67f,
        ViewDistance = 10f,
        FarClipPlane = 500f,
        Expand = 0f,
        ElasticEdge = 0f,
        ElasticSpeed = 0f,
        ViewableBounds = new Bounds(new Vector3(0, -.6f), new Vector3(12f, 13.34f))
    };
}

/// <summary>地图相机相关常量设置。</summary>
public sealed class MapCameraSetting
{
    /// <summary>正交相机的初始视口半高；小于等于 0 表示按地图包围盒自适应。</summary>
    public float OrthographicSize { get; set; }

    /// <summary>相机到地图平面的初始距离。</summary>
    public float ViewDistance { get; set; }

    /// <summary>相机远裁剪面。</summary>
    public float FarClipPlane { get; set; }

    /// <summary>相机允许超出地图边界的大小。</summary>
    public float Expand { get; set; }

    /// <summary>接近边界时开始回弹的外沿距离。</summary>
    public float ElasticEdge { get; set; }

    /// <summary>回弹速度。</summary>
    public float ElasticSpeed { get; set; }
    
    //相机视野窗口大小
    public Bounds ViewableBounds { get; set; }
}