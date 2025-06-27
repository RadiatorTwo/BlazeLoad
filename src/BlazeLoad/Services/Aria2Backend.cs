using Aria2NET;
using BlazeLoad.Models;

namespace BlazeLoad.Services;

public sealed class Aria2Backend : IDownloadBackend, IAsyncDisposable
{
    private readonly Aria2NetClient _rpc;

    public Aria2Backend(string url, string secret)
        => _rpc = new Aria2NetClient(url, secret);

    public async Task<string> AddAsync(DownloadItem item, CancellationToken ct = default)
    {
        var opts = new Dictionary<string, object?>
        {
            ["dir"] = item.TargetDirectory,
            ["out"] = item.Name,
            ["split"] = item.Connections.ToString(),
            ["max-connection-per-server"] = item.Connections.ToString()
        };

        var gid = await _rpc.AddUriAsync([item.Url], opts, null, ct);
        return gid;
    }

    public Task PauseAsync(string id, CancellationToken ct = default)   => _rpc.PauseAsync(id, ct);
    public Task ResumeAsync(string id, CancellationToken ct = default)  => _rpc.UnpauseAsync(id, ct);
    public Task StopAsync(string id, CancellationToken ct = default)    => _rpc.RemoveAsync(id, ct);

    public async Task<IReadOnlyList<BackendStatus>> GetStatusesAsync(CancellationToken ct = default)
    {
        var active   = await _rpc.TellActiveAsync(ct);
        var waiting  = await _rpc.TellWaitingAsync(0, 1000, ct);
        var stopped  = await _rpc.TellStoppedAsync(0, 1000, ct);

        return active.Concat(waiting).Concat(stopped)
            .Select(r => new BackendStatus(
                r.Gid, r.TotalLength, r.CompletedLength, r.DownloadSpeed, r.Status))
            .ToList();
    }

    // public ValueTask DisposeAsync() => _rpc.DisposeAsync();
    public async ValueTask DisposeAsync()
    {
        // TODO release managed resources here
    }
}