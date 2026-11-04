// Starter HTTP-backenden, registrerer den delte spiltilstand og serverer frontenden.
// GameEndpoints håndterer HTTP, GameSession ejer spillet, og Yatzi.Core har reglerne.
using Microsoft.Extensions.FileProviders;
using Yatzi.Api;

var builder = WebApplication.CreateBuilder(args);

// Alle HTTP-kald deler én session; selve spillet oprettes først af POST /api/game/new.
builder.Services.AddSingleton<GameSession>();

var app = builder.Build();

// Serverer frontend-mappen (../../frontend) på http://localhost:5000
var frontendPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "frontend"));
if (Directory.Exists(frontendPath))
{
    var files = new PhysicalFileProvider(frontendPath);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapSpike(); // S1.10 – midlertidigt, se Spike.cs

app.MapGameApi();

// TODO: Oplà-integration gennem beskedhåndtering og /ws/opla.

app.Run();

// Gør Program synlig for integrationstests.
public partial class Program { }
