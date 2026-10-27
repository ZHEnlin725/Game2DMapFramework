using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 地图资源四叉树管理器。
/// 离线阶段由 MapSceneResInfoBaker 烘焙出资源记录；运行时根据视口 Bounds 动态实例化、回收资源，
/// 并驱动资源身上的 MapResBehavior 生命周期（OnResCreate / OnResUpdate / OnResDestroy）。
/// 对外接口与旧版保持一致，可直接替换原文件。
/// </summary>
public class MapResManager : MonoBehaviour
{
    #region 对外接口（签名与旧版一致）

    /// <summary>实例销毁之前触发，参数可能为 null（该实例尚未创建成功的情况）。</summary>
    public event Action<GameObject> OnWillBeDestroyed;

    public bool drawTree;

    [Tooltip("是否额外绘制烘焙场景资源所在的四叉树")] public bool drawSceneTree;

    public void Init(Bounds worldBounds)
    {
        resTree = new MapResTree(worldBounds);
        sceneResTree = new MapResTree(worldBounds);
        InvalidateCollectCache();
    }

    /// <summary>注册地图上拆解出来的美术资源，记录的是相对 root 的局部 TRS；条目内的行为会在实例化后驱动。</summary>
    public void SetMapSceneReslist(Transform root, MapReslist mapReslist,
        Action<int, GameObject> onLoaded = null, Func<int, bool> visible = null)
    {
        if (root == null)
        {
            Debug.LogWarning("[MapResManager] SetMapSceneReslist 需要 root Transform");
            return;
        }

        if (sceneResTree == null)
        {
            Debug.LogWarning("[MapResManager] 请先调用 Init 再注册场景资源");
            return;
        }

        if (mapReslist == null || mapReslist.resources == null) return;

        var matrix = root.localToWorldMatrix;
        foreach (var resData in mapReslist.resources)
        {
            if (resData == null) continue;

            var bounds = resData.localBounds;
            var localPosition = resData.localPosition;
            var localEulerAngles = resData.localEulerAngles;
            var localScale = resData.localScale;
            var trs = Matrix4x4.TRS(localPosition, Quaternion.Euler(localEulerAngles), localScale);
            sceneResTree.Insert(new MapResInfo
            {
                aabb = CalculateWorldAABB(matrix * trs, bounds),
                scale = localScale,
                position = localPosition,
                eulerAngles = localEulerAngles,
                assetpath = resData.assetpath,
                parent = root,
                behaviorTemplates = resData.behaviors,
                onLoaded = go => onLoaded?.Invoke(resData.tag, go),
                visible = () => visible == null || visible(resData.tag)
            });
        }

        InvalidateCollectCache();
    }

    public void AddMapResInfo(ulong key, Bounds bounds, string assetpath, Vector3 position, Vector3 scale,
        Vector3 eulerAngles, Action<GameObject> onLoaded = null, Func<bool> visible = null)
    {
        if (resTree == null)
        {
            Debug.LogWarning("[MapResManager] 请先调用 Init 再添加资源信息");
            return;
        }

        var resInfo = reusableResInfos.Count > 0 ? reusableResInfos.Dequeue() : new MapResInfo();

        resInfo.key = key;
        resInfo.aabb = new AABB(bounds.center, bounds.size);
        resInfo.assetpath = assetpath;
        resInfo.position = position;
        resInfo.scale = scale;
        resInfo.eulerAngles = eulerAngles;
        resInfo.onLoaded = onLoaded;
        resInfo.visible = visible;
        resInfo.behaviorTemplates = null;
        resInfo.invalid = false;

        resTree.Insert(resInfo);
        InvalidateCollectCache();
    }

    public bool RemoveMapResInfo(ulong key, Bounds bounds, Action<GameObject> destroyCallback = null)
    {
        resTree?.Remove(new AABB(bounds.center, bounds.size), key);
        InvalidateCollectCache();

        for (var i = insts.Count - 1; i >= 0; i--)
        {
            if (insts[i].key != key) continue;

            var info = insts[i];
            insts.RemoveAt(i);
            info.invalid = true;
            DestroyInstance(info, true, destroyCallback);
            ResetToPool(info);
            return true;
        }

        return false;
    }

