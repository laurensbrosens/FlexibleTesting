using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis.MSBuild;
using System;
using System.Collections.Generic;
using System.IO;

namespace FlexibleTesting.Tasks;

public class FlexibleTestingTask : Microsoft.Build.Utilities.Task
{
    public string? OutputPath { get; set; }

    [Required]
    public string LegacyProjectPath { get; set; } = string.Empty;

    public override bool Execute()
    {
        try
        {
            // System.Diagnostics.Debugger.Launch();

            OutputPath = string.IsNullOrWhiteSpace(OutputPath)
                ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(BuildEngine.ProjectFileOfTaskNode)!, "Generated"))
                : Path.GetFullPath(OutputPath!);

            var properties = new Dictionary<string, string>
            {
                ["DesignTimeBuild"] = "true",
                ["BuildingInsideVisualStudio"] = "true",
                ["SkipCompilerExecution"] = "true",
                ["BuildProjectReferences"] = "false",
                ["ProvideCommandLineArgs"] = "true",
                ["FlexibleTestingTaskRunning"] = "true",
            };

            using var workspace = MSBuildWorkspace.Create(properties);
            workspace.SkipUnrecognizedProjects = true;

            var legacyProject = workspace.OpenProjectAsync(LegacyProjectPath).Result;
            var legacyComp = legacyProject.GetCompilationAsync().Result ?? throw new InvalidOperationException("Could not get legacy compilation");
            
            var testProject = workspace.OpenProjectAsync(BuildEngine.ProjectFileOfTaskNode).Result;
            var testComp = testProject.GetCompilationAsync().Result ?? throw new InvalidOperationException("Could not get test compilation");

            var creator = new FlexibleTestingInstructionsCreator(legacyComp, testComp);
            var generator = new FlexibleTestingCodeGenerator(legacyProject, OutputPath);

            foreach (var instructions in creator.CreateAll())
            {
                generator.Generate(instructions);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: true);
            return false;
        }
    }
}
