using System.Net;
using System.Text;
using System.Text.Json;

namespace A18.Realtime.Api;

internal sealed class Runtime21Bridge(HttpClient client)
{
    public Task<IResult> GetAsync(string path, CancellationToken cancellationToken) =>
        ForwardAsync(HttpMethod.Get, path, null, cancellationToken);

    public Task<IResult> PostAsync(string path, JsonElement? body, CancellationToken cancellationToken) =>
        ForwardAsync(HttpMethod.Post, path, body, cancellationToken);

    public async Task<IResult> GetCollectionItemAsync(string path, Guid id, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(path, cancellationToken);
            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Content(response.StatusCode, content, response.Content.Headers.ContentType?.ToString());

            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out JsonElement value) &&
                        Guid.TryParse(value.GetString(), out Guid candidate) && candidate == id)
                        return Results.Content(item.GetRawText(), "application/json; charset=utf-8", Encoding.UTF8);
                }
            }

            return Results.NotFound(new { error = $"Runtime-21 item {id} was not found." });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Unavailable(ex);
        }
    }

    private async Task<IResult> ForwardAsync(
        HttpMethod method,
        string path,
        JsonElement? body,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is { } json)
                request.Content = new StringContent(json.GetRawText(), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            return Content(response.StatusCode, content, response.Content.Headers.ContentType?.ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Unavailable(ex);
        }
    }

    private static IResult Content(HttpStatusCode status, string content, string? contentType) =>
        Results.Content(content, contentType ?? "application/json; charset=utf-8", Encoding.UTF8, (int)status);

    private static IResult Unavailable(Exception ex) => Results.Json(new
    {
        error = "A18_21 runtime is unavailable.",
        detail = ex is TaskCanceledException ? "The bridge request timed out." : "The bridge could not reach Runtime-21."
    }, statusCode: StatusCodes.Status503ServiceUnavailable);
}
