// Usage: dotnet run dumpmeta.cs -- <assembly.dll> <out-types.txt> <out-strings.txt>
// Dumps types (with base type), fields and methods, plus the user-string heap. Read-only.
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

var path = args[0];
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();

string TypeName(EntityHandle h)
{
    if (h.IsNil) return "";
    switch (h.Kind)
    {
        case HandleKind.TypeDefinition:
            var td = md.GetTypeDefinition((TypeDefinitionHandle)h);
            return md.GetString(td.Namespace) + "." + md.GetString(td.Name);
        case HandleKind.TypeReference:
            var tr = md.GetTypeReference((TypeReferenceHandle)h);
            return md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
        default: return "<spec>";
    }
}

using (var w = new StreamWriter(args[1]))
{
    foreach (var th in md.TypeDefinitions)
    {
        var td = md.GetTypeDefinition(th);
        var name = md.GetString(td.Name);
        var ns = md.GetString(td.Namespace);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) ns = TypeName(decl) + "+";
        w.WriteLine($"T {ns}{(decl.IsNil ? "." : "")}{name} : {TypeName(td.BaseType)}");
        foreach (var fh in td.GetFields())
            w.WriteLine($"  F {md.GetString(md.GetFieldDefinition(fh).Name)}");
        foreach (var mh in td.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            w.WriteLine($"  M {md.GetString(m.Name)} p{m.GetParameters().Count}");
        }
    }
}

using (var w = new StreamWriter(args[2]))
{
    var h = MetadataTokens.UserStringHandle(1);
    while (!h.IsNil)
    {
        w.WriteLine(md.GetUserString(h).Replace("\n", "\\n"));
        h = md.GetNextHandle(h);
    }
}
