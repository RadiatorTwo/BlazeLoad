using BlazeLoad.Models;

namespace BlazeLoad.Services;

public sealed record BackendStatus(
    string Id,
    long Total,
    long Done,
    long Speed,
    string RawState);

public interface IDownloadBackend
{
    Task<string> AddAsync(DownloadItem item, CancellationToken ct = default);
    Task PauseAsync(string id, CancellationToken ct = default);
    Task ResumeAsync(string id, CancellationToken ct = default);
    Task StopAsync(string id, CancellationToken ct = default);

    /// Aktuellen Status aller im Backend bekannten Jobs abrufen
    Task<IReadOnlyList<BackendStatus>> GetStatusesAsync(CancellationToken ct = default);
}