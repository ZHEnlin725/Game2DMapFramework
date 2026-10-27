public partial class MapResClickable
{
    /// <summary>响应点击时是否播放缩放效果（按下缩小后回弹）。</summary>
    [SerializableField] public bool enableScaleEffect = true;

    /// <summary>按下瞬间相对原始缩放的比例，例如 0.85 表示缩小到 85%。</summary>
    [SerializableField] public float pressedScale = 0.95f;

    /// <summary>按下缩小的时长（秒）。</summary>
    [SerializableField] public float pressDuration = 0.05f;

    /// <summary>回弹到原始缩放的时长（秒）。</summary>
    [SerializableField] public float releaseDuration = 0.1f;

    /// <summary>响应点击时是否向地图事件中心广播 ResClicked 事件。</summary>
    [SerializableField] public bool broadcastEvent;

    /// <summary>响应点击时向地图事件中心广播的事件Key用于逻辑判断。</summary>
    [SerializableField] public string eventKey;
}