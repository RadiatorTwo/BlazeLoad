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
        DownloadService downloads)
    {
        if (!Uri.IsWellFormedUriString(req.Url, UriKind.Absolute))
            return Results.BadRequest("Ungültige URL");

        var id = await downloads.AddAsync(req.Url, req.TargetDirectory, req.FileName);

        if (string.IsNullOrWhiteSpace(req.FileName))
            return Results.Created($"/api/downloads/{id}", new { id });

        var it = downloads.Queue.FirstOrDefault(x => x.Id == id);

        if (it != null) it.Name = req.FileName;

        return Results.Created($"/api/downloads/{id}", new { id });
    }
}