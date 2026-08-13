using NSchema.Diff.Plugins;
using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.Model.Sequences;

namespace NSchema.SqlServer.Sql;

/// <summary>
/// SQL Server equivalence rules so spellings the catalog and a project may disagree on compare as equal.
/// </summary>
public sealed class SqlServerSqlEquivalence : SqlEquivalence
{
    /// <inheritdoc/>
    /// <remarks>
    /// A type in <c>dbo</c> or <c>sys</c> is addressable bare, so the qualifier folds away;
    /// a type in any other schema keeps it. <c>text</c> folds onto the <c>varchar(max)</c> the dialect renders it
    /// as, which the catalog can only report back as an unbounded <c>varchar</c>.
    /// </remarks>
    public override IEqualityComparer<SqlType> Types { get; } = new TypeEquality();

    /// <inheritdoc/>
    /// <remarks>
    /// <c>sys.sequences</c> holds a concrete value for every option whatever was declared, so each one the engine
    /// would have chosen anyway folds back to <see langword="null"/>. SQL Server's bounds are the data type's own
    /// — a bare <c>bigint</c> sequence starts at <c>bigint</c>'s minimum, not at 1 — and the start follows the
    /// effective bound, so a declared <c>MINVALUE 1 START WITH 1</c> is asking for the default twice. That pairing
    /// is what never settled: the read side folded the start away, the declaring side did not, and the repair was
    /// an <c>ALTER SEQUENCE … RESTART WITH 1</c> that reset the live counter on every deploy.
    /// </remarks>
    public override SequenceOptions WithDefaults(SequenceOptions options) => FoldOptions(options);

    /// <inheritdoc cref="WithDefaults(SequenceOptions)"/>
    /// <remarks>Static so introspection folds a catalog row through the same rules the comparison uses.</remarks>
    internal static SequenceOptions FoldOptions(SequenceOptions options)
    {
        var (typeMin, typeMax) = TypeRange(options.DataType);
        var start = options.IncrementBy is null or > 0 ? options.MinValue ?? typeMin : options.MaxValue ?? typeMax;

        return new SequenceOptions(
            DataType: IsBigInt(options.DataType) ? null : options.DataType,
            StartWith: options.StartWith == start ? null : options.StartWith,
            IncrementBy: options.IncrementBy == 1 ? null : options.IncrementBy,
            MinValue: options.MinValue == typeMin ? null : options.MinValue,
            MaxValue: options.MaxValue == typeMax ? null : options.MaxValue,
            Cache: options.Cache,
            Cycle: options.Cycle);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>sys.identity_columns</c> reports a seed and an increment for every identity, so a column that asked only
    /// to be an <c>IDENTITY</c> read back carrying <c>IDENTITY(1, 1)</c> and differed from itself on every deploy.
    /// SQL Server has no identity minimum at all, which is the opposite of Postgres reporting one that was never
    /// asked for — folding each engine's own convention away is what lets a single declaration settle on both.
    /// </remarks>
    public override IdentityOptions WithDefaults(IdentityOptions options, SqlType columnType) => FoldOptions(options);

    /// <inheritdoc cref="WithDefaults(IdentityOptions, SqlType)"/>
    /// <remarks>Static so introspection folds a catalog row through the same rules the comparison uses.</remarks>
    internal static IdentityOptions FoldOptions(IdentityOptions options) => new(
        StartWith: options.StartWith == 1 ? null : options.StartWith,
        MinValue: options.MinValue,
        IncrementBy: options.IncrementBy == 1 ? null : options.IncrementBy,
        NotForReplication: options.NotForReplication);

    // A sequence's bounds are its data type's, in both directions — unlike Postgres, where an ascending sequence
    // starts at 1.
    private static (long Min, long Max) TypeRange(SqlType? dataType) => dataType?.Name.Value switch
    {
        "tinyint" => (byte.MinValue, byte.MaxValue),
        "smallint" => (short.MinValue, short.MaxValue),
        "int" => (int.MinValue, int.MaxValue),
        _ => (long.MinValue, long.MaxValue),
    };

    private static bool IsBigInt(SqlType? dataType) => dataType is null || dataType.Name.Value == "bigint";

    private sealed class TypeEquality : IEqualityComparer<SqlType>
    {
        public bool Equals(SqlType? x, SqlType? y) => object.Equals(Fold(x), Fold(y));

        public int GetHashCode(SqlType obj) => Fold(obj)!.GetHashCode();

        private static SqlType? Fold(SqlType? type)
        {
            if (type is null)
            {
                return null;
            }

            // SQL Server has no unbounded character type of its own: the dialect renders the canonical text as
            // varchar(max), which the catalog reports as a varchar of length -1 and so reads back as an unbounded
            // varchar. Without this the column was written correctly and then asked to change on every deploy.
            // A column of SQL Server's own deprecated `text` type also folds here — the two are the same name and
            // the model has no way to tell them apart — so an adopted one settles rather than drifting forever.
            var folded = type.Name.Value.ToLowerInvariant() is "text"
                ? type with { Name = new SqlIdentifier("varchar"), Length = null }
                : type;

            return folded.Schema?.Value is SqlServerSchemas.Provided or SqlServerSchemas.System
                ? folded with { Schema = null }
                : folded;
        }
    }
}
