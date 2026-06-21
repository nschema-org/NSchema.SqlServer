using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace NSchema.SqlServer;

/// <summary>
/// The single connection seam for the SQL Server provider.
/// </summary>
internal sealed class SqlServerConnectionSource(string connectionString) : DbDataSource
{
    /// <inheritdoc/>
    public override string ConnectionString { get; } = connectionString;

    /// <inheritdoc/>
    protected override DbConnection CreateDbConnection() => new SqlConnection(ConnectionString);
}
