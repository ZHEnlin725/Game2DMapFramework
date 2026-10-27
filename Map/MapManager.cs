using System;
using System.Collections.Generic;
using FairyGUI;
using UI.Windows;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 地图总管理器：对外只需要地图 ID 即可完成初始化。
/// 集中负责 MapLoader / MapResManager / MapCamera 的初始化、每帧驱动与释放，并提供基于 NavMesh 的寻路 API。
/// 地图初始化以 MapLoader 为必需项；资源数据与寻路数据均可缺失，缺失时按无资源 / 无寻路降级运行。
/// 数据路径与默认参数统一读取 MapDefine / MapSettings，避免运行时与烘焙工具各自为政。
/// </summary>
public class MapManager : MonoSingletonTemplate<MapManager>
{
    public const int InvalidMapId = -1;

    [Tooltip("关闭后由业务方手动调用 Tick() 驱动")] public bool autoUpdate = true;

    [Tooltip("地图切片使用的像素密度，转发给 MapLoader.ppu")]
    public int pixelsPerUnit = MapDefine.DefaultPixelsPerUnit;

    [Tooltip("切片大图的贴图原点在世界空间的锚点")] public Vector3 mapPivot = new Vector3(1000, 1000);

    [Tooltip("寻路采样点距离最近 NavMesh 顶点的最大容忍距离")]
    public float navMeshSampleDistance = MapDefine.NavMeshSampleDistance;

    private static readonly Vector2[] ProbeOffsets =
    {
        Vector2.zero,
        new Vector2(0.25f, 0.25f),
        new Vector2(-0.25f, 0.25f),
        new Vector2(0.25f, -0.25f),
        new Vector2(-0.25f, -0.25f),
    };

    private MapLoader mapLoader;
    private MapResManager mapResManager;
    private Camera mapCameraComponent;
    private MapCamera mapCameraController;
    private MapPresentationDirector presentationDirector;
    private Transform resRoot;
    private readonly MapEventCenter eventCenter = new MapEventCenter();
    private MapEventRegistrar eventRegistrar;

    private int currentMapId = InvalidMapId;
    private bool resInitialized;
    private bool hasPathData;
    private bool active;
    private Bounds mapBounds;
    private MapCameraSetting cameraSetting = MapSettings.GetCamera(InvalidMapId);

    private readonly List<string> navMeshDataPaths = new List<string>();
    private readonly List<NavMeshData> navMeshDataList = new List<NavMeshData>();
    private readonly List<NavMeshDataInstance> navMeshDataInstances = new List<NavMeshDataInstance>();
    private NavMeshPath scratchPath;

    public int CurrentMapId => currentMapId;

    /// <summary>当前地图是否存在可用寻路数据；由初始化探测与寻路调用共同维护。</summary>
    public bool HasPathData => currentMapId != InvalidMapId && hasPathData;

    public MapLoader Loader => mapLoader;

    public MapResManager ResManager => mapResManager;

    /// <summary>初始化时创建的 MapCamera 控制器，业务可调用 OnBeginSwipe / OnZoom 等手势接口。</summary>
    public MapCamera CameraController => mapCameraController;

    /// <summary>地图事件中心：地图行为在此广播事件，UI 等外部系统通过 MapManager.Singleton.EventCenter 订阅。</summary>
    public MapEventCenter EventCenter => eventCenter;

    /// <summary>向地图事件中心广播一条地图事件。</summary>
    public void BroadcastMapEvent(in MapEvent evt) => eventCenter.Broadcast(evt);

    /// <summary>已加载的 NavMeshData 资源路径集合；按路径去重。</summary>
    public IReadOnlyList<string> NavMeshDataPaths => navMeshDataPaths;

    private void Update()
    {
        if (autoUpdate) Tick();
    }

    private void OnDestroy()
    {
        Release();
    }

