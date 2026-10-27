using System;
using UnityEngine;

/// <summary>说话表现行为的数据字段。</summary>
[Serializable]
public partial class MapPresentationSpeech : MapPresentationBehavior
{
    [SerializableField] public float delay;
    [SerializableField] public string speechText;
    [SerializableField] public float speechDuration = 3f;

    /// <summary>重复间隔；0 表示只说一次。</summary>
    [SerializableField] public float speechRepeatInterval;
}