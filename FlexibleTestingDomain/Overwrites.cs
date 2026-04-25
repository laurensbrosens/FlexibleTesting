using System.Linq.Expressions;

namespace FlexibleTestingDomain;

public class Overwrites
{
    public static void ReplaceProperty<TDelegate>(Expression<TDelegate> target, Expression<TDelegate> replacement)
        where TDelegate : Delegate { }

    public static void Replace<TDelegate>(TDelegate target, TDelegate replacement)
        where TDelegate : Delegate { }

    public static void Mock<TDelegate>(TDelegate value)
        where TDelegate : Delegate { }

    public static void MockSignature<TDelegate>(TDelegate value)
        where TDelegate : Delegate { }

    public static void MakePublic<TInterface, TDelegate>(Expression<Func<TInterface, TDelegate>> methodSelector)
        where TDelegate : Delegate { }

    public static void MakePublic<TDelegate>(TDelegate value)
        where TDelegate : Delegate { }

    public static void Include<T>()
        where T : IGeneratorInstructions { }

    public static void RedirectNew<TTarget, TDelegate>(Func<TTarget> value1, Func<TDelegate> value2)
        where TDelegate : Delegate { }

    public static void Mock<TClass>() { }

    public static void MockWithInterface<TClass, TInterface>() { }

    public static void ForClass<TClass>() { }

    public static void ForClass(Type type) { }

    public static void MockInheritance() { }

    public static void RecursiveMockInheritance() { }

    public static void InheritFrom<TClass>() { }

    public static void RemoveSealed() { }
}
