namespace BlazeLoad.Models;

public record AddDownloadRequest(
    string Url,
    string FileName,
    string TargetDirectory);