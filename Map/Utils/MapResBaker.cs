#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Spine.Unity;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 地图资源烘焙器：把场景里选中的资源记录成 .bytes 数据供运行时动态加载，
/// 同时支持把 .bytes 反向还原成场景实例以便继续离线编辑。
/// 烘焙后的行为（MapResBehavior）在 MapResBakerEditor 里单独编辑。
/// </summary>
[ExecuteInEditMode]
public class MapResBaker : MonoBehaviour
{
    private const string CloneSuffix = "(Clone)";
    private const string DefaultPrefabFolder = "Assets/Art/Map/Res/Common/Prefab";
    private const string SpriteMaterialPath = "Assets/Art/Map/Materials/Sprites-ZWrite.mat";

    public int mapId;
    public TextAsset data;

    [SerializeField] private GameObject[] resources;
    [SerializeField] private GameObject[] zwriteObjects;

    [SerializeField] private MapReslist reslist;
    
    public void Modify()
    {
        EnsureReslist();

        var newEntries = new List<MapRes>();
        var resById = BuildNormalIndex();
        var resourceList = new List<GameObject>();
        for (var i = 0; i < resources.Length; i++)
        {
            var resource = resources[i];
            if (resource == null) continue;
            resourceList.Add(resource);
            var trans = resource.transform;
            var sprites = resource.GetComponentsInChildren<SpriteRenderer>();
            var skeletons = resource.GetComponentsInChildren<SkeletonAnimation>();
            var box = resource.GetComponent<BoxCollider>();

            var localBounds = CaptureLocalBounds(resource, box, skeletons, sprites);
            var prefabExists = !string.IsNullOrEmpty(
                PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(resource));
            var assetpath = ResolveAssetPath(resource);
            var flattened = new Bounds(localBounds.center,
                new Vector3(localBounds.size.x, localBounds.size.y));

            if (resById.TryGetValue(resource.GetInstanceID(), out var existing))
                ApplyTransform(existing, trans, flattened);
            else
                newEntries.Add(CreateMapSceneRes(resource, assetpath, trans, flattened));

            CreateFallbackPrefabIfNeeded(resource, assetpath, prefabExists, sprites);
        }

        resources = resourceList.ToArray();
        // 新增条目在前，旧的存活条目保持在后（沿用旧版的合并顺序）。
        var finalEntries = new List<MapRes>(newEntries);
        AppendAliveNormals(finalEntries);
        reslist.resources = finalEntries.ToArray();

        WriteBytes();
    }

    public void Deserialize()
    {
        if (data == null) return;
        if (reslist != null)
        {
            var length = reslist.resources?.Length ?? -1;
            for (int i = 0; i < length; i++)
            {
                var inst = reslist.resources[i].inst;
                if(inst) DestroyImmediate(inst);
                reslist.resources[i].inst = null;
            }
        }
        
        using var stream = new ReadWriteStream(data.bytes);
        reslist = MapReslist.ReadFrom(stream);

        var spawned = new List<GameObject>();
        SpawnEntries(spawned);
        resources = spawned.ToArray();
    }

    #region 烘焙

    private void EnsureReslist()
    {
        if (reslist == null)
            reslist = new MapReslist { resources = Array.Empty<MapRes>() };
        if (resources == null)
            resources = Array.Empty<GameObject>();
    }

    private Dictionary<int, MapRes> BuildNormalIndex()
    {
        var index = new Dictionary<int, MapRes>();
        if (reslist?.resources == null) return index;

        foreach (var res in reslist.resources)
        {
            if (res == null || res.inst == null) continue;
            index[res.inst.GetInstanceID()] = res;
        }

        return index;
    }

    private static Bounds CaptureLocalBounds(GameObject resource, BoxCollider box,
        SkeletonAnimation[] skeletons, SpriteRenderer[] sprites)
    {
        if (box != null)
            return new Bounds(box.center, box.size);

        var bounds = new Bounds();
        if (skeletons != null)
        {
            foreach (var skeleton in skeletons)
                bounds.Encapsulate(GetSpineBounds(
                    skeleton.gameObject, Vector3.zero, Vector3.zero, Vector3.one));
        }

        if (sprites != null && sprites.Length > 0)
            bounds.Encapsulate(GetSpriteBoundsIncludeChildren(
                resource, Vector3.zero, Vector3.zero, Vector3.one));

        return bounds;
    }

    private static string ResolveAssetPath(GameObject resource)
    {
        var existing = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(resource);
        if (!string.IsNullOrEmpty(existing))
            return existing;

        return $"{DefaultPrefabFolder}/{resource.name.toLower()}.prefab";
    }

    private static void ApplyTransform(MapRes res, Transform trans, Bounds bounds)
    {
        res.localScale = trans.localScale;
        res.localPosition = trans.localPosition;
        res.localEulerAngles = trans.localEulerAngles;
        res.localBounds = bounds;
    }

    private static MapRes CreateMapSceneRes(GameObject resource, string assetpath,
        Transform trans, Bounds bounds)
    {
        return new MapRes
        {
            assetpath = assetpath,
            localScale = trans.localScale,
            localPosition = trans.localPosition,
            localEulerAngles = trans.localEulerAngles,
            localBounds = bounds,
            inst = resource,
            behaviors = null
        };
    }

