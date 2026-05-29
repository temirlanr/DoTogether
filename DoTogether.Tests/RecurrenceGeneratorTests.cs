using DoTogether.Application.Services;
using DoTogether.Domain.Enums;
using DoTogether.Domain.ValueObjects;

namespace DoTogether.Tests;

public class RecurrenceGeneratorTests
{
    // ── Daily ──

    [Fact]
    public void Daily_BasicInterval1_ReturnsEveryDay()
    {
        var rule = new RecurrenceRule { Type = RecurrenceType.Daily, Interval = 1 };
        var start = new DateOnly(2025, 1, 1);

        var result = RecurrenceGenerator.Generate(rule, start, start, new DateOnly(2025, 1, 5));

        Assert.Equal(5, result.Count);
        Assert.Equal(new DateOnly(2025, 1, 1), result[0]);
        Assert.Equal(new DateOnly(2025, 1, 5), result[4]);
    }

    [Fact]
    public void Daily_Interval3_SkipsDays()
    {
        var rule = new RecurrenceRule { Type = RecurrenceType.Daily, Interval = 3 };
        var start = new DateOnly(2025, 1, 1);

        var result = RecurrenceGenerator.Generate(rule, start, start, new DateOnly(2025, 1, 10));

        Assert.Equal([
            new DateOnly(2025, 1, 1),
            new DateOnly(2025, 1, 4),
            new DateOnly(2025, 1, 7),
            new DateOnly(2025, 1, 10)
        ], result);
    }

    [Fact]
    public void Daily_RangeStartAfterTemplateStart_FastForwards()
    {
        var rule = new RecurrenceRule { Type = RecurrenceType.Daily, Interval = 2 };
        var templateStart = new DateOnly(2025, 1, 1);
        var rangeStart = new DateOnly(2025, 3, 1);
        var rangeEnd = new DateOnly(2025, 3, 5);

        var result = RecurrenceGenerator.Generate(rule, templateStart, rangeStart, rangeEnd);

        // Jan 1 + 2*N days. Day-number diff to Mar 1 = 59 (odd), so Mar 1 is NOT hit.
        // Schedule hits: Feb 28 (day 58), Mar 2 (day 60), Mar 4 (day 62).
        Assert.All(result, d => Assert.True(d >= rangeStart && d <= rangeEnd));
        Assert.Contains(new DateOnly(2025, 3, 2), result);
        Assert.Contains(new DateOnly(2025, 3, 4), result);
        Assert.DoesNotContain(new DateOnly(2025, 3, 1), result);
    }

    // ── Weekly ──

    [Fact]
    public void Weekly_MondayAndFriday_ReturnsCorrectDays()
    {
        var rule = new RecurrenceRule
        {
            Type = RecurrenceType.Weekly,
            Interval = 1,
            DaysOfWeek = [1, 5] // Mon, Fri
        };
        // 2025-01-06 is a Monday
        var start = new DateOnly(2025, 1, 6);

        var result = RecurrenceGenerator.Generate(rule, start, start, new DateOnly(2025, 1, 19));

        Assert.All(result, d =>
            Assert.True(d.DayOfWeek == DayOfWeek.Monday || d.DayOfWeek == DayOfWeek.Friday));
        Assert.Contains(new DateOnly(2025, 1, 6), result);  // Mon
        Assert.Contains(new DateOnly(2025, 1, 10), result); // Fri
        Assert.Contains(new DateOnly(2025, 1, 13), result); // Mon
        Assert.Contains(new DateOnly(2025, 1, 17), result); // Fri
    }

    [Fact]
    public void Weekly_EveryOtherWeek_SkipsWeeks()
    {
        var rule = new RecurrenceRule
        {
            Type = RecurrenceType.Weekly,
            Interval = 2,
            DaysOfWeek = [3] // Wednesday
        };
        // 2025-01-01 is a Wednesday
        var start = new DateOnly(2025, 1, 1);

        var result = RecurrenceGenerator.Generate(rule, start, start, new DateOnly(2025, 2, 28));

        // Every other Wednesday: Jan 1, Jan 15, Jan 29, Feb 12, Feb 26
        Assert.All(result, d => Assert.Equal(DayOfWeek.Wednesday, d.DayOfWeek));
        Assert.Contains(new DateOnly(2025, 1, 1), result);
        Assert.Contains(new DateOnly(2025, 1, 15), result);
        Assert.DoesNotContain(new DateOnly(2025, 1, 8), result);
    }

    [Fact]
    public void Weekly_SundayIso7_ReturnsSundays()
    {
        var rule = new RecurrenceRule
        {
            Type = RecurrenceType.Weekly,
            Interval = 1,
            DaysOfWeek = [7]
        };
        var start = new DateOnly(2025, 1, 5);

        var result = RecurrenceGenerator.Generate(rule, start, start, new DateOnly(2025, 1, 18));

        Assert.Equal([
            new DateOnly(2025, 1, 5),
            new DateOnly(2025, 1, 12)
        ], result);
        Assert.All(result, date => Assert.Equal(DayOfWeek.Sunday, date.DayOfWeek));
    }

