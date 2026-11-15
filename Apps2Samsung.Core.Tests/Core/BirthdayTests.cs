using Apps2Samsung.Helpers.Core;

namespace Apps2Samsung.Tests.Core;

/// <summary>The birthday line shows on 13 May of every year after 2025, and on no other day.</summary>
public class BirthdayTests
{
    [Theory]
    [InlineData(2026, 1)]
    [InlineData(2027, 2)]
    [InlineData(2035, 10)]
    public void The_thirteenth_of_May_is_a_birthday(int year, int age) =>
        Assert.Equal(age, Birthday.AgeOn(new DateOnly(year, 5, 13)));

    [Theory]
    [InlineData(2025, 5, 13)] // the day of the first release is not a birthday yet
    [InlineData(2024, 5, 13)]
    [InlineData(2027, 5, 12)]
    [InlineData(2027, 5, 14)]
    [InlineData(2027, 6, 13)]
    [InlineData(2026, 11, 15)]
    public void Any_other_day_is_not(int year, int month, int day) =>
        Assert.Null(Birthday.AgeOn(new DateOnly(year, month, day)));
}
