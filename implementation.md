Steps implementation of sourcegenerator:
1. DONE Create copy of class defined with Overwrites.ForClass in a .g.cs file with a _g suffix
2. DONE Overwrites.MakePublic
3. Overwrites.ReplaceProperty
4. Overwrites.Replace
5. Overwrites.InheritFrom (probably easier than MockInheritance?)
6. Overwrites.MockInheritance (spicy)
7. Overwrites.Mock<> (spicy)
8. Implement Overwrites.Include (to allow a common base builder), last because not required

Worst case, I don't use a sourcegenerator and simply run the builder as a script and create a real class
That way developers can edit it as well
There should be a unittest at the start that checks for changes in the real file and reruns the builder or something

Possible ways to circumvent the namespace pollution problem:
* Change namespace to .Generated. (only partial solution)
* Don't use sourcegenerator, use a custom MSBuild codegen step (Roslyn MSBuildWorkspace?)
* <AdditionalFiles Include="..\ProjectA\**\*.cs" /> (ugly hack)

MSBuildWorkspace?:
<ItemGroup>
  <!-- Path to Project A (or pass it via a property) -->
  <_ProjectA Include="..\ProjectA\ProjectA.csproj" />
</ItemGroup>

<Target Name="GenerateFromProjectA"
        BeforeTargets="CoreCompile"
        Inputs="@(_ProjectA)"
        Outputs="$(IntermediateOutputPath)Generated\FromA.g.cs">

  <MakeDir Directories="$(IntermediateOutputPath)Generated" />

  <Exec Command='dotnet run --project ..\Tools\MyGen\MyGen.csproj -- "@(_ProjectA)" "$(IntermediateOutputPath)Generated\FromA.g.cs"' />

  <ItemGroup>
    <Compile Include="$(IntermediateOutputPath)Generated\FromA.g.cs"
             AutoGen="True"
             DesignTime="True" />
  </ItemGroup>
</Target>