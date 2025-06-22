using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using BlazeLoad.Models;
using Downloader;
using DownloadStatus = Downloader.DownloadStatus;

namespace BlazeLoad.Services;

public sealed class DownloadService : IDisposable
{
    /* ───── Event meldet jede sichtbare Änderung ───── */
    public event Action? Updated;

    private void RaiseUpdated()
    {
        Updated?.Invoke();
    }

    /* ───── öffentliche Bindungen (für UI) ───── */
    public ObservableCollection<DownloadItem> ActiveItems { get; } = new();
    public ObservableCollection<DownloadItem> QueueItems { get; } = new();

    public string TotalSpeedFormatted { get; private set; } = "0.0 KiB/s";
    public int ActiveCount => ActiveItems.Count;
    public int QueuedCount => QueueItems.Count;
    public int TotalCount => ActiveItems.Count + QueueItems.Count;

    /* ───── interne Felder ───── */
    private readonly int _maxParallel;
    private readonly DownloadConfiguration _cfg;
    private readonly ConcurrentDictionary<Guid, DownloadContext> _running = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly PeriodicTimer _infoTimer = new(TimeSpan.FromMilliseconds(500));
    private readonly Task _queueWorker;
    private readonly string _persistFile = Path.Combine(AppContext.BaseDirectory, "downloadqueue.json");

    public DownloadService(int maxParallel = 3)
    {
        _maxParallel = Math.Max(1, maxParallel);

        _cfg = new DownloadConfiguration // global für alle Downloads
        {
            ChunkCount = 4,
            ParallelDownload = true,
            ParallelCount = 4,
            BufferBlockSize = 64 * 1024, // 64 KiB
            MaximumMemoryBufferBytes = 1024 * 1024 * 50,
            MinimumSizeOfChunking = 1024,
            ReserveStorageSpaceBeforeStartingDownload = false,
            EnableLiveStreaming = false
        };

        LoadPersistedQueue();

        _queueWorker = Task.Run(ProcessQueueAsync);
        _ = Task.Run(UpdateInfoLoop);
    }

    /* ========== Öffentliche API ========== */

    public Guid AddDownloadAsync(string url, string? filename = null, string? targetDir = null)
    {
        var item = new DownloadItem
        {
            SourceUrl = url,
            FileName = filename,
            TargetDirectory = targetDir ?? Path.Combine(AppContext.BaseDirectory, "Downloads"),
            Status = DownloadStatus.Created
        };

        QueueItems.Add(item);
        PersistQueue();

        return item.Id;
    }

    public bool RemoveDownload(Guid id)
    {
        if (_running.TryRemove(id, out var ctx))
        {
            ctx.D.Stop(); // stop laufenden DL
            ActiveItems.Remove(ctx.Item);
            return true;
        }

        var q = QueueItems.FirstOrDefault(x => x.Id == id);
        if (q is not null)
        {
            QueueItems.Remove(q);
            PersistQueue();
            return true;
        }

        return false;
    }

    public Task PauseAllAsync()
    {
        foreach (var x in _running.Values)
            x.D.Pause();
        return Task.CompletedTask;
    }

    public Task StopAllAsync()
    {
        foreach (var x in _running.Values)
            x.D.Stop();
        return Task.CompletedTask;
    }

    /* ========== Queue-Worker ========== */

    private async Task ProcessQueueAsync()
    {
        var token = _cts.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_running.Count < _maxParallel && QueueItems.Any())
                {
                    var next = QueueItems[0];
                    QueueItems.RemoveAt(0);
                    _ = StartDownloadAsync(next, token);
                }
                else
                    await Task.Delay(250, token);
            }
            catch (OperationCanceledException)
            {
                /* ignore */
            }
        }
    }

    private async Task StartDownloadAsync(DownloadItem item, CancellationToken globalToken)
    {
        Directory.CreateDirectory(item.TargetDirectory!);

        if (item.FileName is not null)
        {
            var filePath = Path.Combine(item.TargetDirectory!, item.FileName);

            /* File conflict → Skip */
            if (File.Exists(filePath))
            {
                item.Status = DownloadStatus.Stopped;
                return;
            }
        }

        /* Downloader-Instanz pro Item */
        var builder = DownloadBuilder.New()
            .WithUrl(item.SourceUrl)
            .WithDirectory(item.TargetDirectory!)
            .WithConfiguration(_cfg);

        if (!string.IsNullOrEmpty(item.FileName))
            builder = builder.WithFileName(item.FileName);

        var dl = builder.Build();

        /* Event-Verdrahtung (liefert alle nötigen Daten) */
        dl.DownloadStarted += (_, args) =>
        {
            item.TotalBytes = args.TotalBytesToReceive;
            item.Status = DownloadStatus.Running;
            ActiveItems.Add(item);
        };

        dl.DownloadProgressChanged += (_, args) =>
        {
            item.ReceivedBytes = args.ReceivedBytesSize;
            item.SpeedBytesPerSec = args.BytesPerSecondSpeed;
            //item.TimeRemaining = args.TimeRemaining;
            // Prozent wird über Received/Total berechnet
        };

        dl.DownloadFileCompleted += (_, args) =>
        {
            item.Status = args.Cancelled ? DownloadStatus.Stopped
                : args.Error != null ? DownloadStatus.Failed : DownloadStatus.Stopped;
            Cleanup(item.Id);
            PersistQueue();
        };

        /* Start */
        var ctx = new DownloadContext { Item = item, D = dl };

        _running[item.Id] = ctx;

        await dl.StartAsync(globalToken);
    }

    private void Cleanup(Guid id)
    {
        if (_running.TryRemove(id, out var ctx))
            ActiveItems.Remove(ctx.Item);
    }

    /* ========== Info-Ticker & Persistence ========== */

    private async Task UpdateInfoLoop()
    {
        while (await _infoTimer.WaitForNextTickAsync(_cts.Token))
            UpdateInfo();
    }

    private void UpdateInfo()
    {
        var speed = ActiveItems.Sum(i => i.SpeedBytesPerSec);
        TotalSpeedFormatted = DownloadItem.ByteFormat(speed) + "/s";
        RaiseUpdated();
    }

    private void PersistQueue() => File.WriteAllText(_persistFile, JsonSerializer.Serialize(QueueItems));

    private void LoadPersistedQueue()
    {
        if (File.Exists(_persistFile))
            foreach (var it in JsonSerializer.Deserialize<List<DownloadItem>>(File.ReadAllText(_persistFile)) ?? [])
                QueueItems.Add(it);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queueWorker.Dispose();
        _infoTimer.Dispose();
    }
}