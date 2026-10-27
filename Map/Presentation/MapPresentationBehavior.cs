using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>地图表现行为的抽象基类；路线、动画、说话等都是它的具体实现。</summary>
[Serializable]
public abstract class MapPresentationBehavior
{
    /// <summary>二进制持久化时的类型键，默认使用类型全名。</summary>
    public virtual string TypeKey => GetType().FullName;

    /// <summary>绑定到目标 GameObject 时调用一次。</summary>
    public virtual void Initialize(GameObject target)
    {
    }

    /// <summary>每帧驱动；返回 true 表示该行为已经结束。</summary>
    public virtual bool Update(GameObject target, float deltaTime)
    {
        return IsFinished();
    }

    /// <summary>当前行为是否已经结束，供 Candidate 操作类判断是否销毁实例。</summary>
    public abstract bool IsFinished();

    /// <summary>为每个实例创建独立副本。</summary>
    public abstract MapPresentationBehavior Clone();

    #region 类型注册 / 反序列化工厂

    private static readonly Dictionary<string, Type> ResolvedTypes = new Dictionary<string, Type>();

    /// <summary>根据持久化类型键创建行为实例；找不到类型时返回 null 并告警。</summary>
    public static MapPresentationBehavior Create(string typeKey)
    {
        if (string.IsNullOrEmpty(typeKey)) return null;

        var type = ResolveType(typeKey);
        if (type == null)
        {
            Debug.LogWarning($"[MapPresentationBehavior] 找不到行为类型: {typeKey}");
            return null;
        }

        return (MapPresentationBehavior)Activator.CreateInstance(type);
    }

    private static Type ResolveType(string typeKey)
    {
        if (ResolvedTypes.TryGetValue(typeKey, out var cached)) return cached;

        Type type = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(typeKey, false);
            if (type != null) break;
        }

        ResolvedTypes[typeKey] = type;
        return type;
    }

    #endregion
}