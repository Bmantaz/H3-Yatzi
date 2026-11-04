using System.Net.WebSockets;
using System.Text;

// S1.10 – midlertidigt ekko-endpoint til spiken. Erstattes af /ws/opla (S2.12).
public static class Spike
{
    public static void MapSpike(this WebApplication app)
    {
        app.UseWebSockets();

        app.Map("/ws/spike", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[256];

            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, ctx.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close) break;

                var line = Encoding.UTF8.GetString(buffer, 0, result.Count).Trim();
                app.Logger.LogInformation("Spike modtog: {Line}", line);

                // READY → PAIR 1234 (som i protokollen), alt andet sendes retur så browseren kan måle forsinkelsen
                var reply = line == "READY" ? "PAIR 1234" : $"# ekko {line}";
                await ws.SendAsync(Encoding.UTF8.GetBytes(reply), WebSocketMessageType.Text, true, ctx.RequestAborted);
            }
        });
    }
}
