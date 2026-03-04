namespace DoTogether.Domain.Enums;

public enum MemberRole
{
    Admin = 0,
    Member = 1
}

public enum RecurrenceType
{
    Once = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3
}

public enum OccurrenceStatus
{
    Pending = 0,
    Completed = 1,
    Skipped = 2,
    Missed = 3
}

public enum ChoreEventType
{
    Created = 0,
    Completed = 1,
    Undone = 2,
    Skipped = 3,
    Reassigned = 4,
    Edited = 5,
    Missed = 6
}

public enum PushPlatform
{
    Fcm = 0,
    Apns = 1
}

public enum AchievementScope
{
    Me = 0,
    Partner = 1,
    Household = 2
}
