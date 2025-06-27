using System.Collections.ObjectModel;
using BlazeLoad.Data;
using BlazeLoad.Models;
using Microsoft.EntityFrameworkCore;

namespace BlazeLoad.Services;

public sealed class PersistentDownloadService : BackgroundService
{
    private readonly ILogger<PersistentDownloadService> _logger;
    private readonly IDownloadBackend _backend;
    private readonly IDbContextFactory<DownloadDbContext> _dbFactory;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromMilliseconds(500));

    public ObservableCollection<DownloadItem> Active { get; } = [];
    public ObservableCollection<DownloadItem> Queue { get; } = [];
    public ObservableCollection<DownloadItem> History { get; } = [];

    private readonly Dictionary<DownloadState, ObservableCollection<DownloadItem>> _buckets;

    private readonly Dictionary<Guid, DownloadState> _lastStates = new();

    public event Action? Updated;

    /* Gesamtspeed für Toolbar */
    public string TotalSpeedFormatted => DownloadItem.ByteFormat(_totalSpeed) + "/s";
    public int ActiveCount => Active.Count;
    public int QueuedCount => Queue.Count;
    public int TotalCount => Active.Count + Queue.Count;

    private long _totalSpeed;

    public PersistentDownloadService(
        ILogger<PersistentDownloadService> logger,
        IDownloadBackend backend,
        IDbContextFactory<DownloadDbContext> dbFactory)
    {
        _logger = logger;
        _backend = backend;
        _dbFactory = dbFactory;

        _buckets = new Dictionary<DownloadState, ObservableCollection<DownloadItem>>
        {
            [DownloadState.Downloading] = Active,
            [DownloadState.Waiting] = Queue,
            [DownloadState.Paused] = Queue, // oder eigene Paused-Liste
            [DownloadState.Stopped] = History,
            [DownloadState.Error] = History,
            [DownloadState.Complete] = History
        };

        using var ctx = _dbFactory.CreateDbContext();
        ctx.Database.Migrate();

        foreach (var it in ctx.Downloads.AsNoTracking())
        {
            ReBucket(it);
        }
    }

    /* ---------- Public API ---------- */

    public async Task AddAsync(string url, string? targetFolder = null, string? filename = null, int? split = 8)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync();

        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            Url = url,
            Name = filename,
            State = DownloadState.Waiting,
            Total = 0,
            Done = 0,
            Speed = 0,
            TargetDirectory = targetFolder,
            Connections = (int)split!
        };

        // Sofort in DB
        ctx.Downloads.Add(item);
        await ctx.SaveChangesAsync();

        Queue.Add(item); // UI
        _lastStates[item.Id] = item.State;
    }

    public Task PauseAsync(DownloadItem it) => _backend.PauseAsync(it.BackendId);
    public Task ResumeAsync(DownloadItem it) => _backend.ResumeAsync(it.BackendId);
    public Task StopAsync(DownloadItem it) => _backend.StopAsync(it.BackendId);

    /* ---------- Background loop ---------- */

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync(stoppingToken);

        // Alle noch nicht “fertigen” Items dem Backend bekanntmachen
        foreach (var it in ctx.Downloads.Where(d =>
                     d.State == DownloadState.Waiting || d.State == DownloadState.Downloading))
        {
            // Wenn aria2 frisch ist, muss AddAsync neu aufgerufen werden
            it.BackendId = await _backend.AddAsync(it, stoppingToken);
            ctx.Update(it);
        }

        await ctx.SaveChangesAsync(stoppingToken);

        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RefreshFromBackend(stoppingToken);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Log und weitermachen
                _logger.LogError(ex, "Unerwarteter Fehler in RefreshFromBackend");
            }
        }
    }

    private const int MaxParallel = 2;

    private async Task RefreshFromBackend(CancellationToken ct)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync(ct);

        /* ---------- 1. Rückkanal vom Backend ---------- */

        IReadOnlyList<BackendStatus> states;
        try
        {
            states = await _backend.GetStatusesAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Kann aria2-RPC unter {Url} nicht erreichen, versuche es in 1 Sekunde erneut.");
            // kurze Pause, damit du nicht im Tight-Loop stuckst
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            return;
        }

        _totalSpeed = 0;

        foreach (var st in states)
        {
            var it = await ctx.Downloads
                .SingleOrDefaultAsync(d => d.BackendId == st.Id, ct);
            
            if (it is null) continue;
            
            // 2. Neuen Zustand mappen
            var newState = st.RawState switch
            {
                "active"   => DownloadState.Downloading,
                "waiting"  => DownloadState.Waiting,
                "paused"   => DownloadState.Paused,
                "error"    => DownloadState.Error,
                "complete" => DownloadState.Complete,
                _          => DownloadState.Stopped
            };
            
            // 3. Nur bei echtem Change in die DB
            var isModified = false;
            
            if (it.State != newState)
            {
                it.State = newState;
                isModified = true;
            }
            
            // 4. Nur wenn Download gerade läuft, Speed & Fortschritt updaten
            if (newState == DownloadState.Downloading)
            {
                if (it.TotalBytes != st.Total)
                {
                    it.TotalBytes = st.Total;
                }
                if (it.DownloadedBytes != st.Done)
                {
                    it.DownloadedBytes = st.Done;
                }
                if (it.SpeedBytesPerSec != st.Speed)
                {
                    it.SpeedBytesPerSec = st.Speed;
                }

                _totalSpeed += st.Speed;
            }

            // 5. Nur wenn was geändert wurde, EF ChangeTracker informieren
            if (isModified)
            {
                ctx.Update(it);
            }
            
            // 6. UI-Rebucket basierend auf neuem State
            ReBucket(it);
        }

        /* ---------- 2. Wartende Jobs anstoßen ---------- */

        var active = states.Count(s => s.RawState is "active");
        var freeSlots = Math.Max(MaxParallel - active, 0);

        if (freeSlots > 0)
        {
            // 1) Nur filtern, ohne ORDER BY in SQL
            var waiting = await ctx.Downloads
                .Where(d => d.State == DownloadState.Waiting && d.BackendId == "")
                .ToListAsync(ct);

            // 2) In-Memory sortieren nach AddedAt
            var toStart = waiting
                .OrderBy(d => d.AddedAt)
                .Take(freeSlots)
                .ToList();

            foreach (var it in toStart)
            {
                it.BackendId = await _backend.AddAsync(it, ct);
                // aria2 stellt sie sofort auf „waiting“, daher State beibehalten
                ctx.Update(it);
                ReBucket(it);
            }
        }

        // 7. SaveChanges nur, wenn mindestens ein Item modifiziert wurde
        if (ctx.ChangeTracker.HasChanges())
        {
            await ctx.SaveChangesAsync(ct);
        }
        
        // 8. UI-Update immer
        Updated?.Invoke();
    }

    /* ---------- Pause/Stop All ---------- */

    public async Task PauseAllAsync()
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync();

        // Nur Items, die gerade laufen oder warten
        var candidates = ctx.Downloads
            .Where(d => d.State == DownloadState.Downloading || d.State == DownloadState.Waiting)
            .ToList();

        foreach (var it in candidates)
        {
            try
            {
                await _backend.PauseAsync(it.BackendId);
                it.State = DownloadState.Paused;
                it.SpeedBytesPerSec = 0;
                ReBucket(it);
            }
            catch (Exception ex)
            {
                it.State = DownloadState.Error;
                it.ErrorMessage = ex.Message;
            }
        }

        await ctx.SaveChangesAsync();
        Updated?.Invoke();
    }

    public async Task StopAllAsync()
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync();

        // Nur Items, die gerade laufen oder warten
        var candidates = ctx.Downloads
            .Where(d => d.State == DownloadState.Downloading || d.State == DownloadState.Waiting)
            .ToList();

        foreach (var it in candidates)
        {
            try
            {
                await _backend.StopAsync(it.BackendId);
                it.State = DownloadState.Stopped;
                it.SpeedBytesPerSec = 0;
                ReBucket(it);
            }
            catch (Exception ex)
            {
                it.State = DownloadState.Error;
                it.ErrorMessage = ex.Message;
            }
        }

        await ctx.SaveChangesAsync();
        Updated?.Invoke();
    }

    /* ---------- graceful shutdown ---------- */

    public override async Task StopAsync(CancellationToken ct)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync(ct);
        await ctx.SaveChangesAsync(ct); // letzter Stand
        await base.StopAsync(ct);
    }

    /* ---------- UI buckets ---------- */

    private void ReBucket(DownloadItem it)
    {
        // 1) Alten State holen (default Waiting)
        var prevState = _lastStates.TryGetValue(it.Id, out var st)
            ? st
            : DownloadState.Waiting;

        // 2) Neuer Bucket
        var newBucket = _buckets[it.State];
        // 3) Alter Bucket
        var oldBucket = _buckets[prevState];

        // 4) Nur bei State-Wechsel UND wenn sich die Listen unterscheiden
        if (prevState == it.State || oldBucket == newBucket) return;
        
        oldBucket.Remove(it);
        if (!newBucket.Contains(it))
            newBucket.Add(it);

        // 5) letzten State merken
        _lastStates[it.Id] = it.State;
    }
}