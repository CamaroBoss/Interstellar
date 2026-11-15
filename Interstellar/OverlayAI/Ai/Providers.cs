using System.Net;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OverlayAI.Services;

namespace OverlayAI.Ai;

public record ChatMessage(string Role, string Text, string? ImageBase64 = null, string ImageMime = "image/jpeg");

public interface IAiProvider
{
    IAsyncEnumerable<string> StreamAsync(string system, IReadOnlyList<ChatMessage> history, CancellationToken ct = default);
}

internal static class Net
{
    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(3),
        MaxConnectionsPerServer = 8,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { Timeout = Timeout.InfiniteTimeSpan };

        public static async Task<HttpResponseMessage> PostStreamAsync(
        string url, object body, Action<HttpRequestMessage>? configure, CancellationToken ct)
    {
        const int maxAttempts = 4;
        var json = JsonSerializer.Serialize(body);
    
        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            configure?.Invoke(req);
    
            var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode) return resp;
    
            var status = resp.StatusCode;
            var reason = resp.ReasonPhrase;
            var retryAfter = resp.Headers.RetryAfter?.Delta;
            var err = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
    
            bool transient = status is HttpStatusCode.ServiceUnavailable
                                    or HttpStatusCode.BadGateway
                                    or HttpStatusCode.GatewayTimeout;
    
            if (transient && attempt < maxAttempts)
            {
                var delay = retryAfter ?? TimeSpan.FromMilliseconds(
                    800 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 300));
                if (delay > TimeSpan.FromSeconds(8)) delay = TimeSpan.FromSeconds(8);
                await Task.Delay(delay, ct);
                continue;
            }
    
            if (err.Length > 600) err = err[..600] + "…";
            throw new HttpRequestException($"{(int)status} {reason}: {err}");
        }
    }

    public static async IAsyncEnumerable<string> Lines(HttpResponseMessage resp, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var r = new StreamReader(s, Encoding.UTF8);
        while (true)
        {
            var line = await r.ReadLineAsync(ct);
            if (line is null) yield break;
            yield return line;
        }
    }

    public static string RequireKey(AppConfig c)
    {
        var k = c.ResolveApiKey();
        if (k.Length == 0)
            throw new InvalidOperationException($"No API key for '{c.P}'. Open âš™ Settings or set it in .env.");
        return k;
    }
}

/// <summary>OpenAI and anything OpenAI-compatible (OpenRouter, LM Studio, vLLM, Azure-style gateways...).</summary>
public sealed class OpenAiProvider(AppConfig c) : IAiProvider
{
    public async IAsyncEnumerable<string> StreamAsync(string system, IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var key = c.ResolveApiKey(); // may be empty for local OpenAI-compatible servers
        var msgs = new List<object> { new { role = "system", content = system } };
        foreach (var m in history)
        {
            if (m.ImageBase64 is null) msgs.Add(new { role = m.Role, content = m.Text });
            else msgs.Add(new
            {
                role = m.Role,
                content = new object[]
                {
                    new { type = "text", text = m.Text },
                    new { type = "image_url", image_url = new { url = $"data:{m.ImageMime};base64,{m.ImageBase64}" } }
                }
            });
        }

        using var resp = await Net.PostStreamAsync(
            c.EffectiveBaseUrl + "/chat/completions",
            new { model = c.EffectiveModel, messages = msgs, stream = true, max_tokens = c.FastMode ? 2048 : 4096 },
            r => { if (key.Length > 0) r.Headers.Authorization = new("Bearer", key); }, ct);

        await foreach (var line in Net.Lines(resp, ct))
        {
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") yield break;
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0 &&
                ch[0].TryGetProperty("delta", out var d) &&
                d.TryGetProperty("content", out var t) && t.ValueKind == JsonValueKind.String)
                yield return t.GetString()!;
        }
    }
}

