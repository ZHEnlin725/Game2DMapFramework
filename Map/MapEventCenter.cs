using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>地图事件的统一负载；Sender 通常是被交互的资源实例。</summary>
public readonly struct MapEvent
{
    public readonly string Type;
    public readonly GameObject Sender;
    public readonly object UserData;

    public MapEvent(string type, GameObject sender = null, object userData = null)
    {
        Type = type;
        Sender = sender;
        UserData = userData;
    }

    public override string ToString() =>
        $"[{Type}] sender:{(Sender == null ? "null" : Sender.name)}, userData:{UserData}";
}

/// <summary>地图模块内置事件类型。</summary>
public static class MapEventTypes
{
    /// <summary>地图资源被点击（MapResClickBehavior 触发）。</summary>
    public const string Clicked = "CLICKED";
}

/// <summary>
/// 地图事件中心：按字符串类型注册监听并广播事件，由 MapManager 持有，
/// 地图行为通过它广播事件，UI 等外部系统通过 MapManager.Singleton.EventCenter 订阅。
/// 监听关系跨地图存活，订阅方需自行调用 RemoveListener 退订；单次派发先快照再执行，
/// 因此监听者可以在回调里安全地增删监听。
/// </summary>
public sealed class MapEventCenter
{
    private readonly Dictionary<string, List<Action<MapEvent>>> listeners =
        new Dictionary<string, List<Action<MapEvent>>>();

    private readonly List<Action<MapEvent>> dispatchBuffer = new List<Action<MapEvent>>();

    public void AddListener(string type, Action<MapEvent> listener)
    {
        if (string.IsNullOrEmpty(type) || listener == null) return;

        if (!listeners.TryGetValue(type, out var list))
        {
            list = new List<Action<MapEvent>>();
            listeners[type] = list;
        }

        if (!list.Contains(listener)) list.Add(listener);
    }

    public bool RemoveListener(string type, Action<MapEvent> listener)
    {
        if (string.IsNullOrEmpty(type) || listener == null) return false;
        if (!listeners.TryGetValue(type, out var list)) return false;

        var removed = list.Remove(listener);
        if (list.Count == 0) listeners.Remove(type);
        return removed;
    }

    /// <summary>按类型广播事件；单个监听回调异常不影响其余监听者。</summary>
    public void Broadcast(in MapEvent evt)
    {
        if (string.IsNullOrEmpty(evt.Type) ||
            !listeners.TryGetValue(evt.Type, out var list) ||
            list.Count == 0) return;

        dispatchBuffer.Clear();
        dispatchBuffer.AddRange(list);

        for (var i = 0; i < dispatchBuffer.Count; i++)
        {
            try
            {
                dispatchBuffer[i]?.Invoke(evt);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        dispatchBuffer.Clear();
    }

    /// <summary>清空全部监听；通常仅在彻底销毁地图系统时使用。</summary>
    public void Clear() => listeners.Clear();
}