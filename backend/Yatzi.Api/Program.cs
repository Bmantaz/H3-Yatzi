// Starter HTTP- og WebSocket-backenden, registrerer den delte spiltilstand og serverer frontenden.
// GameEndpoints håndterer HTTP, GameSession ejer spillet, og Yatzi.Core har reglerne.
// OplaMessageHandler fortolker ROLL/HOLD og danner STATE-svar gennem samme session.
using Microsoft.Extensions.FileProviders;
using Yatzi.Api;

var builder = WebApplication.CreateBuilder(args);

// HTTP-API'et og beskedhåndteringen deler én session gennem dependency injection.
// Spillet oprettes af POST /api/game/new eller lobbyens start gennem samme session.
builder.Services.AddSingleton<GameSession>();
builder.Services.AddSingleton<OplaMessageHandler>();
// Transportregisteret deler forbindelser, men ejer ingen spiltilstand.
builder.Services.AddSingleton<OplaConnectionRegistry>();
builder.Services.AddSingleton(TimeProvider.System);
// Samme lobbyinstans ejer READY-koder og tilmeldinger i alle requests.
// PAIR giver ingen binding; HTTP-join binder spiller-id og sender JOINED.
builder.Services.AddSingleton<LobbySession>();
builder.Services.AddSingleton<IOplaReadyHandler>(services => services.GetRequiredService<LobbySession>());

var app = builder.Build();

// Efter Build er også integrationstestenes konfiguration tilgængelig.
// En ugyldig offentlig adresse stopper opstarten, før serveren accepterer requests.
TunnelStartup.LogPublicAddresses(app.Configuration, app.Logger);

// Begge WebSocket-endpoints bruger samme middleware. Registreringen er uafhængig af spiken.
app.UseWebSockets();

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
app.MapLobbyApi();
app.MapOplaWebSocket(); // #63: samme adgangskontrol og STATE-svar i alle miljøer.

app.Run();

// Gør Program synlig for integrationstests.
public partial class Program { }
