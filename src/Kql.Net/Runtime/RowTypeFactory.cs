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

            return tb.CreateType();
        }
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