    public void UpdateView(Bounds bounds)
    {
        var aabb = (AABB)bounds;

        for (var i = insts.Count - 1; i >= 0; i--)
        {
            var info = insts[i];
            if (!aabb.Intersects(info.aabb))
            {
                DestroyInstance(info, true);
                info.go = null;
                info.onLoaded = null;
                info.visible = null;
                insts.RemoveAt(i);
            }
            else if (info.go != null)
            {
                info.go.SetActiveSafe(info.visible == null || info.visible());
                NotifyBehaviorsUpdate(info);
            }
        }

        if (resTree == null) return;

        // 视口未变化时跳过四叉树收集：已实例化条目无需重复遍历，增删资源时会作废缓存。
        if (hasLastCollectBounds &&
            bounds.center == lastCollectBounds.center &&
            bounds.size == lastCollectBounds.size)
            return;

        hasLastCollectBounds = true;
        lastCollectBounds = bounds;

        buffer.Clear();
        resTree.Collect(aabb, buffer);
        sceneResTree?.Collect(aabb, buffer);

        foreach (var resInfo in buffer)
        {
            if (resInfo.invalid || resInfo.go != null) continue;

            // var instance = ResourceManager.Instantiate(resInfo.assetpath);
            var prefab = YooAssetManager.LoadAssetSync<GameObject>(resInfo.assetpath);
            var instance = Instantiate(prefab);
            if (instance == null)
            {
                Debug.LogError($"[MapResManager] Can not find {resInfo.assetpath}");
                continue;
            }

            instance.layer = MapDefine.LayerIndex;
            resInfo.go = instance;
            var trans = instance.transform;
            if (resInfo.parent != null) trans.SetParent(resInfo.parent);
            trans.localPosition = resInfo.position;
            trans.localScale = resInfo.scale;
            trans.localEulerAngles = resInfo.eulerAngles;
            resInfo.onLoaded?.Invoke(instance);
            instance.SetActiveSafe(resInfo.visible == null || resInfo.visible());
            CreateBehaviors(resInfo, instance);
            insts.Add(resInfo);
        }
    }

    public void DestroyAllInsts(bool cache = true)
    {
        for (var i = insts.Count - 1; i >= 0; i--)
        {
            var info = insts[i];
            DestroyInstance(info, cache);
            info.go = null;
            insts.RemoveAt(i);
        }

        InvalidateCollectCache();
    }

    public void Release()
    {
        foreach (var inst in insts)
        {
            if (inst.go == null) continue;
            DestroyInstance(inst, true);
            inst.go = null;
        }

        insts.Clear();
        reusableResInfos.Clear();

        resTree = null;
        sceneResTree = null;
        OnWillBeDestroyed = null;
        hasLastCollectBounds = false;
    }

    #endregion

    private MapResTree resTree;
    private MapResTree sceneResTree;

    private Bounds lastCollectBounds;
    private bool hasLastCollectBounds;

    private readonly List<MapResInfo> insts = new List<MapResInfo>();
    private readonly List<MapResInfo> buffer = new List<MapResInfo>();
    private readonly Queue<MapResInfo> reusableResInfos = new Queue<MapResInfo>();

    /// <summary>原版对烘焙数据的包围盒做缩放近似，这里改为变换八个顶点，旋转下结果更准确。</summary>
    private static AABB CalculateWorldAABB(Matrix4x4 trs, Bounds localBounds)
    {
        var min = Vector3.positiveInfinity;
        var max = Vector3.negativeInfinity;
        var center = localBounds.center;
        var extents = localBounds.extents;

        for (var x = -1; x <= 1; x += 2)
        for (var y = -1; y <= 1; y += 2)
        for (var z = -1; z <= 1; z += 2)
        {
            var corner = trs.MultiplyPoint3x4(center + new Vector3(extents.x * x, extents.y * y, extents.z * z));
            min = Vector3.Min(min, corner);
            max = Vector3.Max(max, corner);
        }

        return new AABB((Vector2)((min + max) * 0.5f), (Vector2)(max - min));
    }

    private void ResetToPool(MapResInfo info)
    {
        info.go = null;
        info.parent = null;
        info.onLoaded = null;
        info.visible = null;
        info.behaviorTemplates = null;
        info.activeBehaviors.Clear();
        info.invalid = true;
        reusableResInfos.Enqueue(info);
    }

