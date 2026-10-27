using System;
using FairyGUI;
using UnityEngine;

/// <summary>
/// 点击行为：OnCreate 时注册 InputGesture.sharedInst.OnClickScreen，触发点击时
/// 通过 hitInfo 判定点击是否落在自己身上；enableScaleEffect 控制是否播放按钮式
/// 缩放反馈（GTween 实现），broadcastEvent 控制是否向 MapManager 的地图事件中心
/// 广播点击事件（主要用于通知 UI）。
/// </summary>
[Serializable]
public partial class MapResClickable : MapResBehavior
{
    private GTweener scaleTween;
    private GameObject boundTarget;
    private Vector3 originalScale = Vector3.one;

    public override void OnCreate(GameObject target)
    {
        boundTarget = target;
        originalScale = target != null ? target.transform.localScale : Vector3.one;
        InputGesture.sharedInst.OnClickScreen += OnScreenClick;
        var collider = boundTarget.GetComponent<BoxCollider>();
        if (collider == null)
            collider = boundTarget.AddComponent<BoxCollider>();
    }

    public override void OnDestroy(GameObject target)
    {
        InputGesture.sharedInst.OnClickScreen -= OnScreenClick;
        KillScaleTween();
        if (target != null) target.transform.localScale = originalScale;
        boundTarget = null;
    }

    public override MapResBehavior Clone() => new MapResClickable
    {
        enableScaleEffect = enableScaleEffect,
        pressedScale = pressedScale,
        pressDuration = pressDuration,
        releaseDuration = releaseDuration,
        broadcastEvent = broadcastEvent,
        eventKey = eventKey,
    };

    private void OnScreenClick(RaycastHit hitInfo)
    {
        if (!IsSelfHit(hitInfo)) return;

        if (broadcastEvent)
        {
            Debug.LogError($"Click:{eventKey}");
            if (!string.IsNullOrEmpty(eventKey))
            {
                MapManager.Singleton.BroadcastMapEvent(new MapEvent(
                    MapEventTypes.Clicked, boundTarget, eventKey));
            }
        }

        if (enableScaleEffect) PlayScaleEffect();
    }

    /// <summary>点击射线命中的碰撞体属于自己（含子节点）时才算被点击。</summary>
    private bool IsSelfHit(RaycastHit hitInfo) =>
        hitInfo.collider != null && boundTarget != null &&
        hitInfo.collider.transform.IsChildOf(boundTarget.transform);

    private void PlayScaleEffect()
    {
        var target = boundTarget;
        if (target == null) return;

        KillScaleTween();

        var trans = target.transform;
        var pressedVec = originalScale * pressedScale;

        // 按钮式反馈：先缩小到 pressedScale，再回弹到原始缩放。
        scaleTween = GTween.To(trans.localScale, pressedVec, Mathf.Max(0.01f, pressDuration))
            .SetEase(EaseType.QuadOut)
            .SetTarget(this)
            .OnUpdate(ApplyTweenScale)
            .OnComplete(() =>
            {
                // 实例可能在动画期间被回收，回弹前重新校验。
                if (target == null)
                {
                    scaleTween = null;
                    return;
                }

                scaleTween = GTween.To(
                        target.transform.localScale,
                        originalScale,
                        Mathf.Max(0.01f, releaseDuration))
                    .SetEase(EaseType.BackOut)
                    .SetTarget(this)
                    .OnUpdate(ApplyTweenScale)
                    .OnComplete(() => scaleTween = null);
            });
    }

    private void ApplyTweenScale(GTweener tweener)
    {
        if (boundTarget == null) return;
        boundTarget.transform.localScale = tweener.value.vec3;
    }

    private void KillScaleTween()
    {
        if (scaleTween == null) return;
        scaleTween.Kill(false);
        scaleTween = null;
    }
}