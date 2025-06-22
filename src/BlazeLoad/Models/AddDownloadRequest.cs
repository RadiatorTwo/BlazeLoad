namespace BlazeLoad.Models;

public record AddDownloadRequest(
    string Url,
    string? FileName = null,
    string? TargetDirectory = null);