    /// <summary>
    /// 按地图 ID 初始化整套地图子系统，并创建正交相机（挂 MapCamera）。
    /// 切片数据缺失视为初始化失败；资源数据或寻路数据缺失只会降级，不影响地图运行。
    /// </summary>
    public bool Init(int mapId)
    {
        Release();

        var sliceBytes = YooAssetManager.LoadBytesSync(string.Format(MapDefine.SliceDataFormat, mapId));
        if (sliceBytes == null || sliceBytes.Length == 0)
        {
            Debug.LogWarning(
                $"[MapManager] 切片数据不存在，地图初始化失败。mapId={mapId}, path={string.Format(MapDefine.SliceDataFormat, mapId)}");
            return false;
        }

        MapSlices slices;
        try
        {
            slices = ReadWriteStream.Deserialize<MapSlices>(sliceBytes);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Debug.LogError($"[MapManager] 切片数据解析失败。mapId={mapId}");
            return false;
        }

        if (slices == null || slices.slices == null)
        {
            Debug.LogError($"[MapManager] 切片数据内容为空。mapId={mapId}");
            return false;
        }

        cameraSetting = MapSettings.GetCamera(mapId);
        transform.position = mapPivot;
        active = true;
        
        EnsureChildren();

        mapLoader.pivot = mapPivot;
        mapLoader.ppu = pixelsPerUnit;
        mapLoader.preloadRangeX = MapDefine.DefaultPreloadRange;
        mapLoader.preloadRangeY = MapDefine.DefaultPreloadRange;
        mapLoader.Init(slices);

        mapBounds = CalculateMapBounds(slices, pixelsPerUnit, mapPivot);
        EnsureCamera();
        mapResManager.Init(mapBounds);
        resInitialized = true;

        // 背景 click catcher：InputGesture 仅在射线命中时回调 OnClickScreen，
        // 该碰撞体保证空白地图区域也有命中，资源自身的碰撞体覆盖在其上。
        var collider = mapLoader.gameObject.AddComponent<BoxCollider>();
        collider.size = mapBounds.size;
        
        // MapResManager 只有拿到有效 Bounds 才能建四叉树，资源数据缺失时不初始化。
        var resBytes = YooAssetManager.LoadBytesSync(string.Format(MapDefine.ResDataFormat, mapId));
        if (resBytes != null && resBytes.Length > 0)
        {
            var reslist = MapReslist.CreateInstance(resBytes);
            if (reslist != null && reslist.resources is { Length: > 0 })
            {
                mapResManager.SetMapSceneReslist(resRoot, reslist);
            }
            else
            {
                Debug.LogWarning($"[MapManager] 资源数据为空，本地图将不启用资源管理。mapId={mapId}");
            }
        }
        else
        {
            Debug.LogWarning($"[MapManager] 资源数据不存在，本地图将不启用资源管理。mapId={mapId}");
        }

        TryInitPresentation(mapId);
        TryInitNavMesh(mapId);

        currentMapId = mapId;
        ProbePathData();
        EnsureEventRegistrar();
        eventRegistrar.Register(mapId);
        return true;
    }

    public void SetActive(bool enable)
    {
        active = enable;
        InputGesture.sharedInst.enableClick = active;
        InputGesture.sharedInst.enableSwipe = active;
    }

    /// <summary>释放地图子系统、内部相机与全部寻路数据；重复调用安全。</summary>
    public void Release()
    {
        eventRegistrar?.UnregisterAll();
        presentationDirector?.Release();
        ReleaseNavMeshData();
        ReleaseCamera();

        if (mapLoader != null) mapLoader.Release();

        if (mapResManager != null)
        {
            mapResManager.DestroyAllInsts(true);
            mapResManager.Release();
            ClearResRoot();
        }

        resInitialized = false;
        hasPathData = false;
        mapBounds = default;
        active = false;
        currentMapId = InvalidMapId;
    }

    /// <summary>
    /// 按资源路径加载 NavMeshData 并挂到全局 NavMesh；支持多次加载不同路径，相同路径会被去重。
    /// 调用后当前地图的 HasPathData 会重新探测；寻路数据缺失时寻路 API 直接返回 false。
    /// </summary>
    public bool LoadNavMeshData(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return false;
        if (navMeshDataPaths.Contains(assetPath)) return true;

        var data = YooAssetManager.LoadAssetSync<NavMeshData>(assetPath);
        if (data == null)
        {
            Debug.LogWarning($"[MapManager] NavMeshData 加载失败。path={assetPath}");
            return false;
        }

        data.position += mapPivot;
        navMeshDataPaths.Add(assetPath);
        navMeshDataList.Add(data);
        navMeshDataInstances.Add(NavMesh.AddNavMeshData(data));

        if (currentMapId != InvalidMapId) ProbePathData();
        return navMeshDataInstances[navMeshDataInstances.Count - 1].valid;
    }

