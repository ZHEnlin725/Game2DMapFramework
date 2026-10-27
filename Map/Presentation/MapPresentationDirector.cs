using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>负责当前地图表现层的初始化、每帧驱动和释放。</summary>
public class MapPresentationDirector : MonoBehaviour
{
    private readonly List<MapPresentation> presentations = new();

    private int currentMapId = MapManager.InvalidMapId;
    private Transform presentationRoot;

    public bool HasPresentations => presentations.Count > 0;

    public void Init(int mapId, Transform root, MapPresentationDefinition definition)
    {
        Release();
        if (definition?.rules == null || definition.rules.Length == 0 || root == null) return;

        currentMapId = mapId;
        presentationRoot = root;

        foreach (var rule in definition.rules)
        {
            if (rule?.candidates == null || rule.candidates.Length == 0) continue;

            var presentation = new MapPresentation();
            presentation.Initialize(rule, new MapPresentationContext(mapId, root));
            presentation.Activate();
            presentations.Add(presentation);
        }
    }

    public void Tick(float deltaTime, in Bounds viewBounds)
    {
        if (currentMapId == MapManager.InvalidMapId) return;

        for (var i = presentations.Count - 1; i >= 0; i--)
            presentations[i]?.Tick(deltaTime, viewBounds);
    }

    public void Release()
    {
        for (var i = presentations.Count - 1; i >= 0; i--)
        {
            try
            {
                presentations[i]?.Release();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        presentations.Clear();
        MapSpeechPopupPresenter.ClearAll();
        currentMapId = MapManager.InvalidMapId;
        presentationRoot = null;
    }
}