namespace DoTogether.Application.Interfaces;

public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
    DateOnly TodayIn(string ianaTimeZoneId);
}
