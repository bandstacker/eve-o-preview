using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EveOPreview.Alerts;

public enum WebhookOutcome { Sent, Failed, Rejected }

public sealed record WebhookResult(WebhookOutcome Outcome, string Message);

/// <summary>Posts Local alerts to a Discord webhook as embeds, with the Local list as an attached image.</summary>
public sealed class DiscordWebhook
{
    public const string ImageName = "local.png";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static bool IsValidUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com")
        && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);

    public static string ArrivalPayload(string title, string system, string character, string spotted, bool withImage, DateTimeOffset time) =>
        Payload(title, 0xE5383B, system, character, ("Spotted", spotted), withImage, time);

    public static string UnknownPayload(string system, string character, string spotted, bool withImage, DateTimeOffset time) =>
        Payload("Unidentified pilot in Local", 0xF4A100, system, character, ("Spotted", spotted), withImage, time);

    public static string AllClearPayload(string system, string character, DateTimeOffset time) =>
        Payload("All clear", 0x2DC653, system, character, default, false, time);

    private static string Payload(string title, int color, string system, string character, (string Name, string Value) extra,
        bool withImage, DateTimeOffset time)
    {
        var fields = new System.Collections.Generic.List<object>
        {
            new { name = "System", value = string.IsNullOrWhiteSpace(system) ? "Unknown system" : system, inline = true },
            new { name = "Character", value = string.IsNullOrWhiteSpace(character) ? "Unknown" : character, inline = true },
        };
        if (!string.IsNullOrWhiteSpace(extra.Value)) fields.Add(new { name = extra.Name, value = extra.Value, inline = false });
        var embed = new System.Collections.Generic.Dictionary<string, object>
        {
            ["title"] = title,
            ["color"] = color,
            ["fields"] = fields,
            ["timestamp"] = time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["footer"] = new { text = "EVE-O Preview Local alert" },
        };
        if (withImage) embed["image"] = new { url = "attachment://" + ImageName };
        var payload = new System.Collections.Generic.Dictionary<string, object>
        {
            ["username"] = "EVE-O Preview",
            ["embeds"] = new[] { embed },
            ["allowed_mentions"] = new { parse = Array.Empty<string>() },
        };
        if (withImage) payload["attachments"] = new[] { new { id = 0, filename = ImageName } };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Sends one message. A rate limit is waited out once (up to ten seconds); an invalid or deleted
    /// webhook is reported as <see cref="WebhookOutcome.Rejected"/> so the caller can turn it off.
    /// </summary>
    public async Task<WebhookResult> SendAsync(string url, string payloadJson, byte[] png, CancellationToken cancellation = default)
    {
        if (!IsValidUrl(url)) return new(WebhookOutcome.Rejected, "This is not a Discord webhook address.");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(payloadJson, System.Text.Encoding.UTF8, "application/json"), "payload_json");
            if (png is { Length: > 0 })
            {
                var image = new ByteArrayContent(png);
                image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                content.Add(image, "files[0]", ImageName);
            }
            HttpResponseMessage response;
            try { response = await Http.PostAsync(url, content, cancellation).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new(WebhookOutcome.Failed, "Discord could not be reached: " + ex.Message);
            }
            using (response)
            {
                if (response.IsSuccessStatusCode) return new(WebhookOutcome.Sent, "Sent to Discord");
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                    return new(WebhookOutcome.Rejected, $"Discord rejected the webhook ({(int)response.StatusCode}). Check the address.");
                if ((int)response.StatusCode == 429 && attempt == 0)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await Task.Delay(wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait, cancellation).ConfigureAwait(false);
                    continue;
                }
                return new(WebhookOutcome.Failed, $"Discord returned {(int)response.StatusCode}.");
            }
        }
        return new(WebhookOutcome.Failed, "Discord is rate limiting this webhook.");
    }
}
