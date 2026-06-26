using NSchema.Configuration;
using NSchema.Plugins;

namespace NSchema.SqlServer;

/// <summary>
/// The NSchema plugin manifest for the SQL Server provider.
/// </summary>
public sealed class SqlServerPlugin : INSchemaProviderPlugin
{
    private const string EnvConnectionString = "NSCHEMA_SQLSERVER_CONNECTION_STRING";
    private const string EnvUsername = "NSCHEMA_SQLSERVER_USERNAME";
    private const string EnvPassword = "NSCHEMA_SQLSERVER_PASSWORD";

    private const string Template =
        """
        PROVIDER sqlserver (
          connection_string = ''
        );
        """;

    /// <inheritdoc />
    public string Label => "sqlserver";

    /// <inheritdoc />
    public string GetScaffoldTemplate(ScaffoldContext context) => Template;

    /// <inheritdoc />
    public PluginConfigureResult Configure(NSchemaApplicationBuilder builder, ConfigBlock block)
    {
        var errors = new List<string>();
        var connectionString = "";
        string? username = null;
        string? password = null;
        int? commandTimeout = null;

        foreach (var (key, value) in block.Attributes)
        {
            switch (key.ToLowerInvariant())
            {
                case "connection_string":
                    connectionString = value.AsString();
                    break;
                case "username":
                    username = value.AsString();
                    break;
                case "password":
                    password = value.AsString();
                    break;
                case "command_timeout":
                    if (value.Kind is ConfigValueKind.Integer)
                    {
                        commandTimeout = (int)value.AsInteger();
                    }
                    else
                    {
                        errors.Add("PROVIDER sqlserver: command_timeout must be an integer.");
                    }

                    break;
                default:
                    errors.Add($"PROVIDER sqlserver: unknown attribute '{key}'.");
                    break;
            }
        }

        // Credentials may be supplied out of band (e.g. a secret store); the environment overrides the block.
        connectionString = Environment.GetEnvironmentVariable(EnvConnectionString) ?? connectionString;
        username = Environment.GetEnvironmentVariable(EnvUsername) ?? username;
        password = Environment.GetEnvironmentVariable(EnvPassword) ?? password;

        if (string.IsNullOrEmpty(connectionString))
        {
            errors.Add($"PROVIDER sqlserver: connection_string is required. Set it via the {EnvConnectionString} environment variable or the block attribute.");
        }

        if (commandTimeout is < 0)
        {
            errors.Add("PROVIDER sqlserver: command_timeout must not be negative.");
        }

        if (errors.Count > 0)
        {
            return PluginConfigureResult.Failure([.. errors]);
        }

        builder.UseSqlServerSchema(connectionStringBuilder =>
        {
            // Order matters: assigning ConnectionString re-parses the whole string, so it must precede the discrete overrides.
            connectionStringBuilder.ConnectionString = connectionString;
            if (username is not null)
            {
                connectionStringBuilder.UserID = username;
            }

            if (password is not null)
            {
                connectionStringBuilder.Password = password;
            }

            if (commandTimeout is { } timeout)
            {
                connectionStringBuilder.CommandTimeout = timeout;
            }
        });

        return PluginConfigureResult.Success;
    }
}
