Steps implementation of sourcegenerator:
1. DONE Create copy of class defined with Overwrites.ForClass in a .g.cs file with a _g suffix
2. Overwrites.MakePublic
3. Overwrites.ReplaceProperty
4. Overwrites.Replace
5. Overwrites.InheritFrom (probably easier than MockInheritance?)
6. Overwrites.MockInheritance (spicy)
7. Overwrites.Mock<> (spicy)
8. Implement Overwrites.Include (to allow a common base builder), last because not required

Worst case, I don't use a sourcegenerator and simply run the builder as a script and create a real class
That way developers can edit it as well
There should be a unittest at the start that checks for changes in the real file and reruns the builder or something

