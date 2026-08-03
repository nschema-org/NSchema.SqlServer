using NSchema.Diff.Plugins;
using NSchema.Model.Columns;

namespace NSchema.SqlServer.Sql;

/// <summary>
/// SQL Server equivalence rules so spellings the catalog and a project may disagree on compare as equal.
/// </summary>
public sealed class SqlServerSqlEquivalence : SqlEquivalence
{
    /// <inheritdoc/>
    /// <remarks>
    /// A type in <c>dbo</c> or <c>sys</c> is addressable bare, so the qualifier folds away;
    /// a type in any other schema keeps it.
    /// </remarks>
    public override IEqualityComparer<SqlType> Types { get; } = new TypeEquality();

    private sealed class TypeEquality : IEqualityComparer<SqlType>
    {
        public bool Equals(SqlType? x, SqlType? y) => object.Equals(Fold(x), Fold(y));

        public int GetHashCode(SqlType obj) => Fold(obj)!.GetHashCode();

        private static SqlType? Fold(SqlType? type) =>
            type?.Schema?.Value is SqlServerSchemas.Provided or SqlServerSchemas.System ? type with { Schema = null } : type;
    }
}
