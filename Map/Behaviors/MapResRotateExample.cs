using System;
using UnityEngine;

/// <summary>
/// 示例行为：实例存活期间绕自身 Y 轴旋转。
/// 展示了生命周期、Clone 与二进制序列化的完整写法，可作为其他子类的模板。
/// </summary>
// [Serializable]
// public class MapResRotateExample : MapResBehavior
// {
//     public float speed = 30f;
//
//     public override void OnCreate(GameObject target)
//     {
//     }
//
//     public override void OnUpdate(GameObject target, float deltaTime)
//     {
//         target.transform.Rotate(0f, speed * deltaTime, 0f, Space.Self);
//     }
//
//     public override void OnDestroy(GameObject target)
//     {
//     }
//
//     public override MapResBehavior Clone()
//     {
//         return new MapResRotateExample { speed = speed };
//     }
//
//     public override void Serialize(ReadWriteStream stream) => stream.WriteSingle(speed);
//
//     public override void Deserialize(ReadWriteStream stream) => speed = stream.ReadSingle();
// }