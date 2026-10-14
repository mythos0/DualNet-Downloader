using System.Globalization;

namespace DualNet.App.ViewModels;

/// <summary>Human-readable byte/speed formatting.</summary>
public static class Formatter
{
    public static string Bytes(long bytes)
    {
        double b = bytes;
        string unit = b switch
        {
            >= 1L << 30 => "GB",
            >= 1L << 20 => "MB",
            >= 1L << 10 => "KB",
            _ => "B"
        };
        return unit switch
        {
            "GB" => (b / (1L << 30)).ToString("0.00", CultureInfo.InvariantCulture) + " GB",
            "MB" => (b / (1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
            "KB" => (b / (1L << 10)).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
            _ => bytes.ToString("0", CultureInfo.InvariantCulture) + " B"
        };
    }

    public static string Bytes(double bytes) => Bytes((long)Math.Round(bytes));

    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "0 B/s";
        string unit = bytesPerSecond switch
        {
            >= 1L << 30 => "GB/s",
            >= 1L << 20 => "MB/s",
            >= 1L << 10 => "KB/s",
            _ => "B/s"
        };
        double v = bytesPerSecond;
        double scaled = unit switch
        {
            "GB/s" => v / (1L << 30),
            "MB/s" => v / (1L << 20),
            "KB/s" => v / (1L << 10),
            _ => v
        };
        return scaled.ToString(scaled >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + unit;
    }

    public static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var diff = DateTime.Now - local;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes} min ago";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours} h ago";
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
