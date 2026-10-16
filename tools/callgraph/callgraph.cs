// Usage: dotnet run callgraph.cs -- <assembly.dll> <out.txt>
// Writes one line per call site / field access: "Caller.Type::Method -> op Target.Type::Member". Read-only.
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using var fs = File.OpenRead(args[0]);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();

// Opcode operand sizes (single-byte opcodes and 0xFE-prefixed).
int OperandSize(ushort op) => op switch
{
    0x1F or 0x2B or 0x2C or 0x2D or 0x2E or 0x2F or 0x30 or 0x31 or 0x32 or 0x33 or 0x34 or 0x35 or 0x36 or 0x37 or 0xDE
        or 0x0E or 0x0F or 0x10 or 0x11 or 0x12 or 0x13 => 1,
    0x20 or 0x22 or 0x28 or 0x29 or 0x38 or 0x39 or 0x3A or 0x3B or 0x3C or 0x3D or 0x3E or 0x3F or 0x40 or 0x41 or 0x42
        or 0x43 or 0x44 or 0x6F or 0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 or 0x79 or 0x7B or 0x7C or 0x7D or 0x7E
        or 0x7F or 0x80 or 0x81 or 0x8C or 0x8D or 0x8F or 0xA3 or 0xA4 or 0xA5 or 0xC2 or 0xC6 or 0xD0 or 0xDD or 0x27 => 4,
    0x21 or 0x23 => 8,
    0xFE06 or 0xFE07 or 0xFE15 or 0xFE16 or 0xFE1C or 0xFE1D => 4,
    0xFE09 or 0xFE0A or 0xFE0B or 0xFE0C or 0xFE0D or 0xFE0E => 2,
    0xFE12 => 1,
    _ => 0,
};

string TypeName(EntityHandle h)
{
    switch (h.Kind)
    {
        case HandleKind.TypeDefinition:
            var td = md.GetTypeDefinition((TypeDefinitionHandle)h);
            var decl = td.GetDeclaringType();
            var pre = decl.IsNil ? md.GetString(td.Namespace) + "." : TypeName(decl) + "+";
            return pre + md.GetString(td.Name);
        case HandleKind.TypeReference:
            var tr = md.GetTypeReference((TypeReferenceHandle)h);
            return md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
        case HandleKind.TypeSpecification: return "<spec>";
        default: return "?";
    }
}

string MemberName(EntityHandle h)
{
    switch (h.Kind)
    {
        case HandleKind.MethodDefinition:
            var m = md.GetMethodDefinition((MethodDefinitionHandle)h);
            return TypeName(m.GetDeclaringType()) + "::" + md.GetString(m.Name);
        case HandleKind.FieldDefinition:
            var f = md.GetFieldDefinition((FieldDefinitionHandle)h);
            return TypeName(f.GetDeclaringType()) + "::" + md.GetString(f.Name);
        case HandleKind.MemberReference:
            var r = md.GetMemberReference((MemberReferenceHandle)h);
            var parent = r.Parent.Kind == HandleKind.TypeSpecification
                ? SpecName((TypeSpecificationHandle)r.Parent) : TypeName(r.Parent);
            return parent + "::" + md.GetString(r.Name);
        case HandleKind.MethodSpecification:
            return MemberName(md.GetMethodSpecification((MethodSpecificationHandle)h).Method);
        default: return TypeName(h);
    }
}

// Best effort for generic instantiations: take the generic type's name from the blob.
string SpecName(TypeSpecificationHandle h)
{
    var br = md.GetBlobReader(md.GetTypeSpecification(h).Signature);
    if (br.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance)
    {
        br.ReadSignatureTypeCode();
        return TypeName(br.ReadTypeHandle()) + "<>";
    }
    return "<spec>";
}

using var w = new StreamWriter(args[1]);
foreach (var th in md.TypeDefinitions)
{
    foreach (var mh in md.GetTypeDefinition(th).GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        if (m.RelativeVirtualAddress == 0) continue;
        var caller = MemberName(mh);
        var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILReader();
        while (il.RemainingBytes > 0)
        {
            ushort op = il.ReadByte();
            if (op == 0xFE) op = (ushort)(0xFE00 | il.ReadByte());
            if (op == 0x45) { var n = il.ReadInt32(); il.Offset += n * 4; continue; } // switch
            var size = OperandSize(op);
            if (size == 4 && op is 0x28 or 0x6F or 0x73 or 0x7B or 0x7C or 0x7D or 0x7E or 0x7F or 0x80 or 0xFE06 or 0xFE07)
            {
                var tok = il.ReadInt32();
                var kind = op switch { 0x28 => "call", 0x6F => "callvirt", 0x73 => "newobj", 0x7D or 0x80 => "stfld", 0xFE06 or 0xFE07 => "ldftn", _ => "ldfld" };
                string target;
                try { target = MemberName(MetadataTokens.EntityHandle(tok)); } catch { target = $"0x{tok:X8}"; }
                w.WriteLine($"{caller} -> {kind} {target}");
            }
            else if (op == 0x72)
            {
                var tok = il.ReadInt32();
                var s = md.GetUserString(MetadataTokens.UserStringHandle(tok & 0xFFFFFF));
                w.WriteLine($"{caller} -> ldstr \"{s.Replace("\n", "\\n")}\"");
            }
            else il.Offset += size;
        }
    }
}
