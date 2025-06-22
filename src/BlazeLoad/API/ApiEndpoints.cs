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

    private static IResult AddDownload(
        AddDownloadRequest req,
        DownloadService downloads)
    {
        if (!Uri.IsWellFormedUriString(req.Url, UriKind.Absolute))
            return Results.BadRequest("Ungültige URL");

        var id = downloads.AddDownload(req.Url, req.TargetDirectory);

        if (string.IsNullOrWhiteSpace(req.FileName))
            return Results.Created($"/api/downloads/{id}", new { id });
        
        var it = downloads.QueueItems.FirstOrDefault(x => x.Id == id);
        
        if (it != null) it.FileName = req.FileName;

        return Results.Created($"/api/downloads/{id}", new { id });
    }
}