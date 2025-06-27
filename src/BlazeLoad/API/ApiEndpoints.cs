using BlazeLoad.Models;
using BlazeLoad.Services;

namespace BlazeLoad.API;

public static class ApiEndpoints
{
    public static void MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/downloads");

        group.MapPost("/", AddDownload)
            .WithName("AddDownload");
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
}