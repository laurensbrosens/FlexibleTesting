Read @file:README.md for the general idea. I want to fix a problem with the way the Overwrites.Mock currently works for properties. Overwrites.Mock(() => DateTime.Now); and Overwrites.Mock(() => Now); (a property that is also called Now) will both use the same generated _dependencies.Now in the copy. However, the user might want to mock these seperatly. To prevent these naming collisions, static properties like DateTime.Now should always become DateTime_Now on the dependency interface in the generated file. The Now property in the class itself should not be renamed. It is possible a part of the generation code needs to be refactored to support this change. Here are some relevant files: @file:FlexibleTestingInstructionsCreator.cs @file:FlexibleTestingCodeGenerator.cs @file:FlexibleTestingTask.cs . Also make sure the Overwrites.Mock(() => Now); mocking actually works, it seems to have broken somehow (maybe that never worked?). Test using the workflow as described in the agent.md
Check that the UserViewModel code in it's constructor:
var test1 = DateTime.Now;
var test2 = Now;
becomes this in the generated file:
var test1 = _dependencies.DateTime_Now;
var test2 = _dependencies.Now;