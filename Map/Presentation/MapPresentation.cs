using System;
using System.Collections.Generic;
using UnityEngine;
using static UnityEngine.Object;

/// <summary>表现物生成前由运行时传入的上下文。</summary>
public readonly struct MapPresentationContext
{
    public MapPresentationContext(int mapId, Transform root)
    {
        MapId = mapId;
        Root = root;
    }

    public int MapId { get; }

    public Transform Root { get; }
}

/// <summary>一个规则的具体出生决策。</summary>
public readonly struct MapPresentationPlacement
{
    public MapPresentationPlacement(Vector3 position, MapPresentationCandidate candidate)
    {
        Position = position;
        Candidate = candidate;
    }

    public Vector3 Position { get; }

    public MapPresentationCandidate Candidate { get; }
}

/// <summary>把规则转换成确定或随机的出生决策。</summary>
public static class MapPresentationPlanner
{
    public static List<MapPresentationPlacement> Generate(MapPresentationRule rule, int mapId)
    {
        var result = new List<MapPresentationPlacement>();
        if (rule?.candidates == null || rule.candidates.Length == 0) return result;

        var anchorPools = new List<CandidateAnchorPool>();
        var uniqueAnchorLimit = 0;
        var hasReusableAnchorCandidate = false;
        for (var i = 0; i < rule.candidates.Length; i++)
        {
            var candidate = rule.candidates[i];
            if (candidate == null || string.IsNullOrEmpty(candidate.assetpath)) continue;

            var points = ResolveCandidateAnchorPoints(candidate);
            if (points.Length == 0) continue;

            anchorPools.Add(new CandidateAnchorPool(candidate, points));
            if (candidate.uniqueAnchors) uniqueAnchorLimit += points.Length;
            else hasReusableAnchorCandidate = true;
        }

        var anchorLimit = hasReusableAnchorCandidate ? int.MaxValue : uniqueAnchorLimit;
        var minLimit = Mathf.Min(rule.minCount, anchorLimit);
        var count = Mathf.Clamp(rule.maxCount, minLimit, anchorLimit);
        if (count <= 0) return result;

        var random = new System.Random(ResolveSeed(rule, mapId));
        for (var i = 0; i < anchorPools.Count; i++)
            Shuffle(anchorPools[i].Points, random);

        for (var i = 0; i < count; i++)
        {
            var pool = PickAnchorPool(anchorPools, random);
            if (pool == null) break;

            var anchorIndex = pool.Candidate.uniqueAnchors
                ? pool.TakeNext()
                : random.Next(pool.Points.Count);
            result.Add(new MapPresentationPlacement(pool.Points[anchorIndex], pool.Candidate));

            if (pool.Candidate.uniqueAnchors && !pool.HasNext())
                anchorPools.Remove(pool);
        }

        return result;
    }

    private static MapPresentationCandidate PickCandidate(MapPresentationCandidate[] candidates, System.Random random)
    {
        var totalWeight = 0f;
        foreach (var candidate in candidates)
        {
            if (candidate == null || string.IsNullOrEmpty(candidate.assetpath)) continue;
            totalWeight += Mathf.Max(0f, candidate.weight);
        }

        if (totalWeight <= Mathf.Epsilon) return null;

        var cursor = (float)random.NextDouble() * totalWeight;
        foreach (var candidate in candidates)
        {
            if (candidate == null || string.IsNullOrEmpty(candidate.assetpath)) continue;
            cursor -= Mathf.Max(0f, candidate.weight);
            if (cursor <= 0f) return candidate;
        }

        return null;
    }

    private static CandidateAnchorPool PickAnchorPool(List<CandidateAnchorPool> pools, System.Random random)
    {
        var totalWeight = 0f;
        for (var i = 0; i < pools.Count; i++)
            totalWeight += Mathf.Max(0f, pools[i].Candidate.weight);

        if (totalWeight <= Mathf.Epsilon) return null;

        var cursor = (float)random.NextDouble() * totalWeight;
        for (var i = 0; i < pools.Count; i++)
        {
            cursor -= Mathf.Max(0f, pools[i].Candidate.weight);
            if (cursor <= 0f) return pools[i];
        }

        return pools[pools.Count - 1];
    }

    private static Vector3[] ResolveCandidateAnchorPoints(MapPresentationCandidate candidate)
    {
        if (candidate.anchorPoints is { Length: > 0 })
            return candidate.anchorPoints;

        var behaviors = candidate.behaviors;
        if (behaviors == null) return Array.Empty<Vector3>();

        for (var i = 0; i < behaviors.Length; i++)
        {
            if (behaviors[i] is not MapPresentationRoute route) continue;
            if (route.waypoints is { Length: > 0 })
                return new[] { route.waypoints[0] };
        }

        return Array.Empty<Vector3>();
    }

    private sealed class CandidateAnchorPool
    {
        public CandidateAnchorPool(MapPresentationCandidate candidate, Vector3[] points)
        {
            Candidate = candidate;
            Points = new List<Vector3>(points);
        }

        public MapPresentationCandidate Candidate { get; }
        public List<Vector3> Points { get; }

        public bool HasNext()
        {
            return Points.Count > 0;
        }

        public int TakeNext()
        {
            var index = nextIndex;
            nextIndex = Mathf.Min(nextIndex + 1, Points.Count - 1);
            return index;
        }

        private int nextIndex;
    }

