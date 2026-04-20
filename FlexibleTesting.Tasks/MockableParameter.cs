using Microsoft.CodeAnalysis;

namespace FlexibleTesting.Tasks;

public record MockableParameter(
    string Name,
    string TypeDisplay,
    NullableAnnotation NullableAnnotation,
    bool HasExplicitDefaultValue,
    object? ExplicitDefaultValue,
    bool HasCallerMemberNameAttribute
);
