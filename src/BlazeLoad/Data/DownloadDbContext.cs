using BlazeLoad.Models;
using Microsoft.EntityFrameworkCore;

namespace BlazeLoad.Data;

public sealed class DownloadDbContext : DbContext
{
    public DbSet<DownloadItem> Downloads => Set<DownloadItem>();

    protected override void OnConfiguring(DbContextOptionsBuilder b)
        => b.UseSqlite("Data Source=downloads.db");
}