using NSchema.Model.Columns;

namespace NSchema.SqlServer;

/// <summary>
/// Provides extension methods for defining SQL Server-specific SQL types in NSchema.
/// </summary>
public static class SqlTypeSqlServerExtensions
{
    extension(SqlType)
    {
        /// <summary>
        /// The SQL Server <c>money</c> type — a fixed-point currency value.
        /// </summary>
        public static SqlType Money => SqlType.Custom("money");

        /// <summary>
        /// The SQL Server <c>xml</c> type, for storing XML documents and fragments.
        /// </summary>
        public static SqlType Xml => SqlType.Custom("xml");

        /// <summary>
        /// The SQL Server <c>rowversion</c> type (also surfaced as <c>timestamp</c>), an automatically maintained
        /// row-version stamp.
        /// </summary>
        public static SqlType RowVersion => SqlType.Custom("rowversion");
    }
}