    /// <summary>树内容变化后作废视口收集缓存，确保新增资源当帧即可被收集。</summary>
    private void InvalidateCollectCache()
    {
        hasLastCollectBounds = false;
    }

    #region 行为生命周期

    /// <summary>为实例创建独立的行为副本并触发 OnResCreate。</summary>
    private void CreateBehaviors(MapResInfo info, GameObject target)
    {
        var templates = info.behaviorTemplates;
        if (templates == null || templates.Length == 0) return;

        for (var i = 0; i < templates.Length; i++)
        {
            var template = templates[i];
            if (template == null) continue;

            var behavior = template.Clone();
            if (behavior == null) continue;

            behavior.OnCreate(target);
            info.activeBehaviors.Add(behavior);
        }
    }

    /// <summary>实例存活期间每帧触发 OnResUpdate。</summary>
    private void NotifyBehaviorsUpdate(MapResInfo info)
    {
        if (info.activeBehaviors.Count == 0) return;

        var target = info.go;
        var deltaTime = Time.deltaTime;
        for (var i = 0; i < info.activeBehaviors.Count; i++)
            info.activeBehaviors[i]?.OnUpdate(target, deltaTime);
    }

    /// <summary>销毁前触发 OnResDestroy 并清理实例级行为。</summary>
    private void NotifyBehaviorsDestroy(MapResInfo info)
    {
        if (info.activeBehaviors.Count == 0) return;

        var target = info.go;
        for (var i = 0; i < info.activeBehaviors.Count; i++)
            info.activeBehaviors[i]?.OnDestroy(target);
        info.activeBehaviors.Clear();
    }

    #endregion

    private void DestroyInstance(MapResInfo info, bool cache, Action<GameObject> customHandler = null)
    {
        if (info == null) return;

        NotifyBehaviorsDestroy(info);
        if (customHandler != null)
        {
            customHandler(info.go);
            return;
        }

        OnWillBeDestroyed?.Invoke(info.go);
        // ResourceManager.DestroyGameObject(info.go, cache);
        Destroy(info.go);
    }

    private void OnDestroy()
    {
        Release();
    }

    private void OnDrawGizmos()
    {
#if UNITY_EDITOR
        if (drawTree) resTree?.DrawGizmos();
        if (drawSceneTree) sceneResTree?.DrawGizmos();
#endif
    }

    /// <summary>2D 包围盒，语义与旧版一致；注意 Contains 在旧版里会把 max.y 误写成 max.x，这里已修正。</summary>
    [Serializable]
    public struct AABB
    {
        public Vector2 center;
        public Vector2 extents;

        public AABB(Vector2 center, Vector2 size)
        {
            this.center = center;
            extents = size * 0.5f;
        }

        public Vector2 size
        {
            get => extents * 2f;
            set => extents = value * 0.5f;
        }

        public Vector2 min => center - extents;

        public Vector2 max => center + extents;

        public void SetMinMax(Vector2 minValue, Vector2 maxValue)
        {
            extents = (maxValue - minValue) * 0.5f;
            center = minValue + extents;
        }

        public bool Contains(Vector2 point) =>
            point.x >= min.x && point.x <= max.x &&
            point.y >= min.y && point.y <= max.y;

        public bool Contains(AABB other) =>
            Contains(other.min) && Contains(other.max);

        public bool Intersects(AABB other) =>
            min.x <= other.max.x && max.x >= other.min.x &&
            min.y <= other.max.y && max.y >= other.min.y;

        public override string ToString() => $"center:{center},size:{size}";

        public static implicit operator AABB(Bounds value) => new(value.center, value.size);
    }

    private sealed class MapResTree
    {
        private readonly MapResNode root;

        public MapResTree(AABB bounds) => root = new MapResNode { aabb = bounds };

        public void Insert(MapResInfo info) => root?.Insert(info);

        public void Remove(AABB bounds, ulong key) => root?.Remove(bounds, key);

        public void Collect(AABB bounds, List<MapResInfo> results) => root?.Collect(bounds, results);

#if UNITY_EDITOR
        public void DrawGizmos() => root?.DrawGizmos();
#endif
    }

