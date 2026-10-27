using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 地图资源行为的抽象基类。
/// 具体行为由子类实现，以数组形式存在于 MapSceneRes.behaviors 中；
/// MapResManager 在资源实例创建 / 更新 / 销毁时驱动其生命周期。
/// </summary>
[Serializable]
public abstract class MapResBehavior
{
    /// <summary>编辑器中显示的名称，默认取类型名。</summary>
    public virtual string DisplayName => GetType().Name;

    /// <summary>资源实例创建后调用一次（此时位置 / 缩放 / 旋转已设置）。</summary>
    public abstract void OnCreate(GameObject target);

    /// <summary>实例存活期间每帧调用。</summary>
    public virtual void OnUpdate(GameObject target, float deltaTime)
    {
    }

    /// <summary>实例销毁前调用一次。</summary>
    public virtual void OnDestroy(GameObject target)
    {
    }

    /// <summary>
    /// 为每个新实例创建独立副本，子类需要复制自己的可变状态。
    /// 运行时通过无参构造创建，因此所有子类都必须提供无参构造函数。
    /// </summary>
    public abstract MapResBehavior Clone();

    /// <summary>二进制持久化的类型键，默认使用类型全名。</summary>
    public virtual string TypeKey => GetType().FullName;

    #region 类型注册 / 反序列化工厂

    private static readonly Dictionary<string, Type> ResolvedTypes = new Dictionary<string, Type>();

    /// <summary>根据持久化类型键创建行为实例；找不到类型时返回 null 并告警。</summary>
    public static MapResBehavior Create(string typeKey)
    {
        if (string.IsNullOrEmpty(typeKey)) return null;

        var type = ResolveType(typeKey);
        if (type == null)
        {
            Debug.LogWarning($"[MapResBehavior] 找不到行为类型: {typeKey}");
            return null;
        }

        return (MapResBehavior)Activator.CreateInstance(type);
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