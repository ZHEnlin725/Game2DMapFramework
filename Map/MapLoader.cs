using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 地图渲染器（优化版）。
/// 相比原版的改进：
/// 1. 视口裁剪：只遍历当前块附近的有效行列，越界行/列直接跳过；
/// 2. 帧间去重：世界坐标与预载范围均未变化时，直接复用上次计算结果，避免每帧重复计算；
/// 3. 预载范围热更新：preloadRangeX/Y 改动会使缓存失效，立即生效；
/// 4. 同步路径零闭包分配：仅在使用 asyncLoad 时才创建回调委托；
/// 5. 失败重试：实例化失败会将块状态回退为 None，下次视口经过时可重新尝试加载；
/// 6. 行为兼容：对外属性与方法签名保持不变，可直接替换原组件使用。
/// </summary>
public class MapLoader : MonoBehaviour
{
    private enum SliceState
    {
        None,
        Loading,
        Loaded
    }

    private sealed class Slice
    {
        public int x = -1;
        public int y = -1;
        public string assetname;
        public SliceState sliceState;
        public GameObject gameObject;
        public double tick;
    }

    public Vector3 pivot;

    public int ppu;

    public int width { get; private set; }
    public int height { get; private set; }
    public int sliceWidth { get; private set; }
    public int sliceHeight { get; private set; }

    private int _preloadRangeX;

    public int preloadRangeX
    {
        get => _preloadRangeX;
        set
        {
            var clamped = Mathf.Max(1, value);
            if (_preloadRangeX == clamped) return;
            _preloadRangeX = clamped;
            InvalidateCellCache();
        }
    }

    private int _preloadRangeY;

    public int preloadRangeY
    {
        get => _preloadRangeY;
        set
        {
            var clamped = Mathf.Max(1, value);
            if (_preloadRangeY == clamped) return;
            _preloadRangeY = clamped;
            InvalidateCellCache();
        }
    }

    public Action<GameObject> OnSliceLoaded;

    public Action<GameObject> OnSliceWillBeDestroyed;

    public float releaseTimeThreshold = 20;

    private Slice[,] slices;

    private List<Slice> loadedSlices;

    private global::Slice[,] sources;

    private bool initialized;

    public Vector2Int origin { get; private set; }

    public Vector2Int offset { get; private set; }

    private int sliceRows;
    private int sliceCols;

    private int _lastCellX = int.MinValue;
    private int _lastCellY = int.MinValue;
    private int _lastRangeStamp = int.MinValue;
    private bool hasLastCell;

    public void Init(byte[] data)
    {
        Init(ReadWriteStream.Deserialize<MapSlices>(data));
    }

    public void Init(MapSlices data)
    {
        // 先清理上一份地图的运行时状态，但不清空外部订阅的事件，便于连续切换地图。
        ClearLoadedSlices();

        if (data == null || data.slices == null)
        {
            Debug.LogError("MapRendererOptimized.Init: slicing data is null.");
            return;
        }

        initialized = true;
        sources = data.slices;
        sliceRows = sources.GetLength(0);
        sliceCols = sources.GetLength(1);
        slices = new Slice[sliceRows, sliceCols];
        loadedSlices = new List<Slice>((sliceRows * sliceCols) >> 2);

        width = data.mapSize.x;
        height = data.mapSize.y;
        sliceWidth = data.sliceSize.x;
        sliceHeight = data.sliceSize.y;
        origin = data.origin;
        offset = data.offset;

        _lastCellX = int.MinValue;
        _lastCellY = int.MinValue;
        _lastRangeStamp = int.MinValue;
        hasLastCell = false;
    }

    public void Release()
    {
        ClearLoadedSlices();
        OnSliceLoaded = null;
        OnSliceWillBeDestroyed = null;
    }

    public void UpdatePosition(Vector3 position, bool asyncLoad = false)
    {
        if (!initialized || ppu <= 0) return;

        var diff = (position - pivot) * ppu;
        var x = Mathf.CeilToInt((diff.x - origin.x - offset.x) / sliceWidth) - 1;
        var y = Mathf.CeilToInt((diff.y - origin.y - offset.y) / sliceHeight) - 1;

        // preloadRange 作为低位数据打包，运行期调整范围也会触发重新计算。
        var rangeStamp = (preloadRangeX << 16) ^ (preloadRangeY & 0xFFFF);
        if (hasLastCell && x == _lastCellX && y == _lastCellY && rangeStamp == _lastRangeStamp)
            return;

        _lastCellX = x;
        _lastCellY = y;
        _lastRangeStamp = rangeStamp;
        hasLastCell = true;

        var minX = Mathf.Max(0, x - preloadRangeX);
        var maxX = Mathf.Min(sliceRows - 1, x + preloadRangeX);
        var minY = Mathf.Max(0, y - preloadRangeY);
        var maxY = Mathf.Min(sliceCols - 1, y + preloadRangeY);

        var now = Time.realtimeSinceStartup;
        for (var i = minX; i <= maxX; i++)
        {
            for (var j = minY; j <= maxY; j++)
            {
                var slicingData = sources[i, j];
                var slice = slices[i, j];
                if (slice == null)
                {
                    slice = new Slice { x = i, y = j, assetname = slicingData.assetpath };
                    slices[i, j] = slice;
                }

                if (slice.gameObject != null)
                {
                    slice.tick = now;
                    continue;
                }

                if (slice.sliceState != SliceState.None) continue;

                slice.sliceState = SliceState.Loading;
                var sliceCenter = slicingData.position.center / ppu;
                if (asyncLoad)
                {
                    void OnLoaded(GameObject go)
                    {
                        if (initialized)
                        {
                            InitSlice(slice, go, sliceCenter);
                        }
                        else
                        {
                            slice.sliceState = SliceState.None;
                            // ResourceManager.DestroyGameObject(go, false);
                            UnityEngine.Object.Destroy(go);
                        }
                    }

                    // ResourceManager.InstantiateAsync(slice.assetname, OnLoaded);
                    var prefab = YooAssetManager.LoadAssetSync<GameObject>(slice.assetname);
                    var inst = Instantiate(prefab);
                    OnLoaded(inst);
                }
                else
                {
                    var prefab = YooAssetManager.LoadAssetSync<GameObject>(slice.assetname);
                    var inst = Instantiate(prefab);
                    InitSlice(slice, inst, sliceCenter);
                }
            }
        }
    }