    [Fact]
    public void Weekly_NoDaysOfWeek_Throws()
    {
        var rule = new RecurrenceRule { Type = RecurrenceType.Weekly, Interval = 1, DaysOfWeek = [] };
        var start = new DateOnly(2025, 1, 1);

        Assert.Throws<ArgumentException>(() =>
            RecurrenceGenerator.Generate(rule, start, start, new DateOnly(2025, 1, 31)));
    }

    // ── Monthly ──

    [Fact]
    public void Monthly_Day15_ReturnsCorrectDates()
    {
        var rule = new RecurrenceRule { Type = RecurrenceType.Monthly, Interval = 1, DayOfMonth = 15 };
        var start = new DateOnly(2025, 1, 15);

        var result = RecurrenceGenerator.Generate(rule, start, start, new DateOnly(2025, 6, 30));

        Assert.Equal(6, result.Count);
        Assert.All(result, d => Assert.Equal(15, d.Day));
    }

    [Fact]
    public void Monthly_Day31_ClampsToLastDay()
    {
        var rule = new RecurrenceRule { Type = RecurrenceType.Monthly, Interval = 1, DayOfMonth = 31 };
        var start = new DateOnly(2025, 1, 31);

        var result = RecurrenceGenerator.Generate(rule, start, start, new DateOnly(2025, 12, 31));

        // Jan 31, Feb 28, Mar 31, Apr 30, May 31, Jun 30, Jul 31, Aug 31, Sep 30, Oct 31, Nov 30, Dec 31
        Assert.Equal(12, result.Count);
        Assert.Equal(new DateOnly(2025, 2, 28), result[1]);   // Feb clamped to 28
        Assert.Equal(new DateOnly(2025, 4, 30), result[3]);   // Apr clamped to 30
        Assert.Equal(new DateOnly(2025, 6, 30), result[5]);   // Jun clamped to 30
        Assert.Equal(new DateOnly(2025, 9, 30), result[8]);   // Sep clamped to 30
        Assert.Equal(new DateOnly(2025, 11, 30), result[10]); // Nov clamped to 30
    }

    [Fact]
    public void Monthly_Day29_LeapYear_ProducesFeb29()
    {
        var rule = new RecurrenceRule { Type = RecurrenceType.Monthly, Interval = 1, DayOfMonth = 29 };
        var start = new DateOnly(2024, 1, 29);

        var result = RecurrenceGenerator.Generate(rule, start, start, new DateOnly(2024, 3, 31));

        // Jan 29, Feb 29 (2024 is leap), Mar 29
        Assert.Equal(3, result.Count);
        Assert.Equal(new DateOnly(2024, 2, 29), result[1]);
    }

    [Fact]
    public void Monthly_Day29_NonLeapYear_ClampsToFeb28()
    {
        var rule = new RecurrenceRule { Type = RecurrenceType.Monthly, Interval = 1, DayOfMonth = 29 };
        var start = new DateOnly(2025, 1, 29);

        var result = RecurrenceGenerator.Generate(rule, start, start, new DateOnly(2025, 3, 31));

        // Jan 29, Feb 28 (clamped), Mar 29
        Assert.Equal(3, result.Count);
        Assert.Equal(new DateOnly(2025, 2, 28), result[1]);
    }

    [Fact]
    public void Monthly_Day30_February_ClampsCorrectly()
    {
        var rule = new RecurrenceRule { Type = RecurrenceType.Monthly, Interval = 1, DayOfMonth = 30 };
        var start = new DateOnly(2025, 1, 30);

        var result = RecurrenceGenerator.Generate(rule, start, start, new DateOnly(2025, 4, 30));

        Assert.Equal(new DateOnly(2025, 2, 28), result[1]); // Feb clamped to 28
        Assert.Equal(new DateOnly(2025, 3, 30), result[2]);
    }

    [Fact]
    public void Monthly_EveryOtherMonth_SkipsMonths()
    {
        var rule = new RecurrenceRule { Type = RecurrenceType.Monthly, Interval = 2, DayOfMonth = 1 };
        var start = new DateOnly(2025, 1, 1);

        var result = RecurrenceGenerator.Generate(rule, start, start, new DateOnly(2025, 12, 31));

        // Jan 1, Mar 1, May 1, Jul 1, Sep 1, Nov 1
        Assert.Equal(6, result.Count);
        Assert.Equal(new DateOnly(2025, 1, 1), result[0]);
        Assert.Equal(new DateOnly(2025, 3, 1), result[1]);
        Assert.Equal(new DateOnly(2025, 5, 1), result[2]);
    }

