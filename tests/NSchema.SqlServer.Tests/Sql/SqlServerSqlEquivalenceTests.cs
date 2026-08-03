using NSchema.Model.Columns;
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
}
