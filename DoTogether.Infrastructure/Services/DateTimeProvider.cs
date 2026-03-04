using DoTogether.Application.Interfaces;

namespace DoTogether.Infrastructure.Services;

public class DateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;

    public DateOnly TodayIn(string ianaTimeZoneId)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId);
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        return DateOnly.FromDateTime(now);
    }
}
