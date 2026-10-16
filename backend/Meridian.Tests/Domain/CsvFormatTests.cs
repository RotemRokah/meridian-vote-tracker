using Meridian.Domain.Ingestion;

namespace Meridian.Tests.Domain;

public class CsvFormatTests
{
    [Fact]
    public void Header_is_accepted()
    {
        var check = CsvFormat.CheckHeader(TestData.Header);
        Assert.True(check.IsValid);
        Assert.False(check.HadBom);
    }

    [Fact]
    public void Header_with_BOM_is_accepted_and_the_BOM_is_reported()
    {
        // assembly_export_2024_q1.csv starts with a UTF-8 BOM
        var check = CsvFormat.CheckHeader("﻿" + TestData.Header);
        Assert.True(check.IsValid);
        Assert.True(check.HadBom);
    }

    [Fact]
    public void Wrong_header_fails_the_file()
    {
        var check = CsvFormat.CheckHeader("vote,country,ballot");
        Assert.False(check.IsValid);
        Assert.Contains("Header must be", check.Error);
    }

    [Fact]
    public void Quoted_field_keeps_commas_and_escaped_quotes()
    {
        // real line from q1 (vote MA/2024/036)
        var line = "MA/2024/036,79,2024-03-05,\"Follow-up to the \"\"Blue Ocean\"\" Declaration, Including Capacity Building\",AFG,Afghanistan,X,2024-04-02T09:00:00Z";

        Assert.True(CsvFormat.TrySplit(line, out var fields, out _));
        Assert.Equal(8, fields.Count);
        Assert.Equal("Follow-up to the \"Blue Ocean\" Declaration, Including Capacity Building", fields[3]);
    }

    [Fact]
    public void Empty_fields_are_kept()
    {
        Assert.True(CsvFormat.TrySplit("a,,c,", out var fields, out _));
        Assert.Equal(new[] { "a", "", "c", "" }, fields);
    }

    [Theory]
    [InlineData("a,\"unterminated,c", "Unterminated")]
    [InlineData("a,\"quoted\"junk,c", "after closing quote")]
    [InlineData("a,un\"quoted,c", "Unexpected quote")]
    public void Broken_quoting_is_an_error(string line, string expectedError)
    {
        Assert.False(CsvFormat.TrySplit(line, out _, out var error));
        Assert.Contains(expectedError, error);
    }
}
