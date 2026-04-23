# FlexibleTesting
 
FlexibleTesting is a compile-time mocking framework for C#. It allows you to unit test legacy code, including WPF ViewModels, without heavy refactoring. It generates a "testable copy" with a `_G` suffix of your class and applies surgical rewrites to bypass side effects like static calls, database requests, and protected encapsulation.

## Getting Started

To test a legacy class, create a builder class marked with `[GeneratorInstructions]`. This class instructs the MSBuild task on how to rewrite the target.

```csharp
[GeneratorInstructions]
internal class UserViewModelBaseBuilder(SomeDataObject someDataObject) : UserViewModelBase(someDataObject), IGeneratorInstructions
{
    public void Configure()
    {
        Overwrites.ForClass<UserViewModelBase>();
        Overwrites.Mock(() => DateTime.Now);
        Overwrites.Mock<UserService>();
        Overwrites.RecursiveMockInheritance();
    }
}
```

```csharp
[GeneratorInstructions]
internal class UserViewModelBuilder(SomeDataObject someDataObject) : UserViewModel(someDataObject), IGeneratorInstructions
{
    public void Configure()
    {
        Overwrites.ForClass<UserViewModel>();
        Overwrites.Mock(() => Guid.NewGuid());
        Overwrites.RecursiveMockInheritance();
    }
}
```

## Usage Examples

### Mocking Static Calls
Redirect problematic static calls, like `DateTime.Now` or `Guid.NewGuid`, to a mockable dependency.

```csharp
// In Builder:
Overwrites.Mock(() => DateTime.Now);

// In Test:
baseDeps.Now.Returns(() => new DateTime(2026, 4, 14));
```

### Publicizing Private Members
Test private or protected methods without using reflection.

```csharp
Overwrites.MakePublic<IShadow, Action>(x => x.InternalMethod);
// Rewrites the method to public in the generated UserViewModel_G
```

### Bypassing Heavy Inheritance
Replace a base class that has side effects in its constructor with an auto-generated copy.

```csharp
Overwrites.MockInheritance();
// class UserViewModel_G : BaseViewModel_G
```

### Mocking Internal Services
Automatically generate an interface for a concrete legacy service and redirect all usages.

```csharp
Overwrites.Mock<UserService>();
// Original:
_userService = new UserService();

// Copy:
_userService = _dependencies.UserService();
```

### Stacking Instructions
Reuse common overwrite rules across multiple builders.

```csharp
Overwrites.Include<BaseBuilder>();
```

### Recursive Instructions
Create complex inheritance hierarchies using one builder file for each class in the chain. Each generated class becomes a full copy of its source, and the derived generated class inherits from the generated base copy.

```csharp
Overwrites.RecursiveMockInheritance();
// UserViewModelBaseBuilder -> UserViewModelBase_G.g.cs
// UserViewModelBuilder -> UserViewModel_G.g.cs
// UserViewModel_G : UserViewModelBase_G
```

## Testing the Generated Class
The generator produces a class named `{ClassName}_G` and a matching interface `IAuto{ClassName}Dependencies`. For recursive inheritance, the base class and the derived class each get their own generated file and their own dependency interface.

```csharp
[Test]
public void Constructor_Sets_Defaults_From_Both_Dependency_Levels()
{
    var data = new SomeDataObject();
    var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
    var deps = Substitute.For<IAutoUserViewModelDependencies>();

    baseDeps.Now.Returns(() => new DateTime(2026, 4, 14));
    deps.NewGuid().Returns(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    var vm = new UserViewModel_G(data, deps, baseDeps);

    Assert.That(vm.Name, Is.EqualTo("Base"));
    Assert.That(vm.Token, Is.EqualTo("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
}
```

## Full Example

```csharp
// Legacy source
public class UserViewModelBase
{
    public UserViewModelBase(SomeDataObject someDataObject)
    {
        SomeDataObject = someDataObject;
        Name = "Base";
        CreatedAt = DateTime.Now;
        _userService = new UserService();
    }

    public SomeDataObject SomeDataObject { get; }
    public string Name { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Summary => $"{Name} ({_userService.GetUserName(Name)})";

    private UserService _userService;
}

public class UserViewModel : UserViewModelBase
{
    public UserViewModel(SomeDataObject someDataObject) : base(someDataObject)
    {
        Token = Guid.NewGuid().ToString();
    }

    public string Token { get; set; }
}
```

```csharp
// Generated base copy
public class UserViewModelBase_G(SomeDataObject someDataObject, IAutoUserViewModelBaseDependencies dependencies)
{
    private readonly IAutoUserViewModelBaseDependencies _dependencies = dependencies;

    public SomeDataObject SomeDataObject { get; }
    public string Name { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Summary => $"{Name} ({_dependencies.UserService().GetUserName(Name)})";
}

/// <summary>Mock this using NSubstitute</summary>
public interface IAutoUserViewModelBaseDependencies
{
    Func<DateTime> Now { get; }
    IAutoUserService UserService();
}
```

```csharp
// Generated derived copy
public class UserViewModel_G(SomeDataObject someDataObject, IAutoUserViewModelDependencies dependencies, IAutoUserViewModelBaseDependencies baseDependencies)
    : UserViewModelBase_G(someDataObject, baseDependencies)
{
    private readonly IAutoUserViewModelDependencies _dependencies = dependencies;

    public string Token { get; set; }
}

/// <summary>Mock this using NSubstitute</summary>
public interface IAutoUserViewModelDependencies
{
    Guid NewGuid();
}
```