public sealed class AnthropicProvider(AppConfig c) : IAiProvider
{
    public async IAsyncEnumerable<string> StreamAsync(string system, IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var key = Net.RequireKey(c);
        var msgs = new List<object>();
        foreach (var m in history)
        {
            if (m.ImageBase64 is null) msgs.Add(new { role = m.Role, content = m.Text });
            else msgs.Add(new
            {
                role = m.Role,
                content = new object[]
                {
                    new { type = "image", source = new { type = "base64", media_type = m.ImageMime, data = m.ImageBase64 } },
                    new { type = "text", text = m.Text }
                }
            });
        }

        using var resp = await Net.PostStreamAsync(
            c.EffectiveBaseUrl + "/v1/messages",
            new { model = c.EffectiveModel, max_tokens = c.FastMode ? 2048 : 8192, system, messages = msgs, stream = true },
            r => { r.Headers.Add("x-api-key", key); r.Headers.Add("anthropic-version", "2023-06-01"); }, ct);

        await foreach (var line in Net.Lines(resp, ct))
        {
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;
            if (type == "content_block_delta" && root.TryGetProperty("delta", out var d) &&
                d.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                yield return t.GetString()!;
            else if (type == "error")
                throw new InvalidOperationException(root.GetProperty("error").GetProperty("message").GetString());
            else if (type == "message_stop")
                yield break;
        }
    }
}

public sealed class GeminiProvider(AppConfig c) : IAiProvider
{
    public async IAsyncEnumerable<string> StreamAsync(string system, IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var key = Net.RequireKey(c);
        var contents = new List<object>();
        foreach (var m in history)
        {
            var parts = new List<object> { new { text = m.Text } };
            if (m.ImageBase64 is not null)
                parts.Add(new { inlineData = new { mimeType = m.ImageMime, data = m.ImageBase64 } });
            contents.Add(new { role = m.Role == "assistant" ? "model" : "user", parts });
        }

        var url = $"{c.EffectiveBaseUrl}/v1beta/models/{c.EffectiveModel}:streamGenerateContent?alt=sse";
        using var resp = await Net.PostStreamAsync(url,
            new { systemInstruction = new { parts = new[] { new { text = system } } }, contents, generationConfig = new { maxOutputTokens = c.FastMode ? 2048 : 8192, thinkingConfig = new { thinkingBudget = 0 } } },
            r => r.Headers.Add("x-goog-api-key", key), ct);

        await foreach (var line in Net.Lines(resp, ct))
        {
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0) continue;
            if (!cands[0].TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var ps)) continue;
            foreach (var p in ps.EnumerateArray())
                if (p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String &&
                    !(p.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True))
                    yield return t.GetString()!;
        }
    }
}

/// <summary>Native Ollama /api/chat (NDJSON). Fully local: nothing leaves the machine.</summary>
public sealed class OllamaProvider(AppConfig c) : IAiProvider
{
    public async IAsyncEnumerable<string> StreamAsync(string system, IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var msgs = new List<object> { new { role = "system", content = system } };
        foreach (var m in history)
        {
            if (m.ImageBase64 is null) msgs.Add(new { role = m.Role, content = m.Text });
            else msgs.Add(new { role = m.Role, content = m.Text, images = new[] { m.ImageBase64 } });
        }

        using var resp = await Net.PostStreamAsync(
            c.EffectiveBaseUrl + "/api/chat",
            new { model = c.EffectiveModel, messages = msgs, stream = true, options = new { num_predict = c.FastMode ? 1536 : 4096 } }, null, ct);

        await foreach (var line in Net.Lines(resp, ct))
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException(err.GetString());
            if (root.TryGetProperty("message", out var m) &&
                m.TryGetProperty("content", out var t) && t.ValueKind == JsonValueKind.String)
            {
                var s = t.GetString();
                if (!string.IsNullOrEmpty(s)) yield return s;
            }
            if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True) yield break;
        }
    }
}

public static class ProviderFactory
{
    public static IAiProvider Create(AppConfig c) => c.P switch
    {
        "anthropic" => new AnthropicProvider(c),
        "gemini" => new GeminiProvider(c),
        "ollama" => new OllamaProvider(c),
        _ => new OpenAiProvider(c),
    };
}
