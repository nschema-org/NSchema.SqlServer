namespace NSchema.SqlServer;

/// <summary>
/// The schemas SQL Server provides.
/// </summary>
internal static class SqlServerSchemas
{
    /// <summary>
    /// Every SQL Server database has a <c>dbo</c> schema; a migration neither creates nor drops it.
    /// </summary>
    public const string Provided = "dbo";

    /// <summary>
    /// The engine's own schema, captured for the native types it provides.
    /// </summary>
    public const string System = "sys";
}