    /// <summary>
    /// 惰性展开的四叉树节点：只有当对象恰好只与唯一子格相交且未达最大深度时才真正建立子层，
    /// 其余情况存放在当前节点，避免旧版一次预建 4^5 个空节点造成的内存浪费。
    /// 放置规则与旧版一致：每层统计四个子格与目标的相交数，恰为一个时下探，否则停在当前层。
    /// </summary>
    private sealed class MapResNode
    {
        public const int MaxDepth = 5;

        private static readonly Vector2[] CornerOffsets =
        {
            new Vector2(-1, -1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(1, 1)
        };

#if UNITY_EDITOR
        private static readonly Color[] DepthColors =
        {
            Color.white, Color.red, Color.green, Color.blue, Color.cyan, Color.magenta
        };
#endif

        public int depth;
        public AABB aabb;
        public MapResNode[] children;
        public List<MapResInfo> infoList;

        private bool IsLeaf => children == null;

        public void Insert(MapResInfo info)
        {
            if (!aabb.Intersects(info.aabb))
                return;

            var childIndex = depth < MaxDepth ? IndexOfUniqueIntersectingChild(info.aabb) : -1;
            if (childIndex >= 0)
            {
                ExpandIfNecessary();
                children[childIndex].Insert(info);
                return;
            }

            infoList ??= new List<MapResInfo>();
            infoList.Add(info);
        }

        public void Remove(AABB bounds, ulong key)
        {
            if (!aabb.Intersects(bounds)) return;

            var found = false;
            if (infoList != null)
            {
                for (var i = 0; i < infoList.Count; i++)
                {
                    if (infoList[i].key != key) continue;
                    infoList.RemoveAt(i);
                    found = true;
                    break;
                }
            }

            if (IsLeaf || found) return;
            foreach (var child in children)
                child.Remove(bounds, key);
        }

        public void Collect(AABB bounds, List<MapResInfo> results)
        {
            if (!aabb.Intersects(bounds))
                return;

            if (!IsLeaf)
                foreach (var child in children)
                    child.Collect(bounds, results);

            if (infoList == null) return;
            foreach (var info in infoList)
                if (bounds.Intersects(info.aabb))
                    results.Add(info);
        }

#if UNITY_EDITOR
        public void DrawGizmos()
        {
            if (infoList != null && infoList.Count > 0)
            {
                Gizmos.color = DepthColors[Math.Min(depth, DepthColors.Length - 1)];
                foreach (var info in infoList)
                {
                    var bounds = info.aabb;
                    Gizmos.DrawWireCube(bounds.center, bounds.size);
                }
            }

            if (children == null) return;
            foreach (var child in children)
                child.DrawGizmos();
        }
#endif

        private int IndexOfUniqueIntersectingChild(AABB bounds)
        {
            var index = -1;
            var count = 0;
            var childSize = aabb.size * 0.5f;
            for (var i = 0; i < CornerOffsets.Length; i++)
            {
                var center = aabb.center + new Vector2(
                    childSize.x * CornerOffsets[i].x * 0.5f,
                    childSize.y * CornerOffsets[i].y * 0.5f);
                if (new AABB(center, childSize).Intersects(bounds))
                {
                    count++;
                    index = i;
                }
            }

            return count == 1 ? index : -1;
        }

        private void ExpandIfNecessary()
        {
            if (children == null)
            {
                children = new MapResNode[4];
                var childSize = aabb.size * 0.5f;
                for (var i = 0; i < CornerOffsets.Length; i++)
                {
                    var center = aabb.center + new Vector2(
                        childSize.x * CornerOffsets[i].x * 0.5f,
                        childSize.y * CornerOffsets[i].y * 0.5f);
                    children[i] = new MapResNode { depth = depth + 1, aabb = new AABB(center, childSize) };
                }
            }
        }
    }

    private sealed class MapResInfo
    {
        public ulong key;

        public AABB aabb;
        public string assetpath;
        public Vector3 scale, position, eulerAngles;
        public Action<GameObject> onLoaded;

        public Transform parent;

        public GameObject go;

        public bool invalid;

        public Func<bool> visible;

        /// <summary>行为模板（来自烘焙数据，同一资源的多个实例共享）。</summary>
        public MapResBehavior[] behaviorTemplates;

        /// <summary>当前实例活跃的行为副本。</summary>
        public readonly List<MapResBehavior> activeBehaviors = new List<MapResBehavior>();
    }
}