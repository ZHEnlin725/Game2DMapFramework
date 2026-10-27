using System;
using System.Collections.Generic;

/// <summary>
/// 地图固有事件监听注册器。
/// 公共事件对所有地图生效；地图专属事件按 mapId 分流，例如主城地图（mapId=0）的
/// 竞技场 / 合作副本建筑点击。所有注册记录由本类统一管理，换图或释放时精确退订，
/// 避免跨地图残留或重复注册。
/// </summary>
public sealed class MapEventRegistrar
{
    private readonly MapEventCenter center;

    private readonly List<Registration> commonRegistrations = new List<Registration>();
    private readonly Dictionary<int, List<Registration>> mapRegistrations =
        new Dictionary<int, List<Registration>>();

    public MapEventRegistrar(MapEventCenter center)
    {
        this.center = center ?? throw new ArgumentNullException(nameof(center));
    }

    /// <summary>注册公共监听和当前地图的专属监听；同一地图重复注册是幂等的。</summary>
    public void Register(int mapId)
    {
        if (mapId == MapManager.InvalidMapId) return;

        RegisterCommonOnce();
        RegisterMapOnce(mapId);
    }

    /// <summary>退订指定地图的专属监听，保留公共监听。</summary>
    public void UnregisterMap(int mapId)
    {
        if (!mapRegistrations.TryGetValue(mapId, out var registrations)) return;

        UnregisterAll(registrations);
        mapRegistrations.Remove(mapId);
    }

    /// <summary>退订本注册器管理过的全部监听，通常在 MapManager.Release 时调用。</summary>
    public void UnregisterAll()
    {
        UnregisterAll(commonRegistrations);

        foreach (var registrations in mapRegistrations.Values)
            UnregisterAll(registrations);

        mapRegistrations.Clear();
    }

    private void RegisterCommonOnce()
    {
        if (commonRegistrations.Count > 0) return;

        // 所有地图通用的固有监听集中在这里注册。
        AddRegistration(commonRegistrations, MapEventTypes.Clicked, OnAnyResourceClicked);
    }

    private void RegisterMapOnce(int mapId)
    {
        if (mapRegistrations.ContainsKey(mapId)) return;

        var registrations = new List<Registration>();
        switch (mapId)
        {
            case 0:
                // 主城地图：竞技场 / 合作副本等建筑点击事件。
                AddRegistration(registrations, MapEventTypes.Clicked, OnMap0BuildingClicked);
                break;
        }

        mapRegistrations[mapId] = registrations;
    }

    private void AddRegistration(
        List<Registration> registrations,
        string eventType,
        Action<MapEvent> listener)
    {
        registrations.Add(new Registration(eventType, listener));
        center.AddListener(eventType, listener);
    }

    private void UnregisterAll(List<Registration> registrations)
    {
        for (var i = 0; i < registrations.Count; i++)
            center.RemoveListener(registrations[i].EventType, registrations[i].Listener);

        registrations.Clear();
    }

    private static void OnAnyResourceClicked(MapEvent evt)
    {
        if (!TryGetEventKey(evt, out var key)) return;
        OnResourceClicked(evt, key);
    }

    private static void OnMap0BuildingClicked(MapEvent evt)
    {
        if (!TryGetEventKey(evt, out var key)) return;

        switch (key)
        {
            case MapBuilding.ARENA:
                OnArenaBuildingClicked(evt);
                break;
            case MapBuilding.COOPERATION:
                OnCooperationBuildingClicked(evt);
                break;
        }
    }

    private static bool TryGetEventKey(MapEvent evt, out string key)
    {
        key = evt.UserData as string;
        return !string.IsNullOrEmpty(key);
    }

    private static void OnResourceClicked(MapEvent evt, string key)
    {
        // 所有地图通用的点击业务在这里接入，例如飘字、音效或通用弹窗。
    }

    private static void OnArenaBuildingClicked(MapEvent evt)
    {
        // 竞技场入口在这里接入。
    }

    private static void OnCooperationBuildingClicked(MapEvent evt)
    {
        // 合作副本入口在这里接入。
    }

    private readonly struct Registration
    {
        public readonly string EventType;
        public readonly Action<MapEvent> Listener;

        public Registration(string eventType, Action<MapEvent> listener)
        {
            EventType = eventType;
            Listener = listener;
        }
    }
}