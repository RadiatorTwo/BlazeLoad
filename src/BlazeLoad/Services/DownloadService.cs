using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Aria2NET;
using BlazeLoad.Models;

namespace BlazeLoad.Services;

/// <summary>
/// Singleton-Backend für Downloads (Queue + Live-Status) auf Basis von aria2.
/// </summary>
public sealed class DownloadService : IHostedService, IDisposable
{
    public ObservableCollection<DownloadItem> Active { get; } = [];
    public ObservableCollection<DownloadItem> Queue { get; } = [];
    public event Action? Updated;

    /* Gesamtspeed für Toolbar */
    public string TotalSpeedFormatted => DownloadItem.ByteFormat(_totalSpeed) + "/s";
    public int ActiveCount => Active.Count;
    public int QueuedCount => Queue.Count;
    public int TotalCount => Active.Count + Queue.Count;


    private readonly Aria2NetClient _rpc =
        new("http://127.0.0.1:6800/jsonrpc", "topsecret");

    private readonly ConcurrentDictionary<string, DownloadItem> _map = new();
    private readonly PeriodicTimer _poll = new(TimeSpan.FromMilliseconds(500));
    private readonly CancellationTokenSource _cts = new();

    private long _totalSpeed;

    public DownloadService()
    {
        // Initiale Abfrage, damit die Listen nicht leer sind
        _ = RefreshAsync();

        // Polling-Loop starten
        _ = StartAsync(CancellationToken.None);
    }

    /* ========== Public API ======================================= */

    public async Task<string> AddAsync(string url, string? dir = null, string? filename = null, int split = 8)
    {
        var opts = new Dictionary<string, object>
        {
            ["dir"] = dir ?? "/home/radi",
            ["split"] = split.ToString(),
            ["max-connection-per-server"] = split.ToString()
        };

        var ver = await _rpc.GetVersionAsync();

        if (!string.IsNullOrWhiteSpace(filename))
        {
            opts["out"] = filename;
        }

        var gid = await _rpc.AddUriAsync([url], opts);

        var item = new DownloadItem
        {
            Id = gid,
            Url = url,
            Name = Path.GetFileName(new Uri(url).LocalPath),
            State = DownloadState.Waiting
        };

        _map[gid] = item;
        Queue.Add(item);
        Updated?.Invoke();
        return item.Id;
    }

    public Task PauseAsync(Guid id) => _rpc.PauseAsync(id.ToString("N"));
    public Task ResumeAsync(Guid id) => _rpc.UnpauseAsync(id.ToString("N"));
    public Task StopAsync(Guid id) => _rpc.RemoveAsync(id.ToString("N"));

    /* ========== polling ========================================== */

    private async Task PollLoop()
    {
        try
        {
            while (await _poll.WaitForNextTickAsync(_cts.Token))
            {
                await RefreshAsync();
                Updated?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshAsync()
    {
        var active = await _rpc.TellActiveAsync();
        var waiting = await _rpc.TellWaitingAsync(0, 1000);
        var stopped = await _rpc.TellStoppedAsync(0, 1000);

        _totalSpeed = 0; // Gesamtspeed neu berechnen

        Update(active, DownloadState.Downloading);
        Update(waiting, DownloadState.Waiting);
        Update(stopped, null); // stopped = error / complete
    }

    /* ========== helpers ========================================== */

    private void Update(IEnumerable<DownloadStatusResult> list,
        DownloadState? forcedState)
    {
        foreach (var r in list)
        {
            var gid = r.Gid;
            if (!_map.TryGetValue(gid, out var it))
            {
                it = new DownloadItem { Id = gid };
                _map[gid] = it;
                Queue.Add(it);
            }

            // Basisdaten (einmalig)
            if (string.IsNullOrWhiteSpace(it.Name) && r.Files?.Count > 0)
                it.Name = Path.GetFileName(r.Files[0].Path ?? it.Name);

            /* -------- Statusbezogene Felder --------------------- */
            if (forcedState == DownloadState.Downloading)
            {
                it.Total = r.TotalLength;
                it.Done = r.CompletedLength;
                it.Speed = r.DownloadSpeed;
                _totalSpeed += it.Speed; // ← Summieren
                it.State = DownloadState.Downloading;
            }
            else if (forcedState == DownloadState.Waiting)
            {
                it.State = DownloadState.Waiting;
            }
            else // stopped-Liste
            {
                it.State = r.Status == "error"
                    ? DownloadState.Error
                    : DownloadState.Complete;
                it.Speed = 0;
            }

            Rebucket(it);
        }
    }

    private void Rebucket(DownloadItem it)
    {
        if (it.State == DownloadState.Downloading)
        {
            if (!Active.Contains(it))
            {
                Queue.Remove(it);
                Active.Add(it);
            }
        }
        else if (it.State == DownloadState.Waiting)
        {
            if (!Queue.Contains(it))
            {
                Active.Remove(it);
                Queue.Add(it);
            }
        }
        else
        {
            Active.Remove(it);
            Queue.Remove(it);
        }
    }

    /* ========== IHostedService plumbing ========================== */

    public Task StartAsync(CancellationToken ct)
    {
        _ = PollLoop();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken _)
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    /* Pausiert alle aktiven Downloads */
    public Task PauseAllAsync()
    {
        // aria2 bringt bereits einen RPC-Befehl „pauseAll“
        // → wird in Aria2.NET als Async-Methode exponiert
        return _rpc.PauseAllAsync();
    }

    /* Stoppt (entfernt) ALLE derzeit bekannten Downloads */
    public async Task StopAllAsync()
    {
        // Falls deine aria2-Version „removeDownloadResult“ unterstützt,
        // kannst du stattdessen _rpc.RemoveDownloadResultAsync(gid) nutzen.
        foreach (var gid in _map.Keys.ToArray())
        {
            try
            {
                await _rpc.RemoveAsync(gid); // beendet und löscht aus aria2
            }
            catch
            {
                /* ignorieren, wenn Download schon weg */
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _poll.Dispose();
    }
}