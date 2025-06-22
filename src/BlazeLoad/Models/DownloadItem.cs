using Downloader;

namespace BlazeLoad.Models;

public class DownloadItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string SourceUrl { get; set; } = string.Empty;
    public string? FileName { get; set; } = string.Empty;
    public string? TargetDirectory { get; set; }

    public long TotalBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public double SpeedBytesPerSec { get; set; }
    public TimeSpan? TimeRemaining { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Created;

    /* --------- Convenience ---------- */
    public double ProgressPercent => TotalBytes == 0 ? 0 : ReceivedBytes * 100d / TotalBytes;
    public string SizeFormatted => ByteFormat(TotalBytes);
    public string ProgressFormatted => $"{ProgressPercent:0}% / {ByteFormat(ReceivedBytes)}";

    public string Info => TimeRemaining is null
        ? "—"
        : $"{TimeRemaining:hh\\:mm\\:ss} @ {ByteFormat(SpeedBytesPerSec)}/s";

    public static string ByteFormat(double bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        var pow = bytes == 0 ? 0 : (int)Math.Floor(Math.Log(bytes, 1024));
        pow = Math.Clamp(pow, 0, units.Length - 1);
        return $"{bytes / Math.Pow(1024, pow):0.##} {units[pow]}";
    }
}