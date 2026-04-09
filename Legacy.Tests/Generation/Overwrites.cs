using System;
using System.Linq.Expressions;

namespace Legacy.Tests.Generation;

public static class Overwrites
{
    public static void Replace<TDelegate>(Expression<TDelegate> target, Expression<TDelegate> replacement)
        where TDelegate : Delegate
    { }
}