    /// <summary>每帧驱动：先刷新 MapCamera 视口，再驱动切片加载与资源四叉树。</summary>
    public void Tick()
    {
        if (!active || currentMapId == InvalidMapId || mapCameraController == null) return;

        mapCameraController.DoUpdate();
        UpdatePreloadRangeFromCameraView();

        mapLoader?.UpdatePosition(mapCameraController.GetViewCenter(), false);

        var viewBounds = mapCameraController.GetViewBounds();
        if (resInitialized) mapResManager.UpdateView(viewBounds);
        presentationDirector?.Tick(Time.deltaTime, viewBounds);
    }

    /// <summary>
    /// 计算两点之间的 NavMesh 路径；当前地图没有寻路数据或任一端点不可达时返回 false。
    /// 结果 waypoints 为空表示调用失败，内容由本次调用重建。
    /// </summary>
    public bool TryFindPath(Vector3 start, Vector3 end, List<Vector3> waypoints)
    {
        if (waypoints == null || currentMapId == InvalidMapId) return false;

        waypoints.Clear();
        if (!hasPathData && !ProbePathData()) return false;

        if (!NavMesh.SamplePosition(start, out var startHit, navMeshSampleDistance, NavMesh.AllAreas))
            return false;
        if (!NavMesh.SamplePosition(end, out var endHit, navMeshSampleDistance, NavMesh.AllAreas))
            return false;

        // NavMeshPath 构造依赖 Unity 引擎上下文，不能在字段初始化器里创建。
        if (scratchPath == null) scratchPath = new NavMeshPath();

        if (!NavMesh.CalculatePath(startHit.position, endHit.position, NavMesh.AllAreas, scratchPath))
            return false;
        if (scratchPath.status == NavMeshPathStatus.PathInvalid) return false;

        foreach (var corner in scratchPath.corners) waypoints.Add(corner);

        // 直线可达时 corners 只有起终点；保证终点一定出现在结果里。
        if (waypoints.Count == 0 || waypoints[waypoints.Count - 1] != endHit.position)
            waypoints.Add(endHit.position);

        return waypoints.Count > 0;
    }

    /// <summary>
    /// 使用 NavMeshAgent 向目标点移动；当前地图没有寻路数据或目标点不可达时返回 false。
    /// </summary>
    public bool TryMoveAgent(NavMeshAgent agent, Vector3 destination)
    {
        if (agent == null || currentMapId == InvalidMapId) return false;
        if (!hasPathData && !ProbePathData()) return false;

        if (!NavMesh.SamplePosition(destination, out var hit, navMeshSampleDistance, NavMesh.AllAreas))
            return false;

        return agent.SetDestination(hit.position);
    }

    private void EnsureChildren()
    {
        if (mapLoader == null)
        {
            var loaderGo = new GameObject("MapLoader");
            loaderGo.transform.SetParent(transform, false);
            mapLoader = loaderGo.AddComponent<MapLoader>();
        }

        if (mapResManager == null)
        {
            var resGo = new GameObject("MapResManager");
            resGo.transform.SetParent(transform, false);
            mapResManager = resGo.AddComponent<MapResManager>();
        }

        if (resRoot == null)
        {
            var rootGo = new GameObject("MapResRoot");
            rootGo.transform.SetParent(transform, false);
            resRoot = rootGo.transform;
        }

        if (presentationDirector == null)
        {
            var directorGo = new GameObject("MapPresentationDirector");
            directorGo.transform.SetParent(transform, false);
            presentationDirector = directorGo.AddComponent<MapPresentationDirector>();
        }
    }

