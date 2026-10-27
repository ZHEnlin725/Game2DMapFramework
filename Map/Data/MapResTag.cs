using System;

/// <summary>场景资源的标签位标记，运行时可用于按类型筛选资源。</summary>
[Flags]
public enum MapResTag : int
{
    DEFAULT = 0,

    BIT_FLAG_0 = 1,
    BIT_FLAG_1 = 2,
    BIT_FLAG_2 = 4,
    BIT_FLAG_3 = 8,
    BIT_FLAG_4 = 16,
    BIT_FLAG_5 = 32,
    BIT_FLAG_6 = 64,
    BIT_FLAG_7 = 128,
    BIT_FLAG_8 = 256,
    BIT_FLAG_9 = 512,
}