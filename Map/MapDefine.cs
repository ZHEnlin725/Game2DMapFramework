/// <summary>
/// 地图模块全局常量：集中定义数据路径格式与默认参数，供运行时管理器与编辑器烘焙工具共用。
/// </summary>
public static class MapDefine
{
    /// <summary>MapLoader 初始化时的默认预载格数。</summary>
    public const int DefaultPreloadRange = 1;

    /// <summary>分块地图默认像素密度（每 Unity 单位的像素数）。</summary>
    public const int DefaultPixelsPerUnit = 100;
    
    /// <summary>分块地图默认渲染顺序。</summary>
    public const int DefaultMapSliceSortingOrder = -10;

    /// <summary>寻路采样点距最近 NavMesh 顶点的默认最大距离。</summary>
    public const float NavMeshSampleDistance = 10f;
    
    /// <summary>地图切片数据与资源数据共用的数据目录。</summary>
    public const string DataDirectory = "Assets/Data/MapData";

    /// <summary>地图切片数据路径格式：MapTextureSlicer 产出，MapManager 读取。</summary>
    public const string SliceDataFormat = DataDirectory + "/mapSlices@{0}.bytes";

    /// <summary>地图资源数据路径格式：MapResBaker 以 filename=mapRes 产出，MapManager 读取。</summary>
    public const string ResDataFormat = DataDirectory + "/mapRes@{0}.bytes";

    /// <summary>地图表现层数据路径格式：随机表现物的候选资源、锚点与路线。</summary>
    public const string PresentationDataFormat = DataDirectory + "/mapPresentation@{0}.bytes";
    
    /// <summary>地图导航网格。</summary>
    public const string NavMeshDataFormat = DataDirectory + "/mapNavMesh@{0}.asset";

    /// <summary>地图中的道具默认层级。</summary>
    public static readonly int LayerIndex = UnityEngine.LayerMask.NameToLayer("World");
}