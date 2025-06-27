using BlazeLoad.API;
using MudBlazor.Services;
using BlazeLoad.Components;
using BlazeLoad.Data;
using BlazeLoad.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Debug);

builder.Configuration.AddEnvironmentVariables("ASPNETCORE_");

// Add MudBlazor services
builder.Services.AddMudServices();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<DownloadDbContext>(opt =>
    opt.UseSqlite("Data Source=downloads.db"));

builder.Services.AddSingleton<IDownloadBackend>(sp =>
    new Aria2Backend("http://127.0.0.1:6800/jsonrpc", "topsecret"));

builder.Services.AddHostedService<PersistentDownloadService>();

builder.Services.AddSingleton<PersistentDownloadService>(sp =>
    (PersistentDownloadService) sp
        .GetRequiredService<IEnumerable<IHostedService>>()
        .First(s => s is PersistentDownloadService));

builder.Services.AddControllers();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapApiEndpoints(); 

app.Run();