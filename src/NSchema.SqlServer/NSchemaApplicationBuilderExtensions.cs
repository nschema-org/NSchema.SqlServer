using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using NSchema.SqlServer.Sql;

namespace NSchema.SqlServer;

/// <summary>
/// Provides extension methods for configuring NSchema to use SQL Server as the underlying database provider.
/// </summary>
public static class NSchemaApplicationBuilderExtensions
{
    extension(NSchemaApplicationBuilder builder)
    {
        /// <summary>
        /// Configures NSchema to use SQL Server as the database provider with the specified connection string.
        /// </summary>
        /// <param name="connectionString">The connection string to the SQL Server database.</param>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UseSqlServerSchema(string connectionString)
        {
            builder.Services.AddSingleton(_ => new SqlServerConnectionSource(connectionString));
            builder.Services.AddSingleton<DbDataSource>(p => p.GetRequiredService<SqlServerConnectionSource>());
            return builder.UseSqlServerSchema();
        }

        /// <summary>
        /// Configures NSchema to use SQL Server as the database provider, building the connection string with a
        /// configuration action for a <see cref="SqlConnectionStringBuilder"/>.
        /// </summary>
        /// <param name="configure">A delegate that configures the <see cref="SqlConnectionStringBuilder"/>.</param>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UseSqlServerSchema(Action<SqlConnectionStringBuilder> configure)
        {
            var connectionStringBuilder = new SqlConnectionStringBuilder();
            configure(connectionStringBuilder);
            return builder.UseSqlServerSchema(connectionStringBuilder.ConnectionString);
        }

        /// <summary>
        /// Configures NSchema to use SQL Server as the database provider by registering the schema provider and SQL
        /// generator. A <see cref="SqlServerConnectionSource"/> (and the <see cref="DbDataSource"/> the executor needs)
        /// must already be registered (use one of the overloads that accept a connection string to register them).
        /// </summary>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UseSqlServerSchema() => builder
            .UseCurrentSchema<SqlServerSchemaProvider>()
            .UseSqlServerGenerator();

        /// <summary>
        /// Configures the NSchema application to generate SQL for SQL Server.
        /// </summary>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UseSqlServerGenerator() => builder.UseSqlGenerator<SqlServerSqlGenerator>();
    }
}
