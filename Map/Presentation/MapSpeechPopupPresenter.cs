using UnityEngine;

/// <summary>说话表现的解耦入口；具体 FGUI 创建逻辑预留，由后续手工实现。</summary>
public static class MapSpeechPopupPresenter
{
    public static void Show(string text, float duration)
    {
        if (string.IsNullOrWhiteSpace(text) || duration <= 0f) return;
        ShowSpeech(text, duration);
    }

    public static void ClearAll()
    {
        ClearSpeech();
    }

    /// <summary>在这里接入 FairyGUI，根据 text 和 duration 创建/刷新文本框。</summary>
    private static void ShowSpeech(string text, float duration)
    {
    }

    /// <summary>在这里回收 FairyGUI 文本框；切换地图或 Director 释放时调用。</summary>
    private static void ClearSpeech()
    {
    }
}