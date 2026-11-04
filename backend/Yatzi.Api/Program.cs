using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// TODO: registrér services, fx spillets tilstand og forbindelsen til Oplaen (DoD 5)

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

// TODO: endpoints til spillet, fx under /api/game

app.Run();

// Gør Program synlig for integrationstests.
public partial class Program { }