    /// <summary>加载可选的表现层数据文件，并激活本张地图的表现规则。</summary>
    private void TryInitPresentation(int mapId)
    {
        var presentationBytes = YooAssetManager.LoadBytesSync(string.Format(MapDefine.PresentationDataFormat, mapId));
        if (presentationBytes == null || presentationBytes.Length == 0) return;

        try
        {
            var definition = MapPresentationDefinition.CreateInstance(presentationBytes);
            presentationDirector.Init(mapId, resRoot, definition);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    private void TryInitNavMesh(int mapId)
    {
        var assetPath = string.Format(MapDefine.NavMeshDataFormat, mapId);
        LoadNavMeshData(assetPath);
    }

    private void EnsureEventRegistrar()
    {
        if (eventRegistrar == null)
            eventRegistrar = new MapEventRegistrar(eventCenter);
    }

    /// <summary>创建正交相机并挂载 MapCamera，相机朝向为 +Z，视口尺寸按地图包围盒计算。</summary>
    private void EnsureCamera()
    {
        if (mapCameraController != null) return;

        var cameraGo = new GameObject("MapCamera");
        cameraGo.transform.SetParent(transform, false);

        var camera = cameraGo.AddComponent<Camera>();
        camera.orthographic = true;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.farClipPlane = cameraSetting.FarClipPlane;
        camera.cullingMask = 1 << MapDefine.LayerIndex;
        var data = camera.GetUniversalAdditionalCameraData();
        if (data != null)
        {
            data.renderType = CameraRenderType.Base;
            var uiCamData = StageCamera.main.GetUniversalAdditionalCameraData();
            uiCamData.renderType = CameraRenderType.Overlay;
            data.cameraStack.Add(StageCamera.main);
        }

        mapCameraComponent = camera;

        mapCameraController = cameraGo.AddComponent<MapCamera>();
        mapCameraController.ManualUpdate = true;
        mapCameraController.elasticEdge = cameraSetting.ElasticEdge;
        mapCameraController.expand = cameraSetting.Expand;
        mapCameraController.elasticSpeed = cameraSetting.ElasticSpeed;

        InputGesture.sharedInst.camera = camera;
        InputGesture.sharedInst.raycastLayerMask = 1 << MapDefine.LayerIndex;
        InputGesture.sharedInst.OnBeginSwipeScreen += mapCameraController.OnBeginSwipe;
        InputGesture.sharedInst.OnSwipeScreen += mapCameraController.OnSwipe;
        InputGesture.sharedInst.OnEndSwipeScreen += mapCameraController.OnEndSwipe;
        // InputGesture.sharedInst.OnZoom += mapCameraController.OnZoom;
        //常规使用XZ平面 这里2D使用XY平面 所以旋转-90度
        var viewBounds = new Bounds(mapBounds.center + cameraSetting.ViewableBounds.center, cameraSetting.ViewableBounds.size);
        mapCameraController.UpdateMapBoundsEulerAngles(viewBounds, -90 * Vector3.right);

        PositionCameraToFitMap();
        mapCameraController.DoUpdate();
    }

    /// <summary>把正交相机摆到能看到完整地图包围盒的位置，并设置视口尺寸。</summary>
    private void PositionCameraToFitMap()
    {
        if (mapCameraController == null || mapCameraComponent == null) return;

        var size = GetCameraOrthographicSize();
        mapCameraComponent.orthographicSize = size;
        mapCameraController.SetOrthgraphicSizeMinMax(size, size);
        mapCameraController.transform.position = mapBounds.center + cameraSetting.ViewableBounds.center + new Vector3(0f, 0f, -cameraSetting.ViewDistance);
        mapCameraController.transform.rotation = Quaternion.identity;
        mapCameraController.StopTranslation();
    }

    /// <summary>相机视口半高：MapSettings 配置了 OrthographicSize 则使用配置值，否则按地图包围盒和屏幕比例自动计算。</summary>
    private float GetCameraOrthographicSize()
    {
        if (cameraSetting.OrthographicSize > 0f) return cameraSetting.OrthographicSize;

        var aspect = mapCameraComponent != null ? Mathf.Max(0.01f, mapCameraComponent.aspect) : 1f;
        return Mathf.Max(0.01f, Mathf.Max(mapBounds.extents.y, mapBounds.extents.x / aspect));
    }

    private void ReleaseCamera()
    {
        if (mapCameraController != null)
        {
            InputGesture.sharedInst.OnBeginSwipeScreen -= mapCameraController.OnBeginSwipe;
            InputGesture.sharedInst.OnSwipeScreen -= mapCameraController.OnSwipe;
            InputGesture.sharedInst.OnEndSwipeScreen -= mapCameraController.OnEndSwipe;
            InputGesture.sharedInst.camera = null;
            InputGesture.sharedInst.raycastLayerMask = 0;
            Destroy(mapCameraController.gameObject);
            mapCameraController = null;
        }

        if (StageCamera.main != null)
        {
            var uiCamData = StageCamera.main.GetUniversalAdditionalCameraData();
            uiCamData.renderType = CameraRenderType.Base;
        }
        
        mapCameraComponent = null;
    }

    /// <summary>按当前相机视野大小折算 MapLoader 的 X/Y 预载格数。</summary>
    private void UpdatePreloadRangeFromCameraView()
    {
        if (mapCameraController == null || mapLoader == null ||
            mapLoader.ppu <= 0 || mapLoader.sliceWidth <= 0 || mapLoader.sliceHeight <= 0)
        {
            return;
        }

        var viewSize = mapCameraController.GetViewBounds().size;
        var sliceWorldWidth = mapLoader.sliceWidth / (float)mapLoader.ppu;
        var sliceWorldHeight = mapLoader.sliceHeight / (float)mapLoader.ppu;

        mapLoader.preloadRangeX = Mathf.CeilToInt(viewSize.x *.5f/ sliceWorldWidth);
        mapLoader.preloadRangeY = Mathf.CeilToInt(viewSize.y *.5f/ sliceWorldHeight);
    }

    /// <summary>移除全部 NavMeshData 实例并卸载对应 YooAsset 资源，清空集合。</summary>
    private void ReleaseNavMeshData()
    {
        for (var i = 0; i < navMeshDataInstances.Count; i++)
        {
            if (navMeshDataInstances[i].valid)
                navMeshDataInstances[i].Remove();
        }

        for (var i = 0; i < navMeshDataPaths.Count; i++)
            YooAssetManager.UnloadAsset(navMeshDataPaths[i]);

        navMeshDataPaths.Clear();
        navMeshDataList.Clear();
        navMeshDataInstances.Clear();
        hasPathData = false;
    }

    /// <summary>销毁资源根节点下的残留实例。MapResManager 内部的真实销毁调用尚未接通，这里兜底防止换图后旧实例堆积。</summary>
    private void ClearResRoot()
    {
        if (resRoot == null) return;

        for (var i = resRoot.childCount - 1; i >= 0; i--)
        {
            var child = resRoot.GetChild(i);
            if (child != null) Destroy(child.gameObject);
        }
    }

    private bool ProbePathData()
    {
        if (currentMapId == InvalidMapId)
        {
            hasPathData = false;
            return false;
        }

        var extents = mapBounds.extents;
        foreach (var offset in ProbeOffsets)
        {
            var point = mapBounds.center +
                        new Vector3(extents.x * offset.x, extents.y * offset.y, 0f);
            if (NavMesh.SamplePosition(point, out _, navMeshSampleDistance, NavMesh.AllAreas))
            {
                hasPathData = true;
                return true;
            }
        }

        hasPathData = false;
        return false;
    }

    private static Bounds CalculateMapBounds(MapSlices data, int ppu, Vector3 pivot)
    {
        var min = Vector2.positiveInfinity;
        var max = Vector2.negativeInfinity;
        var slicings = data.slices;

        for (var i = 0; i < slicings.GetLength(0); i++)
        for (var j = 0; j < slicings.GetLength(1); j++)
        {
            var rect = slicings[i, j].position;
            min = Vector2.Min(min, new Vector2(rect.xMin, rect.yMin));
            max = Vector2.Max(max, new Vector2(rect.xMax, rect.yMax));
        }

        if (min.x > max.x || min.y > max.y)
            return new Bounds(pivot, new Vector3(1f, 1f, 1f));

        var centerPx = (min + max) * 0.5f;
        var sizePx = max - min;
        return new Bounds(
            pivot + new Vector3(centerPx.x, centerPx.y) / ppu,
            new Vector3(sizePx.x, sizePx.y) / ppu);
    }
}