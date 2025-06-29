using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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
    /* ------------- Persistente Felder ------------------ */

    /// <summary>
    /// Primärschlüssel in der lokalen Datenbank – unabhängig vom Backend.
    /// </summary>
    [Key]
    public Guid Id { get; init; } = Guid.Empty;

    /// <summary>
    /// Die vom eigentlichen Download-Backend (z. B. aria2) vergebene ID.
    /// Darf leer sein, solange der Job noch nicht an das Backend übergeben
    /// wurde. Wird spätestens nach dem Hinzufügen gesetzt.
    /// </summary>
    
    [MaxLength(64)]
    public string BackendId { get; set; } = string.Empty;

    [MaxLength(1024)]
    public required string Url { get; set; }

    /// <summary>Ausgabedatei inklusive Erweiterung, ohne Pfad.</summary>
    [MaxLength(1024)]
    public string? Name { get; set; }

    /// <summary>Zielverzeichnis. Muss im Backend existieren.</summary>
    [MaxLength(1024)]
    public string? TargetDirectory { get; set; }

    /// <summary>Anzahl gleichzeitiger Verbindungen pro Server.</summary>
    public int Connections { get; set; } = 8;

    public bool PausedDueToDisconnect { get; set; }

    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    
    [MaxLength(1024)]
    public string? LocalFilePath { get; set; }
    
    public DownloadState State { get; set; } = DownloadState.Waiting;

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Letzte Fehlermeldung (falls State == Error).</summary>
    [MaxLength(1024)]
    public string? ErrorMessage { get; set; }

    /* ------------- Convenience / UI-Only ---------------- */
    
    [NotMapped]
    public long SpeedBytesPerSec { get; set; }
    
    [NotMapped]
    public double ProgressPercent => TotalBytes == 0 ? 0 : DownloadedBytes * 100d / TotalBytes;

    [NotMapped]
    public TimeSpan? TimeRemaining =>
        SpeedBytesPerSec > 0 && TotalBytes > 0
            ? TimeSpan.FromSeconds((TotalBytes - DownloadedBytes) / (double)SpeedBytesPerSec)
            : null;

    [NotMapped]
    public string SizeFormatted => ByteFormat(TotalBytes);

    [NotMapped]
    public string ProgressFormatted => $"{ProgressPercent:0}% / {ByteFormat(DownloadedBytes)}";

    [NotMapped]
    public string Info => TimeRemaining is null
        ? $"{ByteFormat(SpeedBytesPerSec)}/s"
        : $"{TimeRemaining:hh\\:mm\\:ss} @ {ByteFormat(SpeedBytesPerSec)}/s";

    [NotMapped]
    public long Total { get; set; }
    [NotMapped]
    public long Done { get; set; }
    [NotMapped]
    public long Speed { get; set; }

    /* ------------- Helper ------------------------------- */

    /// <summary>Wandelt Bytes in B, KiB, MiB … (binär) um.</summary>
    public static string ByteFormat(double bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        var pow = bytes > 0 ? (int)Math.Floor(Math.Log(bytes, 1024)) : 0;
        pow = Math.Clamp(pow, 0, units.Length - 1);
        return $"{bytes / Math.Pow(1024, pow):0.##} {units[pow]}";
    }
    
    public bool Equals(DownloadItem? other)
        => other is not null && Id.Equals(other.Id);

    public override bool Equals(object? obj)
        => Equals(obj as DownloadItem);

    public override int GetHashCode()
        => Id.GetHashCode();
}