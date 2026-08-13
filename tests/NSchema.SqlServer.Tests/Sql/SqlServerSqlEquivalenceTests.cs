using NSchema.Model.Columns;
using NSchema.Model.Sequences;
using NSchema.SqlServer.Sql;

namespace NSchema.SqlServer.Tests.Sql;

public sealed class SqlServerSqlEquivalenceTests
{
    private readonly SqlServerSqlEquivalence _sut = new();

    [Theory]
    [InlineData("dbo")]
    [InlineData("sys")]
    public void Types_FoldsAProvidedSchemaQualifier(string schema)
    {
        // Act — a type in a schema SQL Server provides is addressable bare, so the spellings are one type.
        var equal = _sut.Types.Equals(SqlType.Custom(schema, "money_amount"), SqlType.Custom("money_amount"));

        // Assert
        equal.ShouldBeTrue();
        _sut.Types.GetHashCode(SqlType.Custom(schema, "money_amount"))
            .ShouldBe(_sut.Types.GetHashCode(SqlType.Custom("money_amount")));
    }

    [Fact]
    public void Types_KeepsAUserSchemaQualifier()
    {
        // Act — app.money_amount and bare money_amount may be different types; under-normalize.
        var equal = _sut.Types.Equals(SqlType.Custom("app", "money_amount"), SqlType.Custom("money_amount"));

        // Assert
        equal.ShouldBeFalse();
    }

    [Fact]
    public void Types_FoldsTextOntoTheVarcharItIsRenderedAs()
    {
        // Act — the dialect writes text as varchar(max), which the catalog can only report as an unbounded
        // varchar, so without this the column was written correctly and asked to change on every deploy.
        var equal = _sut.Types.Equals(SqlType.Text, SqlType.VarChar());

        // Assert
        equal.ShouldBeTrue();
        _sut.Types.GetHashCode(SqlType.Text).ShouldBe(_sut.Types.GetHashCode(SqlType.VarChar()));
    }

    [Fact]
    public void Types_KeepsTextDistinctFromABoundedVarchar()
        // varchar(200) is a real ceiling, not the max the dialect renders text as.
        => _sut.Types.Equals(SqlType.Text, SqlType.VarChar(200)).ShouldBeFalse();

    [Fact]
    public void Types_KeepsTextDistinctFromAnUnboundedNvarchar()
        // Unicode is a difference the plan can and should act on.
        => _sut.Types.Equals(SqlType.Text, SqlType.NVarChar()).ShouldBeFalse();

    // ── Sequence options ──────────────────────────────────────────────────────

    [Fact]
    public void WithDefaults_SequenceDeclaringTheEngineDefaults_FoldsToNothingDeclared()
        // A bare bigint sequence starts at bigint's minimum, not at 1 — SQL Server's bounds are the type's own.
        => _sut.WithDefaults(new SequenceOptions(
                DataType: SqlType.BigInt, StartWith: long.MinValue, IncrementBy: 1, MinValue: long.MinValue, MaxValue: long.MaxValue))
            .ShouldBe(new SequenceOptions());

    [Fact]
    public void WithDefaults_SequenceStartFollowingADeclaredMinimum_FoldsTheStartOnly()
        // The pairing that never settled: the read side folded the start away and the declaring side did not, so
        // every deploy repaired it with a RESTART WITH that reset the live counter.
        => _sut.WithDefaults(new SequenceOptions(StartWith: 1, MinValue: 1)).ShouldBe(new SequenceOptions(MinValue: 1));

    [Fact]
    public void WithDefaults_SequenceStartWithNoDeclaredMinimum_IsKept()
        // Without a minimum the default start is bigint's own, so a start of 1 is a real instruction.
        => _sut.WithDefaults(new SequenceOptions(StartWith: 1)).ShouldBe(new SequenceOptions(StartWith: 1));

    [Fact]
    public void WithDefaults_SequenceOptionsThatDifferFromTheDefaults_AreKept()
        => _sut.WithDefaults(new SequenceOptions(SqlType.Int, StartWith: 20, IncrementBy: 5, MinValue: 10, MaxValue: 1000, Cache: 10, Cycle: true))
            .ShouldBe(new SequenceOptions(SqlType.Int, StartWith: 20, IncrementBy: 5, MinValue: 10, MaxValue: 1000, Cache: 10, Cycle: true));

    [Fact]
    public void WithDefaults_SequenceBoundsFollowTheDeclaredType()
        => _sut.WithDefaults(new SequenceOptions(SqlType.Int, StartWith: int.MinValue, MinValue: int.MinValue, MaxValue: int.MaxValue))
            .ShouldBe(new SequenceOptions(SqlType.Int));

    // ── Identity options ──────────────────────────────────────────────────────

    [Fact]
    public void WithDefaults_IdentityDeclaringTheEngineDefaults_FoldsToNothingDeclared()
        // sys.identity_columns reports IDENTITY(1, 1) for a column that asked only to be an identity.
        => _sut.WithDefaults(new IdentityOptions(StartWith: 1, MinValue: null, IncrementBy: 1), SqlType.Int)
            .ShouldBe(new IdentityOptions(null, null, null));

    [Fact]
    public void WithDefaults_IdentityOptionsThatDifferFromTheDefaults_AreKept()
        => _sut.WithDefaults(new IdentityOptions(StartWith: 1000, MinValue: null, IncrementBy: 5), SqlType.BigInt)
            .ShouldBe(new IdentityOptions(1000, null, 5));

    [Fact]
    public void WithDefaults_IdentityKeepsNotForReplication()
        => _sut.WithDefaults(new IdentityOptions(1, null, 1, NotForReplication: true), SqlType.Int)
            .NotForReplication.ShouldBeTrue();
}
