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

    public ObservableCollection<DownloadItem> Active { get; } = [];
    public ObservableCollection<DownloadItem> Queue { get; } = [];
    public ObservableCollection<DownloadItem> History { get; } = [];

    private readonly Dictionary<DownloadState, ObservableCollection<DownloadItem>> _buckets;

    private readonly Dictionary<Guid, DownloadState> _lastStates = new();

    /// <summary>
    /// True, wenn gerade eine Verbindung zum aria2-RPC-Server besteht.
    /// </summary>
    public bool RpcConnection { get; private set; } = true;

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
            [DownloadState.Paused] = Active,
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

    public async Task<Guid> AddAsync(string url, string? targetFolder = null, string? filename = null, int? split = 8)
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

        return item.Id;
    }

    public async Task DeleteAllStoppedAsync()
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync();
        foreach (var it in History.ToList())
        {
            var item = ctx.Downloads.SingleOrDefault(d => d.Id == it.Id);

            if (item != null) 
                ctx.Downloads.Remove(item);

            History.Remove(it);
        }

        await ctx.SaveChangesAsync();
    }

    public Task PauseAsync(DownloadItem it) => _backend.PauseAsync(it.BackendId);
    public Task ResumeAsync(DownloadItem it) => _backend.ResumeAsync(it.BackendId);
    public Task StopAsync(DownloadItem it) => _backend.StopAsync(it.BackendId);

    /* ---------- Background loop ---------- */

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync(stoppingToken);

        // Alle noch nicht „fertigen“ Items dem Backend bekanntmachen.
        // Als erstes suchen wir welche die eventuell Status Downloading haben.
        // Diese sollten als erstes wieder gestartet werden.
        // Danach die mit Status Paused und dem Flag PausedDueToDisconnect.
        // Alle anderen werden später abgearbeitet.
        var downloading = ctx.Downloads.Where(d => d.State == DownloadState.Downloading).ToList();
        var paused = ctx.Downloads.Where(d => d.State == DownloadState.Paused && d.PausedDueToDisconnect == true)
            .ToList();

        await ReAddExistingDownload(downloading, stoppingToken, ctx);
        await ReAddExistingDownload(paused, stoppingToken, ctx);

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

    private async Task ReAddExistingDownload(List<DownloadItem> items, CancellationToken ct, DownloadDbContext ctx)
    {
        foreach (var it in items)
        {
            // Erst mal prüfen ob wir noch freie Slots haben
            var states = await _backend.GetStatusesAsync(ct);
            var active = states.Count(s => s.RawState is "active");
            var freeSlots = Math.Max(MaxParallel - active, 0);

            if (freeSlots > 0)
            {
                it.BackendId = await _backend.AddAsync(it, ct);
                it.State = DownloadState.Downloading;
            
                Queue.Remove(it);
                Active.Remove(it);
                Active.Add(it);
            }
            else
            {
                // Wenn aria2 voll ist, dann in die Queue zurück schieben.
                it.State = DownloadState.Waiting;
                Active.Remove(it);
                Queue.Remove(it);
                Queue.Add(it);
            }

            _lastStates[it.Id] = it.State;

            ctx.Update(it);
            ReBucket(it);
        }
    }

    private const int MaxParallel = 2;

    private async Task RefreshFromBackend(CancellationToken ct)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync(ct);

        IReadOnlyList<BackendStatus> states;
        try
        {
            states = await _backend.GetStatusesAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            // --- RPC-Verbindung verloren ---
            if (RpcConnection)
            {
                RpcConnection = false;
                _logger.LogWarning(ex, "RPC-Verbindung verloren – pausiere alle aktiven Downloads");

                // Alle aktiven Downloads pausieren
                foreach (var it in Active.ToList())
                {
                    it.State = DownloadState.Paused;
                    it.PausedDueToDisconnect = true;
                    // Bucket-Wechsel
                    ReBucket(it);
                    ctx.Update(it);
                }

                await ctx.SaveChangesAsync(ct);
                Updated?.Invoke();
            }

            // kurzen Abstand, damit wir nicht tight-loopen
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            return;
        }

        // --- RPC-Verbindung wiederhergestellt? ---
        if (!RpcConnection)
        {
            RpcConnection = true;
            _logger.LogInformation("RPC-Verbindung wiederhergestellt – führe Fortsetzungen durch");

            // Alle Downloads, die wir beim Disconnect pausiert haben, neu anlegen
            var toResume = ctx.ChangeTracker
                .Entries<DownloadItem>()
                .Select(e => e.Entity)
                .Union(Active)
                //.Union(Queue)
                .Where(it => it.PausedDueToDisconnect)
                .ToList();

            foreach (var it in toResume)
            {
                // Neuer GID, da aria2 frisch gestartet
                it.BackendId = await _backend.AddAsync(it, ct);
                it.PausedDueToDisconnect = false;
                it.State = DownloadState.Downloading;
                ReBucket(it);
                ctx.Update(it);
            }

            await ctx.SaveChangesAsync(ct);
            Updated?.Invoke();
        }

        _totalSpeed = 0;

        foreach (var st in states)
        {
            // var it = await ctx.Downloads
            //     .SingleOrDefaultAsync(d => d.BackendId == st.Id, ct);

            var it = Active
                .Concat(Queue)
                .Concat(History)
                .FirstOrDefault(it => it.BackendId == st.Id);

            if (it is null) continue;

            // 2. Neuen Zustand mappen
            var newState = st.RawState switch
            {
                "active" => DownloadState.Downloading,
                "waiting" => DownloadState.Waiting,
                "paused" => DownloadState.Paused,
                "error" => DownloadState.Error,
                "complete" => DownloadState.Complete,
                _ => DownloadState.Stopped
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

            if (newState == DownloadState.Complete)
            {
                var filePath = await _backend.GetDownloadedFilePathAsync(it.BackendId, ct);
                if (!string.IsNullOrEmpty(filePath))
                {
                    it.LocalFilePath = filePath;
                    isModified = true;
                }
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
                it.State = DownloadState.Downloading;
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