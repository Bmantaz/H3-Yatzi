using System.Net.Http.Json;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>
/// Bygger requesten fra et eksplicit snapshot og spiller-id. Læser aldrig selv serveren
/// og opdaterer aldrig gammel kontekst. Null-snapshot giver gyldigt format til 404-/rutetests.
/// Samtidighedstests fastholder deres snapshot før opgaverne frigives.
/// </summary>
internal static class GameHttpActions
{
    internal static GameActionContext Context(GameStateResponse? state, Guid? playerId = null) =>
        new(state?.GameId ?? Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), state?.TurnNumber ?? 1, playerId);

    internal static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, GameStateResponse? state,
        Guid? playerId = null, CancellationToken cancellationToken = default) =>
        client.PostAsJsonAsync(path, Context(state, playerId), cancellationToken);
}