    private void Update()
    {
        if (!initialized) return;

        // 修复原版误用 preloadRangeX 计算 Y 方向释放边界的问题。
        var rangeX = preloadRangeX + 1;
        var rangeY = preloadRangeY + 1;
        var now = Time.realtimeSinceStartup;
        for (var i = loadedSlices.Count - 1; i >= 0; i--)
        {
            var block = loadedSlices[i];
            if ((Mathf.Abs(block.x - _lastCellX) >= rangeX || Mathf.Abs(block.y - _lastCellY) >= rangeY) &&
                now - block.tick > releaseTimeThreshold)
            {
                ReleaseSlice(block);
                loadedSlices.RemoveAt(i);
            }
        }
    }

    private void OnDestroy()
    {
        Release();
    }

    private void InitSlice(Slice slice, GameObject go, Vector3 positoin)
    {
        if (go)
        {
            go.layer = MapDefine.LayerIndex;
            slice.gameObject = go;
            slice.gameObject.transform.position = positoin + pivot;
            var renderer = slice.gameObject.GetComponent<Renderer>();
            if (renderer) renderer.sortingOrder = MapDefine.DefaultMapSliceSortingOrder;
#if UNITY_EDITOR
            slice.gameObject.transform.SetParent(transform, true);
#endif
            slice.sliceState = SliceState.Loaded;
            slice.tick = Time.realtimeSinceStartup;
            OnSliceLoaded?.Invoke(go);
            loadedSlices.Add(slice);
        }
        else
        {
            // 回退为 None，使后续帧可以再次尝试加载失败的块。
            slice.sliceState = SliceState.None;
            Debug.LogError($"Not Found:{slice.assetname}");
        }
    }

    private void ReleaseSlice(Slice slice)
    {
        var go = slice.gameObject;
        OnSliceWillBeDestroyed?.Invoke(go);
        // ResourceManager.DestroyGameObject(go, false);
        Destroy(go);
        slice.gameObject = null;
        slice.sliceState = SliceState.None;
    }

    private void ClearLoadedSlices()
    {
        if (loadedSlices != null)
        {
            foreach (var slice in loadedSlices)
            {
                if (slice.gameObject)
                {
                    // ResourceManager.DestroyGameObject(slice.gameObject, false);
                    Destroy(slice.gameObject);
                }

                slice.gameObject = null;
                slice.sliceState = SliceState.None;
            }
        }

        initialized = false;
        slices = null;
        loadedSlices = null;
        sources = null;
        hasLastCell = false;
        _lastCellX = int.MinValue;
        _lastCellY = int.MinValue;
        _lastRangeStamp = int.MinValue;
    }

    private void InvalidateCellCache()
    {
        hasLastCell = false;
        _lastRangeStamp = int.MinValue;
    }

    public bool drawGizmos;

#if UNITY_EDITOR

    private void OnDrawGizmos()
    {
        if (!drawGizmos || slices == null) return;

        Gizmos.color = Color.green;
        for (var i = 0; i < sliceRows; i++)
        {
            for (var j = 0; j < sliceCols; j++)
            {
                var rect = sources[i, j].position;
                var center = rect.center / ppu * 1f;
                var size = new Vector3(rect.width * 1f / ppu, rect.height * 1f / ppu);
                Gizmos.DrawWireCube(new Vector3(center.x + pivot.x, center.y + pivot.y), size);
            }
        }

        float minX = pivot.x + (origin.x + offset.x) * 1f / ppu,
            minY = pivot.y + (origin.y + offset.y) * 1f / ppu;
        float maxX = pivot.x + width * .5f / ppu,
            maxY = pivot.y + height * .5f / ppu;
        Gizmos.color = Color.red;
        Gizmos.DrawWireCube(new Vector3((minX + maxX) * .5f, (minY + maxY) * .5f),
            new Vector3((width - offset.x) * 1f / ppu, (height - offset.y) * 1f / ppu));
    }
#endif
}