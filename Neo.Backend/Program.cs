using Microsoft.AspNetCore.StaticFiles;
using Neo.Backend.Api;
using Neo.Backend.Services;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // The WebApp Browser bundle is copied to bin/<config>/net9.0/wwwroot during
    // Neo.Backend's build (see csproj). Point WebRoot there so `dotnet run`
    // from the project dir also serves the bundle.
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});

builder.Services.AddCors(opt =>
    opt.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

builder.Services.AddSingleton<ProviderRegistry>();

builder.Services.AddHttpClient("ai", c => c.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient("nuget", c =>
{
    c.Timeout = TimeSpan.FromMinutes(2);
    c.DefaultRequestHeaders.Add("User-Agent", "Neo.Backend/0.1");
});

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();

// UseStaticFiles by default refuses to serve unknown content types — and .dll,
// .wasm, .blat, .dat, .pdb are all "unknown" to it. Register the MIME types the
// Avalonia.Browser bundle ships with so every file under wwwroot/ is served.
var provider = new FileExtensionContentTypeProvider();
provider.Mappings[".dll"]   = "application/octet-stream";
provider.Mappings[".pdb"]   = "application/octet-stream";
provider.Mappings[".blat"]  = "application/octet-stream";
provider.Mappings[".dat"]   = "application/octet-stream";
provider.Mappings[".wasm"]  = "application/wasm";
provider.Mappings[".br"]    = "application/octet-stream";
provider.Mappings[".gz"]    = "application/octet-stream";
provider.Mappings[".webcil"]= "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = provider,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
});

app.MapHealth();
app.MapAiProxy();
app.MapNuGetProxy();

// Print available providers at boot so the dev sees immediately what's configured.
var reg = app.Services.GetRequiredService<ProviderRegistry>();
Console.WriteLine("Neo.Backend — provider status:");
foreach (var p in ProviderRegistry.All)
{
    var ok = reg.IsAvailable(p) ? "available" : "missing env var";
    Console.WriteLine($"  {p.Id,-10} {ok}");
}

app.Run();

public partial class Program { }
