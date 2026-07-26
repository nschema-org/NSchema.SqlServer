using System.ComponentModel.DataAnnotations;
using Microsoft.Data.SqlClient;
using NSchema.Configuration.Plugins;
using NSchema.Plugins;
using NSchema.Project.Nsql.Syntax.Settings;

namespace NSchema.SqlServer;

/// <summary>
/// The NSchema plugin manifest for the SQL Server provider.
/// </summary>
public sealed class SqlServerPlugin : INSchemaDatabasePlugin
{
    private const string Source = "sqlserver";
    private const string Integrated = "Integrated";

    /// <summary>The options a DATABASE statement binds onto.</summary>
    private sealed class SqlServerOptions
    {
        [Required(ErrorMessage = "DATABASE sqlserver: connection_string is required. Set it in the statement, or supply NSCHEMA_DATABASE_CONNECTION_STRING.")]
        public string? ConnectionString { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public int? CommandTimeout { get; set; }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The parts of a connection string, rather than the string itself: they are what an operator knows offhand. The
    /// password is deliberately absent — it belongs in NSCHEMA_DATABASE_PASSWORD, not in a committed file.
    /// </remarks>
    public IReadOnlyList<ScaffoldPrompt> GetScaffoldPrompts(ScaffoldContext context) =>
    [
        new() { Key = "server", Label = "Server", Default = "localhost" },
        new() { Key = "database", Label = "Database", Default = "master" },
        new() { Key = "authentication", Label = "Authentication", Default = "Integrated", Choices = ["Integrated", "SQL login"] },
        new() { Key = "username", Label = "Username", Default = "sa" },
    ];

    /// <inheritdoc />
    public SettingsStatement GetScaffoldTemplate(ScaffoldContext context) =>
        SettingsStatement.Database(Source)
            .WithSetting("connection_string", ConnectionString(context))
            .WithDocComment(
                "Prefer the NSCHEMA_DATABASE_CONNECTION_STRING environment variable, which overrides the value below.\n"
                + "Credentials may be supplied separately from the connection string (e.g. from a secret\n"
                + "store) via NSCHEMA_DATABASE_USERNAME / NSCHEMA_DATABASE_PASSWORD. They override any user/password\n"
                + "connection_string.");

    // Nothing answered leaves the setting blank, which is the placeholder a user edits by hand.
    private static string ConnectionString(ScaffoldContext context)
    {
        if (context.Answers.Count == 0)
        {
            return string.Empty;
        }

        var integrated = context.Answer("authentication", Integrated) == Integrated;
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = context.Answer("server", "localhost"),
            InitialCatalog = context.Answer("database", "master"),
            IntegratedSecurity = integrated,
        };

        if (!integrated)
        {
            builder.UserID = context.Answer("username", "sa");
        }

        return builder.ConnectionString;
    }

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

        // The engine has already applied any NSCHEMA_DATABASE_* override, so the bound values are final.
        var connectionString = options.ConnectionString;
        var username = options.Username;
        var password = options.Password;

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
