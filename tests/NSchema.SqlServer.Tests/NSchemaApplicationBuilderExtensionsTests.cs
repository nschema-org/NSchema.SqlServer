using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace NSchema.SqlServer.Tests;

/// <summary>
/// Covers the service registrations <see cref="NSchemaApplicationBuilderExtensions"/> makes. The generator/provider
/// tests drive SQL and introspection directly and never go through DI; these guard the wiring the host (the CLI)
/// relies on — in particular the <see cref="DbDataSource"/> the core's SQL executor needs to apply a plan.
/// </summary>
public sealed class NSchemaApplicationBuilderExtensionsTests
{
    private const string ConnectionString = "Server=localhost;Database=nschema;Trusted_Connection=True;TrustServerCertificate=True";

    [Fact]
    public void UseSqlServerSchema_RegistersADbDataSource_TheExecutorCanResolve()
    {
        // Arrange — wire the provider exactly as a host does.
        var builder = NSchemaApplication.CreateBuilder();
        builder.UseSqlServerSchema(ConnectionString);
        using var services = builder.Services.BuildServiceProvider();

        // Act — the core SqlExecutor resolves a DbDataSource to apply a plan; without one, apply throws.
        var dataSource = services.GetService<DbDataSource>();

        // Assert — it is registered and produces SQL Server connections against the configured database.
        dataSource.ShouldNotBeNull();
        dataSource.ConnectionString.ShouldBe(ConnectionString);
        dataSource.CreateConnection().ShouldBeOfType<SqlConnection>();
    }

    [Fact]
    public void UseSqlServerSchema_ExposesOneConnectionSourceUnderBothFacets()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        builder.UseSqlServerSchema(ConnectionString);
        using var services = builder.Services.BuildServiceProvider();

        // Act — the schema provider reads through SqlServerConnectionSource; the executor writes through DbDataSource.
        var source = services.GetService<SqlServerConnectionSource>();
        var dataSource = services.GetService<DbDataSource>();

        // Assert — both resolve to the single instance, so reads and writes share one connection seam.
        source.ShouldNotBeNull();
        dataSource.ShouldBeSameAs(source);
    }

    [Fact]
    public void UseSqlServerSchema_WithBuilderDelegate_BuildsTheConnectionString()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        builder.UseSqlServerSchema(b =>
        {
            b.DataSource = "localhost";
            b.InitialCatalog = "nschema";
            b.TrustServerCertificate = true;
        });
        using var services = builder.Services.BuildServiceProvider();

        // Act
        var dataSource = services.GetService<DbDataSource>();

        // Assert
        dataSource.ShouldNotBeNull();
        dataSource.ConnectionString.ShouldContain("localhost");
        dataSource.ConnectionString.ShouldContain("nschema");
    }
}
