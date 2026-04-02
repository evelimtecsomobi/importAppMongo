internal static class BrazilTimeZone
{
    private static readonly TimeZoneInfo Tz = GetTz();

    private static TimeZoneInfo GetTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time"); }
    }

    public static DateTime ToUtc(DateTime brazilLocal)
    {
        var local = DateTime.SpecifyKind(brazilLocal, DateTimeKind.Unspecified);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(local, Tz), DateTimeKind.Utc);
    }

    public static DateTime FromUtc(DateTime utc)
    {
        var u = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var brazil = TimeZoneInfo.ConvertTimeFromUtc(u, Tz);
        return DateTime.SpecifyKind(brazil, DateTimeKind.Utc);
    }
}
