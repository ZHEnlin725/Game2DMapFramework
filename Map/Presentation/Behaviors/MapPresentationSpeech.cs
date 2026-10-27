using UnityEngine;

/// <summary>说话表现行为的运行时逻辑：通过 MapSpeechPopupPresenter 通知 UI 层弹出文本。</summary>
public partial class MapPresentationSpeech : MapPresentationBehavior
{
    private float remainingDelay;
    private float repeatTimer;
    private bool finished;

    public override void Initialize(GameObject target)
    {
        remainingDelay = Mathf.Max(0f, delay);
        repeatTimer = 0f;
        finished = false;
    }

    public override bool Update(GameObject target, float deltaTime)
    {
        if (finished) return true;

        if (repeatTimer > 0f)
        {
            repeatTimer -= deltaTime;
            return false;
        }

        if (remainingDelay > 0f)
        {
            remainingDelay -= deltaTime;
            if (remainingDelay > 0f) return false;
        }

        MapSpeechPopupPresenter.Show(speechText, Mathf.Max(0.1f, speechDuration));
        if (speechRepeatInterval > 0f)
        {
            repeatTimer = speechRepeatInterval;
        }
        else
        {
            finished = true;
        }

        return finished;
    }

    public override bool IsFinished() => finished;

    public override MapPresentationBehavior Clone()
    {
        return new MapPresentationSpeech
        {
            delay = delay,
            speechText = speechText,
            speechDuration = speechDuration,
            speechRepeatInterval = speechRepeatInterval,
        };
    }
}