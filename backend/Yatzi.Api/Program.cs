// Starter HTTP-backenden, registrerer den delte spiltilstand og serverer frontenden.
// GameEndpoints håndterer HTTP, GameSession ejer spillet, og Yatzi.Core har reglerne.
// OplaMessageHandler fortolker ROLL/HOLD og danner STATE-svar gennem samme session.
using Microsoft.Extensions.FileProviders;
using Yatzi.Api;

var builder = WebApplication.CreateBuilder(args);

// HTTP-API'et og beskedhåndteringen deler én session gennem dependency injection.
// Selve spillet oprettes først af POST /api/game/new.
builder.Services.AddSingleton<GameSession>();
builder.Services.AddSingleton<OplaMessageHandler>();

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

// TODO (#63): tilslut OplaMessageHandler til det kommende /ws/opla-endpoint.

app.Run();

// Gør Program synlig for integrationstests.
public partial class Program { }
