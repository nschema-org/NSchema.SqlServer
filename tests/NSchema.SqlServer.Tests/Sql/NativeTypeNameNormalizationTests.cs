using NSchema.Model;
using NSchema.SqlServer.Sql;

namespace NSchema.SqlServer.Tests.Sql;

public sealed class NativeTypeNameNormalizationTests
{
    [Theory]
    [InlineData("bit", "boolean")]
    [InlineData("tinyint", "tinyint")]
    [InlineData("int", "int")]
    [InlineData("bigint", "bigint")]
    [InlineData("real", "float")]
    [InlineData("float", "double")]
    [InlineData("numeric", "decimal")]
    [InlineData("decimal", "decimal")]
    [InlineData("datetime2", "datetime")]
    [InlineData("datetimeoffset", "datetimeoffset")]
    [InlineData("uniqueidentifier", "guid")]
    [InlineData("nvarchar", "nvarchar")]
    [InlineData("varbinary", "varbinary")]
    [InlineData("money", "money")]
    [InlineData("xml", "xml")]
    public void NormalizeNativeTypeName_YieldsTheModelsCanonicalSpelling(string typeName, string expected) =>
        SqlServerDatabaseIntrospector.NormalizeNativeTypeName(typeName).ShouldBe(new SqlIdentifier(expected));
}
