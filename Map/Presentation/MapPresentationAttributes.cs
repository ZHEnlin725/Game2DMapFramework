using System;
using System.Collections.Generic;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public class MultiplePresentationBehaviorAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public class MutexPresentationBehaviorAttribute : Attribute
{
    public List<Type> mutexTypes;

    public MutexPresentationBehaviorAttribute(params Type[] types) =>
        mutexTypes = new List<Type>(types);
}