using UnityEngine;
using Spine.Unity;

/// <summary>动画表现行为的运行时逻辑：优先 Spine，其次 Animator。</summary>
public partial class MapPresentationAnimation : MapPresentationBehavior
{
    private float remainingDelay;
    private float remainingDuration;
    private bool started;
    private bool finished;
    private SkeletonAnimation skeletonAnimation;
    private Animator animator;

    public override void Initialize(GameObject target)
    {
        remainingDelay = Mathf.Max(0f, delay);
        remainingDuration = Mathf.Max(0f, duration);
        started = false;
        finished = false;

        skeletonAnimation = target != null ? target.GetComponentInChildren<SkeletonAnimation>() : null;
        animator = skeletonAnimation == null && target != null ? target.GetComponentInChildren<Animator>() : null;
    }

    public override bool Update(GameObject target, float deltaTime)
    {
        if (finished) return true;

        if (!started)
        {
            remainingDelay -= deltaTime;
            if (remainingDelay > 0f) return false;

            Play();
            started = true;
            if (remainingDuration <= 0f && !loop)
            {
                finished = true;
                return true;
            }
        }

        if (remainingDuration > 0f)
        {
            remainingDuration -= deltaTime;
            if (remainingDuration <= 0f) finished = true;
        }

        return finished;
    }

    public override bool IsFinished() => finished;

    public override MapPresentationBehavior Clone()
    {
        return new MapPresentationAnimation
        {
            delay = delay,
            animationName = animationName,
            loop = loop,
            duration = duration,
        };
    }

    private void Play()
    {
        if (skeletonAnimation != null)
        {
            skeletonAnimation.AnimationState.SetAnimation(0, animationName, loop);
            return;
        }

        animator?.CrossFade(animationName, 0.1f);
    }
}