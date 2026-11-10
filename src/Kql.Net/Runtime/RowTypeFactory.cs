using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace Kql.Net.Runtime;

/// <summary>
/// Emits simple immutable row types (constructor + get-only properties, deriving from <see cref="KqlRow"/>).
/// Because rows are created with <c>NewExpression</c> + member list (the same shape as C# anonymous types),
/// LINQ providers such as EF Core can translate projections over them.
/// </summary>
internal static class RowTypeFactory
{
    private static readonly ModuleBuilder Module = AssemblyBuilder
        .DefineDynamicAssembly(new AssemblyName("Kql.Net.DynamicRows"), AssemblyBuilderAccess.Run)
        .DefineDynamicModule("Kql.Net.DynamicRows");

    private static readonly ConcurrentDictionary<string, Type> Cache = new();
    private static int _counter;
    private static readonly object EmitLock = new();

    private static readonly ConcurrentDictionary<Type, IReadOnlyList<string>> ColumnNames = new();

    public static Type GetRowType(IReadOnlyList<(string Name, Type Type)> columns)
    {
        var key = string.Join("|", columns.Select(c => c.Name.Replace("|", "||") + ":" + c.Type.AssemblyQualifiedName));
        return Cache.GetOrAdd(key, _ =>
        {
            var type = Emit(columns);
            ColumnNames[type] = columns.Select(c => c.Name).ToArray();
            return type;
        });
    }

    /// <summary>The original (unsanitised) column names of an emitted row type.</summary>
    public static bool TryGetColumnNames(Type rowType, out IReadOnlyList<string> names) =>
        ColumnNames.TryGetValue(rowType, out names!);

    /// <summary>Property names in emitted types (sanitised, unique, in column order).</summary>
    public static IReadOnlyList<PropertyInfo> GetProperties(Type rowType) =>
        rowType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(p => p.MetadataToken).ToArray();

    private static Type Emit(IReadOnlyList<(string Name, Type Type)> columns)
    {
        lock (EmitLock)
        {
            var tb = Module.DefineType($"KqlRow_{Interlocked.Increment(ref _counter)}",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, typeof(KqlRow));

            var names = UniqueNames(columns.Select(c => c.Name));
            var fields = new FieldBuilder[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                var type = columns[i].Type;
                fields[i] = tb.DefineField("_" + names[i], type, FieldAttributes.Private | FieldAttributes.InitOnly);
                var prop = tb.DefineProperty(names[i], PropertyAttributes.None, type, null);
                var getter = tb.DefineMethod("get_" + names[i],
                    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, type, Type.EmptyTypes);
                var il = getter.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, fields[i]);
                il.Emit(OpCodes.Ret);
                prop.SetGetMethod(getter);
            }

            var ctor = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, columns.Select(c => c.Type).ToArray());
            for (var i = 0; i < columns.Count; i++) ctor.DefineParameter(i + 1, ParameterAttributes.None, names[i]);
            var cil = ctor.GetILGenerator();
            cil.Emit(OpCodes.Ldarg_0);
            cil.Emit(OpCodes.Call, typeof(KqlRow).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!);
            for (var i = 0; i < columns.Count; i++)
            {
                cil.Emit(OpCodes.Ldarg_0);
                cil.Emit(OpCodes.Ldarg, i + 1);
                cil.Emit(OpCodes.Stfld, fields[i]);
            }
            cil.Emit(OpCodes.Ret);

            EmitEquality(tb, fields);
            return tb.CreateType();
        }
    }

    private static readonly MethodInfo TypedEquals = typeof(KqlComparer).GetMethod(nameof(KqlComparer.TypedEquals))!;
    private static readonly MethodInfo TypedHash = typeof(KqlComparer).GetMethod(nameof(KqlComparer.TypedHash))!;
    private static readonly MethodInfo ValueEquals = typeof(KqlComparer).GetMethod(nameof(KqlComparer.ValueEquals))!;
    private static readonly MethodInfo ValueHash = typeof(KqlComparer).GetMethod(nameof(KqlComparer.ValueHash))!;

    /// <summary>
    /// Overrides <see cref="KqlRow"/>'s equality members with field-by-field code: typed comparisons for scalar
    /// columns (ordinal strings) and <see cref="KqlComparer.ValueEquals"/> for dynamic ones, without boxing.
    /// </summary>
    private static void EmitEquality(TypeBuilder tb, FieldBuilder[] fields)
    {
        bool IsTyped(Type t) => t != typeof(object) && Schema.KqlTypes.IsScalarClrType(t);

        void LoadForObjectCall(ILGenerator il, FieldBuilder f)
        {
            il.Emit(OpCodes.Ldfld, f);
            if (f.FieldType.IsValueType) il.Emit(OpCodes.Box, f.FieldType);
        }

        // protected override bool EqualsCore(KqlRow other)
        var eq = tb.DefineMethod("EqualsCore", MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(bool), [typeof(KqlRow)]);
        var il = eq.GetILGenerator();
        var other = il.DeclareLocal(tb);
        var no = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, tb);
        il.Emit(OpCodes.Stloc, other);
        foreach (var f in fields)
        {
            var typed = IsTyped(f.FieldType);
            il.Emit(OpCodes.Ldarg_0);
            if (typed) il.Emit(OpCodes.Ldfld, f); else LoadForObjectCall(il, f);
            il.Emit(OpCodes.Ldloc, other);
            if (typed) il.Emit(OpCodes.Ldfld, f); else LoadForObjectCall(il, f);
            il.Emit(OpCodes.Call, typed ? TypedEquals.MakeGenericMethod(f.FieldType) : ValueEquals);
            il.Emit(OpCodes.Brfalse, no);
        }
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(no);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        // public override int GetHashCode(): 17, then h * 31 + hash(field)
        var hash = tb.DefineMethod("GetHashCode", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(int), Type.EmptyTypes);
        il = hash.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, 17);
        foreach (var f in fields)
        {
            var typed = IsTyped(f.FieldType);
            il.Emit(OpCodes.Ldc_I4, 31);
            il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Ldarg_0);
            if (typed) il.Emit(OpCodes.Ldfld, f); else LoadForObjectCall(il, f);
            il.Emit(OpCodes.Call, typed ? TypedHash.MakeGenericMethod(f.FieldType) : ValueHash);
            il.Emit(OpCodes.Add);
        }
        il.Emit(OpCodes.Ret);

        // public override bool HasNullValue()
        var hasNull = tb.DefineMethod("HasNullValue", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(bool), Type.EmptyTypes);
        il = hasNull.GetILGenerator();
        var yes = il.DefineLabel();
        foreach (var f in fields)
        {
            if (f.FieldType.IsValueType && Nullable.GetUnderlyingType(f.FieldType) is null) continue;
            il.Emit(OpCodes.Ldarg_0);
            if (f.FieldType.IsValueType)
            {
                il.Emit(OpCodes.Ldflda, f);
                il.Emit(OpCodes.Call, f.FieldType.GetProperty("HasValue")!.GetMethod!);
            }
            else il.Emit(OpCodes.Ldfld, f);
            il.Emit(OpCodes.Brfalse, yes);
        }
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(yes);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
    }

    private static string[] UniqueNames(IEnumerable<string> names)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        return names.Select(n =>
        {
            var sb = new StringBuilder();
            foreach (var ch in n) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
            if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
            var baseName = sb.ToString();
            var candidate = baseName;
            for (var i = 2; !used.Add(candidate); i++) candidate = baseName + "_" + i;
            return candidate;
        }).ToArray();
    }
}
