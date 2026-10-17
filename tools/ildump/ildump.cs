// Usage: dotnet run ildump.cs -- <assembly.dll> <Namespace.Type> <Method> [overloadIndex]
// Disassembles one method's IL: offset, opcode, operand (members, strings, branch targets, locals/args).
// Read-only; for locating control flow when no decompiler is available.
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

var oneByte = new Dictionary<byte, OpCode>();
var twoByte = new Dictionary<byte, OpCode>();
foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
{
    var op = (OpCode)f.GetValue(null)!;
    var v = (ushort)op.Value;
    if (op.Size == 1) oneByte[(byte)v] = op; else twoByte[(byte)(v & 0xFF)] = op;
}

using var fs = File.OpenRead(args[0]);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();
var wantIndex = args.Length > 3 ? int.Parse(args[3]) : -1;

string TypeName(EntityHandle h) => h.Kind switch
{
    HandleKind.TypeDefinition => Def((TypeDefinitionHandle)h),
    HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Namespace) + "." + md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Name),
    _ => "<spec>",
};
string Def(TypeDefinitionHandle h)
{
    var td = md.GetTypeDefinition(h);
    var decl = td.GetDeclaringType();
    return (decl.IsNil ? md.GetString(td.Namespace) + "." : Def(decl) + "+") + md.GetString(td.Name);
}
string Member(EntityHandle h)
{
    switch (h.Kind)
    {
        case HandleKind.MethodDefinition: { var m = md.GetMethodDefinition((MethodDefinitionHandle)h); return TypeName(m.GetDeclaringType()) + "::" + md.GetString(m.Name); }
        case HandleKind.FieldDefinition: { var f = md.GetFieldDefinition((FieldDefinitionHandle)h); return TypeName(f.GetDeclaringType()) + "::" + md.GetString(f.Name); }
        case HandleKind.MemberReference: { var r = md.GetMemberReference((MemberReferenceHandle)h); return TypeName(r.Parent) + "::" + md.GetString(r.Name); }
        case HandleKind.MethodSpecification: return Member(md.GetMethodSpecification((MethodSpecificationHandle)h).Method);
        default: return TypeName(h);
    }
}

var index = 0;
foreach (var th in md.TypeDefinitions)
{
    if (Def(th) != args[1]) continue;
    foreach (var mh in md.GetTypeDefinition(th).GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        if (md.GetString(m.Name) != args[2]) continue;
        if (wantIndex >= 0 && index++ != wantIndex) continue;
        var names = m.GetParameters().Select(p => md.GetParameter(p)).Where(p => p.SequenceNumber > 0).Select(p => md.GetString(p.Name));
        Console.WriteLine($"// {args[1]}::{args[2]}({string.Join(", ", names)})  static={m.Attributes.HasFlag(System.Reflection.MethodAttributes.Static)}");
        if (m.RelativeVirtualAddress == 0) { Console.WriteLine("// no body"); continue; }
        var body = pe.GetMethodBody(m.RelativeVirtualAddress);
        var il = body.GetILReader();
        while (il.RemainingBytes > 0)
        {
            var offset = il.Offset;
            var b = il.ReadByte();
            var op = b == 0xFE ? twoByte[il.ReadByte()] : oneByte[b];
            string operand = op.OperandType switch
            {
                OperandType.InlineNone => "",
                OperandType.ShortInlineBrTarget => $"IL_{il.ReadSByte() + il.Offset:X4}",
                OperandType.InlineBrTarget => $"IL_{il.ReadInt32() + il.Offset:X4}",
                OperandType.ShortInlineI => il.ReadSByte().ToString(),
                OperandType.ShortInlineVar => il.ReadByte().ToString(),
                OperandType.InlineVar => il.ReadUInt16().ToString(),
                OperandType.InlineI => il.ReadInt32().ToString(),
                OperandType.InlineI8 => il.ReadInt64().ToString(),
                OperandType.ShortInlineR => il.ReadSingle().ToString(),
                OperandType.InlineR => il.ReadDouble().ToString(),
                OperandType.InlineString => "\"" + md.GetUserString(MetadataTokens.UserStringHandle(il.ReadInt32() & 0xFFFFFF)) + "\"",
                OperandType.InlineSwitch => Switch(ref il),
                OperandType.InlineSig => $"sig 0x{il.ReadInt32():X8}",
                _ => SafeMember(il.ReadInt32()),
            };
            Console.WriteLine($"IL_{offset:X4}: {op.Name,-12} {operand}");
        }
        foreach (var region in body.ExceptionRegions)
            Console.WriteLine($"// {region.Kind} try IL_{region.TryOffset:X4}-IL_{region.TryOffset + region.TryLength:X4} handler IL_{region.HandlerOffset:X4}");
    }
}

string SafeMember(int token)
{
    try { return Member(MetadataTokens.EntityHandle(token)); } catch { return $"0x{token:X8}"; }
}

string Switch(ref BlobReader il)
{
    var n = il.ReadInt32();
    var targets = new int[n];
    for (var i = 0; i < n; i++) targets[i] = il.ReadInt32();
    var baseOffset = il.Offset;
    return "(" + string.Join(", ", targets.Select(t => $"IL_{t + baseOffset:X4}")) + ")";
}