    private static int ResolveSeed(MapPresentationRule rule, int mapId)
    {
        if (rule.seed == 0) return Environment.TickCount ^ mapId;
        return HashCode.Combine(mapId, rule.ruleId, rule.seed);
    }

    private static void Shuffle(List<Vector3> values, System.Random random)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
}

/// <summary>统一展示一条规则；候选人行为和生命周期由本类直接驱动。</summary>
public sealed class MapPresentation
{
    private sealed class RuntimeInstance
    {
        public GameObject Target;
        public MapPresentationCandidate Candidate;
        public readonly List<MapPresentationBehavior> Behaviors = new();
        public float ElapsedTime;

        public bool IsExpired
        {
            get
            {
                if (Candidate.lifetime > 0f && ElapsedTime >= Candidate.lifetime) return true;
                var count = Behaviors.Count;
                if (!Candidate.destroyWhenBehaviorsFinished || count == 0) return false;

                for (var i = 0; i < count; i++)
                {
                    var behavior = Behaviors[i];
                    var ignore = false;
                    //如果有多个行为忽略播放循环动画是否完成
                    if (behavior is MapPresentationAnimation { loop: true }) 
                        ignore = count > 1;
                    if (!ignore && behavior != null && !behavior.IsFinished())
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }

    private readonly List<RuntimeInstance> instances = new();
    private int activationsRemaining;
    private bool waitingForRespawn;
    private float respawnTimer;

    public MapPresentationRule Rule { get; private set; }
    public MapPresentationContext Context { get; private set; }

    public void Initialize(MapPresentationRule rule, in MapPresentationContext context)
    {
        Rule = rule;
        Context = context;
        waitingForRespawn = false;
        respawnTimer = 0f;
        activationsRemaining = rule == null ? 0 :
            rule.repeatCount <= 0 ? int.MaxValue : rule.repeatCount;
    }

    public void Activate()
    {
        if (activationsRemaining != int.MaxValue && activationsRemaining > 0)
            activationsRemaining--;

        var placements = MapPresentationPlanner.Generate(Rule, Context.MapId);
        for (var i = 0; i < placements.Count; i++)
        {
            var placement = placements[i];
            var prefab = YooAssetManager.LoadAssetSync<GameObject>(placement.Candidate.assetpath);
            if (prefab == null)
            {
                Debug.LogWarning($"[MapPresentation] Prefab missing: {placement.Candidate.assetpath}");
                continue;
            }

            var target = Instantiate(prefab, Context.Root);
            target.name = $"presentation[{Rule.ruleId}_{i:D2}]";
            target.layer = MapDefine.LayerIndex;
            var trans = target.transform;
            trans.localPosition = placement.Position;
            trans.localScale = placement.Candidate.localScale;
            trans.localEulerAngles = placement.Candidate.localEulerAngles;

            var runtime = new RuntimeInstance
            {
                Target = target,
                Candidate = placement.Candidate,
            };
            AddBehaviors(runtime.Behaviors, Rule.behaviors);
            AddBehaviors(runtime.Behaviors, placement.Candidate.behaviors);

            for (var j = 0; j < runtime.Behaviors.Count; j++)
                runtime.Behaviors[j]?.Initialize(target);
            instances.Add(runtime);
        }
    }

    public void Tick(float deltaTime, in Bounds viewBounds)
    {
        for (var i = instances.Count - 1; i >= 0; i--)
        {
            var runtime = instances[i];
            if (runtime.Target == null)
            {
                instances.RemoveAt(i);
                continue;
            }

            // runtime.Target.SetActiveSafe(IsVisible(viewBounds, runtime.Target));
            // if (!runtime.Target.activeInHierarchy) continue;

            runtime.ElapsedTime += deltaTime;
            for (var j = 0; j < runtime.Behaviors.Count; j++)
                runtime.Behaviors[j]?.Update(runtime.Target, deltaTime);

            if (!runtime.IsExpired) continue;

            Destroy(runtime.Target);
            instances.RemoveAt(i);
        }

        ScheduleNextActivation(deltaTime);
    }

    public void Release()
    {
        for (var i = 0; i < instances.Count; i++)
            if (instances[i].Target != null)
                Destroy(instances[i].Target);
        instances.Clear();
    }

    private void ScheduleNextActivation(float deltaTime)
    {
        if (activationsRemaining <= 0 || Rule == null) return;

        if (!waitingForRespawn)
        {
            waitingForRespawn = true;
            respawnTimer = PickRespawnInterval();
            return;
        }

        respawnTimer -= deltaTime;
        if (respawnTimer > 0f) return;

        waitingForRespawn = false;
        Activate();
    }

    private float PickRespawnInterval()
    {
        var min = Mathf.Max(0f, Rule.repeatIntervalMin);
        var max = Mathf.Max(min, Rule.repeatIntervalMax);
        return UnityEngine.Random.Range(min, max);
    }

    private static void AddBehaviors(List<MapPresentationBehavior> target, MapPresentationBehavior[] source)
    {
        if (source == null) return;

        for (var i = 0; i < source.Length; i++)
        {
            var behavior = source[i]?.Clone();
            if (behavior != null) target.Add(behavior);
        }
    }

    private static bool IsVisible(in Bounds viewBounds, GameObject target)
    {
        var renderers = target.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return true;

        for (var i = 0; i < renderers.Length; i++)
        {
            var renderer = renderers[i];
            if (renderer != null && renderer.enabled && viewBounds.Intersects(renderer.bounds))
                return true;
        }

        return false;
    }
}