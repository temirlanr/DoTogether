namespace DoTogether.Infrastructure.Data;

/// <summary>
/// Converts a PostgreSQL URL (postgresql://user:pass@host:port/db) to an
/// Npgsql connection string. Railway always provides DATABASE_URL in this format.
/// SSL is required for Railway's managed Postgres; the port defaults to 5432
/// when the URL omits it.
/// </summary>
public static class DatabaseUrl
{
    public static string? ToNpgsqlConnectionString(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 5432;
        var database = uri.AbsolutePath.TrimStart('/');
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";

        return $"Host={host};Port={port};Database={database};Username={username};Password={password};" +
               "SSL Mode=Require;Trust Server Certificate=true";
    }
}
