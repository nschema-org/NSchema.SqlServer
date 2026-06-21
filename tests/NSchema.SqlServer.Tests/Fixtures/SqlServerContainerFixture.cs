using Testcontainers.MsSql;

namespace NSchema.SqlServer.Tests.Fixtures;

/// <summary>
/// Spins up a real SQL Server (<c>mcr.microsoft.com/mssql/server</c>) via Testcontainers for the execution and
/// introspection tests. Docker must be running locally.
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
