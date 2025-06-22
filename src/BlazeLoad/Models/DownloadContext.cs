using Downloader;

namespace BlazeLoad.Models;

public class DownloadContext
{
    public DownloadItem Item { get; init; } = default!;
    public IDownload D { get; init; } = default!;
}