using NSchema.Project.Domain.Directives;
using NSchema.Project.Policies;

namespace NSchema.SqlServer;

/// <summary>
/// Rejects a declaration of a schema SQL Server provides.
/// </summary>
internal sealed class SqlServerProvidedSchemaPolicy : IProjectPolicy
{
    private const string Source = "sqlserver";

    /// <inheritdoc />
    public IEnumerable<Diagnostic> Validate(ProjectDefinition project) => project.Database.Schemas
        .Where(schema => !schema.IsImplicit && schema.Name == SqlServerSchemas.Provided)
        .Select(schema => Diagnostic.Warning(Source, "provided-schema-declared", $"SQL Server provides the '{schema.Name}' schema, so it will be ignored."));
}
