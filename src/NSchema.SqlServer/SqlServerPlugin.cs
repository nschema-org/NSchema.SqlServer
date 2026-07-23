using NSchema.Configuration.Plugins;
using NSchema.Plugins;
using NSchema.Project.Nsql;
using NSchema.Project.Nsql.Syntax;
using NSchema.Project.Nsql.Syntax.Blocks;
using NSchema.Project.Nsql.Tokens;

namespace NSchema.SqlServer;

/// <summary>
/// The NSchema plugin manifest for the SQL Server provider.
/// </summary>
public sealed class SqlServerPlugin : INSchemaDatabasePlugin
{
    private const string Source = "sqlserver";
    private const string EnvConnectionString = "NSCHEMA_SQLSERVER_CONNECTION_STRING";
    private const string EnvUsername = "NSCHEMA_SQLSERVER_USERNAME";
    private const string EnvPassword = "NSCHEMA_SQLSERVER_PASSWORD";

    /// <summary>The options a DATABASE statement binds onto.</summary>
    private sealed class SqlServerOptions
    {
        public string? ConnectionString { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public int? CommandTimeout { get; set; }
    }

    /// <inheritdoc />
    public BlockStatement GetScaffoldTemplate(ScaffoldContext context) =>
        new(BlockKeyword.Database, Identifier.Synthetic(Source), new SeparatedSyntaxList<BlockAttribute>(
        [
            new BlockAttribute("connection_string", string.Empty),
        ]))
        {
            DocComment = new Token(
                TokenKind.DocComment,
                $"Prefer the {EnvConnectionString} environment variable, which overrides the value below.\n" +
                $"Credentials may be supplied separately from the connection string (e.g. from a secret\n" +
                $"store) via {EnvUsername} / {EnvPassword}. They override any user/password embedded in\n" +
                "connection_string.",
                SourcePosition.None),
        };

    /// <inheritdoc />
    public string GetSampleSchema() =>
        """
        CREATE SCHEMA app;

        CREATE TABLE app.widgets (
          id   int NOT NULL,
          name varchar(100),
          CONSTRAINT widgets_pkey PRIMARY KEY (id)
        );
        """;

    /// <inheritdoc />
    public Result Configure(NSchemaApplicationBuilder builder, PluginSettings settings)
    {
        var bound = settings.Get<SqlServerOptions>();
        if (bound.Value is not { } options)
        {
            return Result.From(bound.Diagnostics);
        }

        var diagnostics = new List<Diagnostic>(bound.Diagnostics);

        // Credentials may be supplied out of band (e.g. a secret store); the environment overrides the statement.
        var connectionString = Environment.GetEnvironmentVariable(EnvConnectionString) ?? options.ConnectionString;
        var username = Environment.GetEnvironmentVariable(EnvUsername) ?? options.Username;
        var password = Environment.GetEnvironmentVariable(EnvPassword) ?? options.Password;

        if (string.IsNullOrEmpty(connectionString))
        {
            diagnostics.Add(Diagnostic.Error(Source,
                $"The SQL Server provider requires connection_string. Set it via the {EnvConnectionString} environment variable or the DATABASE statement."));
        }

        if (options.CommandTimeout is < 0)
        {
            diagnostics.Add(Diagnostic.Error(Source, "command_timeout must not be negative."));
        }

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return Result.From(diagnostics);
        }

        builder.UseSqlServer(connectionStringBuilder =>
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

            if (options.CommandTimeout is { } timeout)
            {
                connectionStringBuilder.CommandTimeout = timeout;
            }
        });

        return Result.From(diagnostics);
    }
}
