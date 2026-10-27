using System;

/// <summary>
/// Marks an instance field so SerializableClassGenerator can emit a partial
/// class implementing ISerializable with Serialize/Deserialize methods.
/// The declaring type must be declared with the partial keyword.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public class SerializableFieldAttribute : Attribute
{
}