    // ── Once ──

    [Fact]
    public void Once_InRange_ReturnsSingleDate()
    {
        var rule = new RecurrenceRule { Type = RecurrenceType.Once };
        var start = new DateOnly(2025, 3, 15);

        var result = RecurrenceGenerator.Generate(rule, start, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));

        Assert.Single(result);
        Assert.Equal(start, result[0]);
    }

    [Fact]
    public void Once_OutOfRange_ReturnsEmpty()
    {
        var rule = new RecurrenceRule { Type = RecurrenceType.Once };
        var start = new DateOnly(2025, 3, 15);

        var result = RecurrenceGenerator.Generate(rule, start, new DateOnly(2025, 4, 1), new DateOnly(2025, 12, 31));

        Assert.Empty(result);
    }

    // ── Edge cases ──

    [Fact]
    public void RangeStartAfterRangeEnd_Throws()
    {
        var rule = new RecurrenceRule { Type = RecurrenceType.Daily, Interval = 1 };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RecurrenceGenerator.Generate(rule, new DateOnly(2025, 1, 1),
                new DateOnly(2025, 3, 1), new DateOnly(2025, 1, 1)));
    }

    [Fact]
    public void IntervalZero_Throws()
    {
        var rule = new RecurrenceRule { Type = RecurrenceType.Daily, Interval = 0 };

        Assert.Throws<ArgumentException>(() =>
            RecurrenceGenerator.Generate(rule, new DateOnly(2025, 1, 1),
                new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 31)));
    }

    [Fact]
    public void ClampToMonth_VerifiesCorrectly()
    {
        Assert.Equal(new DateOnly(2025, 2, 28), RecurrenceGenerator.ClampToMonth(2025, 2, 31));
        Assert.Equal(new DateOnly(2024, 2, 29), RecurrenceGenerator.ClampToMonth(2024, 2, 31));
        Assert.Equal(new DateOnly(2025, 4, 30), RecurrenceGenerator.ClampToMonth(2025, 4, 31));
        Assert.Equal(new DateOnly(2025, 1, 31), RecurrenceGenerator.ClampToMonth(2025, 1, 31));
        Assert.Equal(new DateOnly(2025, 1, 15), RecurrenceGenerator.ClampToMonth(2025, 1, 15));
    }

    [Fact]
    public void Daily_CrossesDSTBoundary_StillProducesCorrectDates()
    {
        // March 9, 2025 is US DST spring-forward. Since we use DateOnly, no ambiguity.
        var rule = new RecurrenceRule { Type = RecurrenceType.Daily, Interval = 1 };
        var start = new DateOnly(2025, 3, 8);

        var result = RecurrenceGenerator.Generate(rule, start, start, new DateOnly(2025, 3, 11));

        Assert.Equal([
            new DateOnly(2025, 3, 8),
            new DateOnly(2025, 3, 9),  // DST transition day
            new DateOnly(2025, 3, 10),
            new DateOnly(2025, 3, 11)
        ], result);
    }

    [Fact]
    public void Monthly_CrossesYearBoundary_Works()
    {
        var rule = new RecurrenceRule { Type = RecurrenceType.Monthly, Interval = 1, DayOfMonth = 15 };
        var start = new DateOnly(2024, 11, 15);

        var result = RecurrenceGenerator.Generate(rule, start, start, new DateOnly(2025, 3, 15));

        Assert.Equal(5, result.Count);
        Assert.Equal(new DateOnly(2024, 11, 15), result[0]);
        Assert.Equal(new DateOnly(2024, 12, 15), result[1]);
        Assert.Equal(new DateOnly(2025, 1, 15), result[2]);
        Assert.Equal(new DateOnly(2025, 2, 15), result[3]);
        Assert.Equal(new DateOnly(2025, 3, 15), result[4]);
    }

    [Fact]
    public void Weekly_Sunday_IsoDay7()
    {
        var rule = new RecurrenceRule
        {
            Type = RecurrenceType.Weekly,
            Interval = 1,
            DaysOfWeek = [7] // Sunday
        };
        // 2025-01-05 is a Sunday
        var start = new DateOnly(2025, 1, 5);

        var result = RecurrenceGenerator.Generate(rule, start, start, new DateOnly(2025, 1, 26));

        Assert.Equal(4, result.Count);
        Assert.All(result, d => Assert.Equal(DayOfWeek.Sunday, d.DayOfWeek));
    }

    [Fact]
    public void Weekly_InvalidIsoDay_Throws()
    {
        var rule = new RecurrenceRule
        {
            Type = RecurrenceType.Weekly,
            Interval = 1,
            DaysOfWeek = [8] // Invalid
        };
        var start = new DateOnly(2025, 1, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RecurrenceGenerator.Generate(rule, start, start, new DateOnly(2025, 1, 31)));
    }
}
