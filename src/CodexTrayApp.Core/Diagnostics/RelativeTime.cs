using System.Globalization;

namespace CodexTrayApp.Core.Diagnostics;

/// <summary>"just now", "3 min ago", "2 h ago", "yesterday": coarse on purpose so the footer does not tick every second.</summary>
public static class RelativeTime
{
    public static string Format(DateTimeOffset when, DateTimeOffset now)
    {
        var elapsed = now - when;
        if (elapsed < TimeSpan.FromSeconds(45))
        {
            return "just now";
        }

        if (elapsed < TimeSpan.FromSeconds(90))
        {
            return "1 min ago";
        }

        if (elapsed < TimeSpan.FromMinutes(59.5))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(elapsed.TotalMinutes, MidpointRounding.AwayFromZero)} min ago");
        }

        if (elapsed < TimeSpan.FromMinutes(90))
        {
            return "1 h ago";
        }

        if (elapsed < TimeSpan.FromHours(23.5))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(elapsed.TotalHours, MidpointRounding.AwayFromZero)} h ago");
        }

        if (elapsed < TimeSpan.FromHours(48))
        {
            return "yesterday";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(elapsed.TotalDays)} days ago");
    }
}
