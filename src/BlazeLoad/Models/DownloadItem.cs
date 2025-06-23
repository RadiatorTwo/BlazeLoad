namespace BlazeLoad.Models;

public enum DownloadState
{
    Waiting,
    Downloading,
    Paused,
    Stopped,
    Error,
    Complete
}

public sealed class DownloadItem
{
    public required string Id { get; init; }
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public long Total { get; set; }
    public long Done { get; set; }
    public long Speed { get; set; }
    public DownloadState State { get; set; }
    public double Percent => Total > 0 ? Done * 100d / Total : 0;
    public TimeSpan? TimeRemaining { get; set; }

    /* --------- Convenience ---------- */
    public double ProgressPercent => Total == 0 ? 0 : Done * 100d / Total;
    public string SizeFormatted => ByteFormat(Total);
    public string ProgressFormatted => $"{ProgressPercent:0}% / {ByteFormat(Done)}";


    public string Info => TimeRemaining is null
        ? $"{ByteFormat(Speed)}/s"
        : $"{TimeRemaining:hh\\:mm\\:ss} @ {ByteFormat(Speed)}/s";

    public static string ByteFormat(double bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        var pow = bytes == 0 ? 0 : (int)Math.Floor(Math.Log(bytes, 1024));
        pow = Math.Clamp(pow, 0, units.Length - 1);
        return $"{bytes / Math.Pow(1024, pow):0.##} {units[pow]}";
    }
}