    private void CreateFallbackPrefabIfNeeded(GameObject resource, string assetpath,
        bool prefabExists, SpriteRenderer[] sprites)
    {
        if (prefabExists || sprites == null || sprites.Length == 0) return;
        if (resource.name.toLower().Contains("effect")) return;
        if (resource.GetComponentInChildren<ParticleSystem>()) return;
        // var go = new GameObject(resource.name.toLower());
        // go.hideFlags |= HideFlags.HideInHierarchy;
        // var renderer = go.AddComponent<SpriteRenderer>();
        // renderer.sprite = sprites[0].sprite;
        // if (WriteDepth(resource)) 
        //     renderer.material = AssetDatabase.LoadAssetAtPath<Material>(SpriteMaterialPath);
        var go = sprites[0].gameObject;
        var directory = Path.GetDirectoryName(assetpath);
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
        PrefabUtility.SaveAsPrefabAsset(go, assetpath);
    }

    private bool WriteDepth(GameObject resource)
    {
        if (zwriteObjects == null) return false;
        foreach (var o in zwriteObjects)
            if (o == resource)
                return true;

        return false;
    }

    private void AppendAliveNormals(List<MapRes> result)
    {
        if (reslist?.resources == null) return;
        foreach (var res in reslist.resources)
            if (res != null && res.inst != null && resources.Contains(res.inst))
                result.Add(res);
    }

    private void WriteBytes()
    {
        if (!Directory.Exists(MapDefine.DataDirectory))
            Directory.CreateDirectory(MapDefine.DataDirectory);

        var path = string.Format(MapDefine.ResDataFormat, mapId);
        using var stream = new ReadWriteStream();
        reslist.SaveTo(stream);
        File.WriteAllBytes(path, stream.ToArray());
        AssetDatabase.Refresh();
    }

    #endregion

    #region 反向还原

    private void SpawnEntries(List<GameObject> spawned)
    {
        if (reslist?.resources == null) return;
        foreach (var res in reslist.resources)
        {
            if (res == null) continue;
            var prefab = LoadPrefab(res.assetpath);
            if (prefab == null) continue;
            spawned.Add(SpawnInstance(prefab, res));
        }
    }

    private static GameObject LoadPrefab(string assetpath)
    {
        if (string.IsNullOrEmpty(assetpath))
        {
            Debug.LogWarning("[MapSceneResInfoBaker] 记录缺少 assetpath");
            return null;
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetpath);
        if (prefab == null)
            Debug.LogWarning($"[MapSceneResInfoBaker] 无法加载预制体: {assetpath}");

        return prefab;
    }

    private GameObject SpawnInstance(GameObject prefab, MapRes res)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        res.inst = go;

        var collider = go.AddComponent<BoxCollider>();
        collider.center = res.localBounds.center;
        collider.size = res.localBounds.size;

        var trans = go.transform;
        trans.SetParent(transform, false);
        trans.localPosition = res.localPosition;
        trans.localScale = res.localScale;
        trans.localEulerAngles = res.localEulerAngles;

        var index = go.name.IndexOf(CloneSuffix, StringComparison.Ordinal);
        if (index > 0) go.name = go.name.Substring(0, index);
        return go;
    }

    #endregion

    private static Bounds GetSpriteBoundsIncludeChildren(GameObject go, Vector3 t, Vector3 r, Vector3 s)
    {
        var bounds = new Bounds(Vector3.negativeInfinity, Vector3.negativeInfinity);
        var spriteRenderers = go.GetComponentsInChildren<SpriteRenderer>(true);
        foreach (var renderer in spriteRenderers)
        {
            var localBounds = renderer.localBounds;
            var position = t;
            var rotation = Quaternion.Euler(r);
            var scale = s;
            if (renderer.gameObject != go)
            {
                var current = renderer.gameObject.transform;
                while (current != null)
                {
                    position += current.localPosition;
                    rotation *= Quaternion.Euler(current.localEulerAngles);
                    var localScale = current.localScale;
                    scale = new Vector3(scale.x * localScale.x, scale.y * localScale.y, scale.z * localScale.z);
                    current = current.parent;
                }
            }

            var matrix = Matrix4x4.TRS(position, rotation, scale);
            var size = 2 * matrix.MultiplyVector(localBounds.extents);
            bounds.Encapsulate(new Bounds(
                matrix.MultiplyPoint(localBounds.center), size));
        }

        return bounds;
    }

    private static float[] vertexBuffer = new float[8];

    private static Bounds GetSpineBounds(GameObject go, Vector3 position, Vector3 eulerAngles, Vector3 scale)
    {
        var sa = go.GetComponent<SkeletonAnimation>();
        var bounds = new Bounds();
        if (sa != null)
        {
            sa.skeleton.GetBounds(out var x, out var y, out var w, out var h, ref vertexBuffer);
            var min = new Vector3(x, y);
            var size = new Vector3(w, h);
            bounds = new Bounds(min + size * 0.5f, size);

            var matrix = Matrix4x4.TRS(position, Quaternion.Euler(eulerAngles), scale);
            var center = matrix.MultiplyPoint(bounds.center);
            var extents = matrix.MultiplyVector(bounds.extents);
            return new Bounds(center, 2 * extents);
        }

        return bounds;
    }

    private static void DrawEntryGizmo(Matrix4x4 localToWorld, MapRes res)
    {
        var trs = localToWorld * Matrix4x4.TRS(
            res.localPosition, Quaternion.Euler(res.localEulerAngles), res.localScale);
        var center = trs.MultiplyPoint(res.localBounds.center);
        var extents = trs.MultiplyVector(res.localBounds.extents);
        Gizmos.DrawWireCube(center, extents * 2f);
    }

    private void OnDrawGizmos()
    {
        var localToWorld = transform.localToWorldMatrix;
        if (reslist?.resources == null) return;
        foreach (var res in reslist.resources)
            if (res != null)
                DrawEntryGizmo(localToWorld, res);
    }
}
#endif