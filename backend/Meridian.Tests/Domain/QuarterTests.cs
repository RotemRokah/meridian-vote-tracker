using Meridian.Domain.Scoring;

namespace Meridian.Tests.Domain;

public class QuarterTests
{
    [Theory]
    [InlineData("2024-01-01", "2024-Q1")]
    [InlineData("2024-03-31", "2024-Q1")]
    [InlineData("2024-04-01", "2024-Q2")]
    [InlineData("2024-09-30", "2024-Q3")]
    [InlineData("2024-12-31", "2024-Q4")]
    public void Date_maps_to_its_quarter(string date, string quarter) =>
        Assert.Equal(quarter, Quarter.Of(DateOnly.Parse(date)));

    [Theory]
    [InlineData("2024-Q1", true)]
    [InlineData("2024-Q4", true)]
    [InlineData("2024-Q5", false)]
    [InlineData("2024-q1", false)]
    [InlineData("24-Q1", false)]
    [InlineData(null, false)]
    public void Quarter_format_is_checked(string? quarter, bool valid) =>
        Assert.Equal(valid, Quarter.IsValid(quarter));

    [Fact]
    public void Common_quarter_only_when_all_dates_share_it()
    {
        Assert.Equal("2024-Q1", Quarter.CommonQuarter(new[] { new DateOnly(2024, 1, 12), new DateOnly(2024, 3, 5) }));
        Assert.Null(Quarter.CommonQuarter(new[] { new DateOnly(2024, 3, 31), new DateOnly(2024, 4, 1) }));
    }
}
