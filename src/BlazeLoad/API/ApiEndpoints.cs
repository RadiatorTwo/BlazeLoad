using BlazeLoad.Models;
using BlazeLoad.Services;
using Microsoft.AspNetCore.StaticFiles;

namespace BlazeLoad.API;

public static class ApiEndpoints
{
    public static void MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/downloads");

        group.MapPost("/", AddDownload)
            .WithName("AddDownload");
        
        // 2) Neuer GET-Endpoint zum Herunterladen der fertigen Datei
        group.MapGet("/{id}", DownloadFile)
            .WithName("DownloadFile");
    }

    private static async Task<IResult> AddDownload(
        AddDownloadRequest req,
        IDownloadBackend downloads)
    {
        if (!Uri.IsWellFormedUriString(req.Url, UriKind.Absolute))
            return Results.BadRequest("Ungültige URL");

        var downloadItem = new DownloadItem
        {
            Url = req.Url,
            Name = req.FileName,
            TargetDirectory = req.TargetDirectory,
        };
        
        var id = await downloads.AddAsync(downloadItem);

        return Results.Created($"/api/downloads/{id}", new { id });
    }
    
    private static async Task<IResult> DownloadFile(
        string id,
        IDownloadBackend downloads)
    {
        // 1. Download-Item (inkl. LocalPath) holen
        var filepath = await downloads.GetDownloadedFilePathAsync(id);
        if (!File.Exists(filepath))
            return Results.NotFound($"Kein Download mit ID {id} gefunden.");

        // 2. Content-Type ermitteln (optional, für Browser-Erkennung)
        var provider = new FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(filepath, out var contentType))
            contentType = "application/octet-stream";

        // 3. File-Stream öffnen
        var stream = File.OpenRead(filepath);
        var fileName = Path.GetFileName(filepath);

        // 4. Datei mit Range-Support zurückgeben
        return Results.File(
            fileStream: stream,
            contentType: contentType,
            fileDownloadName: fileName,
            enableRangeProcessing: true
        );
    }
}