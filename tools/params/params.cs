// Usage: dotnet run params.cs -- <assembly.dll> <Namespace.Type> <Method>
// Prints parameter names and positions for each overload (needed for Harmony argument binding). Read-only.
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using var fs = File.OpenRead(args[0]);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();
foreach (var th in md.TypeDefinitions)
{
    var td = md.GetTypeDefinition(th);
    if (md.GetString(td.Namespace) + "." + md.GetString(td.Name) != args[1]) continue;
    foreach (var mh in td.GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        if (md.GetString(m.Name) != args[2]) continue;
        var names = m.GetParameters().Select(p => md.GetParameter(p)).Where(p => p.SequenceNumber > 0)
            .Select(p => $"{p.SequenceNumber}:{md.GetString(p.Name)}");
        Console.WriteLine($"{args[2]}({string.Join(", ", names)}) attrs={m.Attributes}");
    }
}
