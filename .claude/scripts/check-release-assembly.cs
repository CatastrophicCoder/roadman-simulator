// Checks that a release build of the game assembly contains no test code.
// Usage: dotnet run .claude/scripts/check-release-assembly.cs -- <path/to/RoadmanSimulator.dll>
// Exits 1 and lists the offenders if the assembly references a test framework,
// defines a type in a test or gdUnit4 namespace, or has test attributes on
// types or methods.
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: check-release-assembly.cs <assembly.dll>");
    return 2;
}

string[] testAssemblyPrefixes = ["gdUnit4", "xunit", "Microsoft.TestPlatform", "Microsoft.VisualStudio.TestPlatform", "testhost", "Moq"];
string[] testAttributes = ["TestSuiteAttribute", "TestCaseAttribute", "FactAttribute", "TheoryAttribute"];

using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var md = pe.GetMetadataReader();
var problems = new List<string>();

foreach (var handle in md.AssemblyReferences)
{
    var name = md.GetString(md.GetAssemblyReference(handle).Name);
    if (testAssemblyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
    {
        problems.Add($"references test assembly {name}");
    }
}

foreach (var handle in md.TypeDefinitions)
{
    var type = md.GetTypeDefinition(handle);
    var ns = md.GetString(type.Namespace);
    var name = md.GetString(type.Name);
    if (ns.Split('.').Any(part => part is "Tests" or "Test"))
    {
        problems.Add($"type in test namespace: {ns}.{name}");
    }

    // The gdUnit4 add-on's C# bridge compiles to a stub class when its packages are absent.
    if (ns.StartsWith("gdUnit4", StringComparison.OrdinalIgnoreCase))
    {
        problems.Add($"type in gdUnit4 namespace: {ns}.{name}");
    }

    foreach (var attrName in TestAttributesOn(type.GetCustomAttributes()))
    {
        problems.Add($"type with [{attrName}]: {ns}.{name}");
    }

    foreach (var methodHandle in type.GetMethods())
    {
        var method = md.GetMethodDefinition(methodHandle);
        foreach (var attrName in TestAttributesOn(method.GetCustomAttributes()))
        {
            problems.Add($"method with [{attrName}]: {ns}.{name}.{md.GetString(method.Name)}");
        }
    }
}

foreach (var problem in problems)
{
    Console.WriteLine(problem);
}

Console.WriteLine(problems.Count == 0 ? $"OK: no test code in {args[0]}" : $"{problems.Count} problem(s) in {args[0]}");
return problems.Count == 0 ? 0 : 1;

IEnumerable<string> TestAttributesOn(CustomAttributeHandleCollection attributes)
{
    foreach (var attrHandle in attributes)
    {
        var ctor = md.GetCustomAttribute(attrHandle).Constructor;
        if (ctor.Kind != HandleKind.MemberReference)
        {
            continue;
        }

        var parent = md.GetMemberReference((MemberReferenceHandle)ctor).Parent;
        if (parent.Kind == HandleKind.TypeReference)
        {
            var attrName = md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name);
            if (testAttributes.Contains(attrName))
            {
                yield return attrName;
            }
        }
    }
}
