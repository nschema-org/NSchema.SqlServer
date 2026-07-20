using System.Runtime.CompilerServices;
using NSchema.Model;

namespace NSchema.SqlServer.Tests;

internal static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        DerivePathInfo((sourceFile, _, type, method) => new PathInfo(
            directory: Path.Combine(Path.GetDirectoryName(sourceFile)!, "Snapshots"),
            typeName: type.Name,
            methodName: method.Name
        ));

        // A value object (identifier, opaque SQL) is a string in every rendered form; snapshots show its exact
        // underlying text.
        VerifierSettings.AddExtraSettings(settings => settings.Converters.Add(new ValueObjectConverter()));
    }

    private sealed class ValueObjectConverter : WriteOnlyJsonConverter
    {
        public override bool CanConvert(Type type)
        {
            for (var current = type.BaseType; current is not null; current = current.BaseType)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ValueObject<>))
                {
                    return true;
                }
            }

            return false;
        }

        public override void Write(VerifyJsonWriter writer, object value) => writer.WriteValue(value.ToString());
    }
}
