using System.Data.Common;
using System.Reflection;
using System.Reflection.Emit;
using Npgsql;
using Paqueteria.Infrastructure.Database.Pooling;

namespace Paqueteria.ArchitectureTests.Architecture;

/// <summary>
/// SCL-007: reads the IL of every method of a type (lambdas, local functions and async state machines included) and
/// reports each way it could build a PostgreSQL pool or connection of its own, whatever the source syntax: any
/// constructor of <see cref="NpgsqlConnection"/>, <see cref="NpgsqlDataSourceBuilder"/>,
/// <see cref="NpgsqlSlimDataSourceBuilder"/> or <see cref="RuntimeDataSource"/>; <c>NpgsqlDataSource.Create</c>;
/// <c>NpgsqlConnection.GlobalTypeMapper</c>; <c>RuntimeDataSource.Create</c>; and every EF Core <c>UseNpgsql</c>
/// overload that is not given a data source (a connection string or a connection makes EF Core build its own pool).
/// </summary>
internal static class PoolCreationScanner
{
    private const BindingFlags Declared =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.DeclaredOnly;

    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(static field => (OpCode)field.GetValue(null)!)
        .ToDictionary(static code => code.Value);

    private static readonly IReadOnlySet<Type> ConstructedTypes = new HashSet<Type>
    {
        typeof(NpgsqlConnection),
        typeof(NpgsqlDataSourceBuilder),
        typeof(NpgsqlSlimDataSourceBuilder),
        typeof(RuntimeDataSource),
    };

    internal enum Kind
    {
        /// <summary>A connection or data source builder constructed, or a data source created.</summary>
        PoolOrConnection,

        /// <summary>EF Core <c>UseNpgsql</c> given a connection string or a connection instead of a data source.</summary>
        EntityFrameworkOwnPool,
    }

    internal sealed record Finding(Type Type, MethodBase Method, Kind Kind, string What)
    {
        /// <summary>
        /// The type declared in source: compiler-generated closures, local functions and async state machines are
        /// attributed to the type whose code they came from, while a nested type written in source stays itself.
        /// </summary>
        public Type DeclaredType
        {
            get
            {
                var type = Type;
                while (type.DeclaringType is not null && type.Name.StartsWith('<'))
                {
                    type = type.DeclaringType;
                }

                return type;
            }
        }

        public override string ToString() => $"{Type.FullName}.{Method.Name}: {What}";
    }

    internal static IEnumerable<Finding> Scan(Assembly assembly) => LoadableTypes(assembly).SelectMany(ScanType);

    /// <summary>The type and every type nested in it, as the compiler emits closures and state machines.</summary>
    internal static IEnumerable<Finding> Scan(Type type) =>
        new[] { type }.Concat(Nested(type)).SelectMany(ScanType);

    private static IEnumerable<Finding> ScanType(Type type)
    {
        var methods = type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared));
        foreach (var method in methods)
        {
            foreach (var callee in Callees(method))
            {
                if (Describe(callee) is { } finding)
                {
                    yield return new Finding(type, method, finding.Kind, finding.What);
                }
            }
        }
    }

    private static (Kind Kind, string What)? Describe(MethodBase callee)
    {
        var declaring = callee.DeclaringType;
        if (declaring is null)
        {
            return null;
        }

        if (callee is ConstructorInfo && ConstructedTypes.Contains(declaring))
        {
            return (Kind.PoolOrConnection, $"new {declaring.Name}");
        }

        if (declaring == typeof(NpgsqlDataSource) && callee.Name == nameof(NpgsqlDataSource.Create))
        {
            return (Kind.PoolOrConnection, "NpgsqlDataSource.Create");
        }

        if (declaring == typeof(NpgsqlConnection) && callee.Name == "get_GlobalTypeMapper")
        {
            return (Kind.PoolOrConnection, "NpgsqlConnection.GlobalTypeMapper");
        }

        if (declaring == typeof(RuntimeDataSource) && callee.Name == nameof(RuntimeDataSource.Create))
        {
            return (Kind.PoolOrConnection, "RuntimeDataSource.Create");
        }

        if (callee.Name == "UseNpgsql" && declaring.Name == "NpgsqlDbContextOptionsBuilderExtensions")
        {
            var parameters = callee.GetParameters();
            var given = parameters.Length > 1 ? parameters[1].ParameterType : null;
            if (given is null || !typeof(DbDataSource).IsAssignableFrom(given))
            {
                return (Kind.EntityFrameworkOwnPool, $"UseNpgsql({given?.Name ?? "no connection"})");
            }
        }

        return null;
    }

    private static List<MethodBase> Callees(MethodBase method)
    {
        var callees = new List<MethodBase>();
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or BadImageFormatException)
        {
            return callees;
        }

        if (il is null)
        {
            return callees;
        }

        var position = 0;
        while (position < il.Length)
        {
            short value = il[position++];
            if (value == 0xFE)
            {
                value = unchecked((short)(0xFE00 | il[position++]));
            }

            if (!OpCodesByValue.TryGetValue(value, out var code))
            {
                throw new InvalidOperationException($"Unknown IL opcode 0x{value:X4} in {method.DeclaringType}.{method.Name}.");
            }

            switch (code.OperandType)
            {
                case OperandType.InlineMethod:
                    if (Resolve(method, BitConverter.ToInt32(il, position)) is { } callee)
                    {
                        callees.Add(callee);
                    }

                    position += 4;
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    position += 1;
                    break;
                case OperandType.InlineVar:
                    position += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    position += 8;
                    break;
                case OperandType.InlineSwitch:
                    position += 4 + (4 * BitConverter.ToInt32(il, position));
                    break;
                default:
                    position += 4;
                    break;
            }
        }

        return callees;
    }

    private static MethodBase? Resolve(MethodBase method, int token)
    {
        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring
            ? declaring.GetGenericArguments()
            : null;
        var methodArguments = method is MethodInfo { IsGenericMethod: true } ? method.GetGenericArguments() : null;
        try
        {
            return method.Module.ResolveMethod(token, typeArguments, methodArguments);
        }
        catch (Exception exception) when (exception is ArgumentException or BadImageFormatException or TypeLoadException)
        {
            try
            {
                return method.Module.ResolveMethod(token);
            }
            catch (Exception fallback) when (fallback is ArgumentException or BadImageFormatException or TypeLoadException)
            {
                return null;
            }
        }
    }

    private static IEnumerable<Type> Nested(Type type)
    {
        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            yield return nested;
            foreach (var deeper in Nested(nested))
            {
                yield return deeper;
            }
        }
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }
}
