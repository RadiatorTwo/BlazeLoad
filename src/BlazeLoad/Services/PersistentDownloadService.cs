using System.Collections.ObjectModel;
using BlazeLoad.Data;
using BlazeLoad.Models;
using Microsoft.EntityFrameworkCore;

namespace BlazeLoad.Services;

public sealed class PersistentDownloadService : BackgroundService
{
    public ObservableCollection<DownloadItem> Active { get; } = [];
    public ObservableCollection<DownloadItem> Queue { get; } = [];
    public ObservableCollection<DownloadItem> History { get; } = [];

    private readonly IDownloadBackend _backend;
    private readonly DownloadDbContext _db;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromMilliseconds(500));

    public PersistentDownloadService(
        IDownloadBackend backend,
        DownloadDbContext db)
    {
        _backend = backend;
        _db = db;

        // DB migrieren & Items laden
        _db.Database.Migrate();
        foreach (var it in _db.Downloads.AsNoTracking())
            ReBucket(it);
    }

    /* ---------- Public API ---------- */

    public async Task<string> AddAsync(string url, string? fn = null, string? path = null, int? split = 8)
    {
        var item = new DownloadItem
        {
            Id = string.Empty, // temporär, damit es nicht null ist
            Url = url,
            Name = fn ?? Path.GetFileName(new Uri(url).LocalPath),
            State = DownloadState.Waiting,
            Total  = 0,
            Done   = 0,
            Speed  = 0,
            TargetDirectory = "/home/radi",
            Connections = (int)split!
        };

        // Sofort in DB
        _db.Downloads.Add(item);
        await _db.SaveChangesAsync();

        Queue.Add(item); // UI
        return item.Id;
    }

    public Task PauseAsync(DownloadItem it) => _backend.PauseAsync(it.Id);
    public Task ResumeAsync(DownloadItem it) => _backend.ResumeAsync(it.Id);
    public Task StopAsync(DownloadItem it) => _backend.StopAsync(it.Id);

    /* ---------- Background loop ---------- */

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Alle noch nicht “fertigen” Items dem Backend bekanntmachen
        foreach (var it in _db.Downloads.Where(d =>
                     d.State == DownloadState.Waiting || d.State == DownloadState.Downloading))
        {
            // Wenn aria2 frisch ist, muss AddAsync neu aufgerufen werden
            it.Id = await _backend.AddAsync(it, stoppingToken);
            _db.Update(it);
        }

        await _db.SaveChangesAsync(stoppingToken);

        while (await _timer.WaitForNextTickAsync(stoppingToken))
            await RefreshFromBackend(stoppingToken);
    }

    private async Task RefreshFromBackend(CancellationToken ct)
    {
        var states = await _backend.GetStatusesAsync(ct);
        long totalSpeed = 0;

        foreach (var st in states)
        {
            var it = await _db.Downloads.FindAsync(new object?[] { st.Id }, ct);
            if (it == null) continue; // fremde Jobs ignorieren

            it.Total = st.Total;
            it.Done = st.Done;
            it.Speed = st.Speed;
            totalSpeed += it.Speed;

            it.State = st.RawState switch
            {
                "active" => DownloadState.Downloading,
                "paused" => DownloadState.Paused,
                "waiting" => DownloadState.Waiting,
                "error" => DownloadState.Error,
                "complete" => DownloadState.Complete,
                _ => DownloadState.Stopped
            };

            _db.Update(it);
            ReBucket(it);
        }

        await _db.SaveChangesAsync(ct);
        Updated?.Invoke();
    }

    /* ---------- graceful shutdown ---------- */

    public override async Task StopAsync(CancellationToken ct)
    {
        await _db.SaveChangesAsync(ct); // letzter Stand
        await base.StopAsync(ct);
    }

    /* ---------- UI buckets ---------- */

    private void ReBucket(DownloadItem it)
    {
        /* identisch zu deinem Code */
    }
}