using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.VisualStudio.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace FlexibleTesting.Tasks;

public class FlexibleTestingTask : Microsoft.Build.Utilities.Task
{
    public string? OutputPath { get; set; }

    [Required]
    public string LegacyProjectPath { get; set; } = string.Empty;

    private static readonly JoinableTaskContext _taskContext = new JoinableTaskContext();
    private static readonly JoinableTaskFactory _taskFactory = new JoinableTaskFactory(_taskContext);

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

            _taskFactory.Run(async () =>
            {
                using var workspace = MSBuildWorkspace.Create(properties);
                workspace.SkipUnrecognizedProjects = true;

                var legacyProjectTask = workspace.OpenProjectAsync(LegacyProjectPath);
                var testProjectTask = workspace.OpenProjectAsync(BuildEngine.ProjectFileOfTaskNode);
                await Task.WhenAll(legacyProjectTask, testProjectTask);
                var testProject = await testProjectTask;
                var testComp =
                    await testProject.GetCompilationAsync() ?? throw new InvalidOperationException("Could not get test compilation");
                var solution = workspace.CurrentSolution;

                var creator = new FlexibleTestingInstructionsCreator(solution, testComp);
                var generator = new FlexibleTestingCodeGenerator(solution, OutputPath);

                foreach (var instructions in creator.CreateAll())
                {
                    generator.Generate(instructions);
                }
            });

            return true;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: true);
            return false;
        }
    }
}
