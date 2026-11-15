using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace OverlayAI.Services;

/// <summary>.env + environment variable reader.</summary>
internal static class Env
{
    private static readonly Dictionary<string, string> FileVars = Load();

    private static IEnumerable<string> Paths()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        yield return Path.Combine(exeDir, ".env");
        yield return Path.Combine(AppConfig.Dir, ".env");
        yield return Path.Combine(Environment.CurrentDirectory, ".env");
    }

    private static Dictionary<string, string> Load()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Paths())
        {
            if (!File.Exists(p)) continue;
            foreach (var raw in File.ReadAllLines(p))
            {
                var l = raw.Trim();
                if (l.Length == 0 || l[0] == '#') continue;
                int i = l.IndexOf('=');
                if (i <= 0) continue;
                d.TryAdd(l[..i].Trim(), l[(i + 1)..].Trim().Trim('"', '\''));
            }
        }
        return d;
    }

    public static string? Get(string key)
    {
        if (FileVars.TryGetValue(key, out var v) && v.Length > 0) return v;
        var e = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrEmpty(e) ? null : e;
    }
}

public sealed class AppConfig
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OverlayAI");
    private static string FilePath => Path.Combine(Dir, "config.json");

    public static readonly Dictionary<string, (string Url, string Model)> Defaults = new()
    {
        ["openai"]    = ("https://api.openai.com/v1", "gpt-4o"),
        ["anthropic"] = ("https://api.anthropic.com", "claude-sonnet-5-5"),
        ["gemini"]    = ("https://generativelanguage.googleapis.com", "gemini-3.8-flash"),
        ["ollama"]    = ("http://localhost:11434", "llama3.2-vision"),
    };

    // Property initializers read .env, so config.json only overrides what it actually contains.
    public string Provider { get; set; } = Env.Get("AI_PROVIDER") ?? "ollama";
    public string Model { get; set; } = Env.Get("AI_MODEL") ?? "";
    public string BaseUrl { get; set; } = Env.Get("AI_BASE_URL") ?? "";
    [JsonIgnore] public string ApiKey { get; set; } = Env.Get("AI_API_KEY") ?? "";
    [JsonIgnore] public bool ApiKeyProtectedOnDisk { get; private set; }
    public double Opacity { get; set; } = 0.92;
    public bool FastMode { get; set; } = true;
    /// <summary>Keeps this window out of screen recordings and streams (OBS, Discord, screenshots). Toggle: Ctrl+Shift+H.</summary>
    public bool HideFromCapture { get; set; } = true;
    /// <summary>Removes the taskbar button (and Alt+Tab entry). Needs a restart to change; edit config.json to turn off.</summary>
    public bool HideTaskbarButton { get; set; } = true;

    public string SystemPrompt { get; set; } =
        "You are a fast, concise assistant for coding practice, interview preparation and productivity. " +
        "For coding problems: state the approach in a few lines, give a complete solution in a fenced code block " +
        "with a language tag, then time/space complexity. Use Markdown. No filler.";

    public string ScreenshotPrompt { get; set; } =
        "Analyze this screenshot. If it shows a coding problem, explain the approach and give a complete, " +
        "correct solution with complexity. If it shows an error, diagnose and fix it. Otherwise summarize " +
        "what matters and suggest next steps.";

    [JsonIgnore] public string P => Defaults.ContainsKey(Provider.ToLowerInvariant()) ? Provider.ToLowerInvariant() : "openai";
    [JsonIgnore] public string EffectiveBaseUrl => (string.IsNullOrWhiteSpace(BaseUrl) ? Defaults[P].Url : BaseUrl).TrimEnd('/');
    [JsonIgnore] public string EffectiveModel => string.IsNullOrWhiteSpace(Model) ? Defaults[P].Model : Model.Trim();

    public string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey)) return ApiKey.Trim();
        return (P switch
        {
            "anthropic" => Env.Get("ANTHROPIC_API_KEY"),
            "gemini" => Env.Get("GEMINI_API_KEY") ?? Env.Get("GOOGLE_API_KEY"),
            "openai" => Env.Get("OPENAI_API_KEY"),
            _ => null
        }) ?? "";
    }

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppConfig();

            var json = File.ReadAllText(FilePath);
            var config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // New config files store API keys only as DPAPI-protected data.
            if (root.TryGetProperty("ApiKeyProtected", out var protectedElement) &&
                protectedElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(protectedElement.GetString()))
            {
                try
                {
                    config.ApiKey = CurrentUserProtection.UnprotectFromBase64(protectedElement.GetString()!);
                    config.ApiKeyProtectedOnDisk = true;
                }
                catch
                {
                    // A different Windows user profile cannot decrypt CurrentUser DPAPI data.
                    // Do not fall back to plaintext or silently overwrite the protected value.
                    config.ApiKey = "";
                    config.ApiKeyProtectedOnDisk = false;
                }
            }
            else if (root.TryGetProperty("ApiKey", out var legacyElement) && legacyElement.ValueKind == JsonValueKind.String)
            {
                // One-time migration from older releases that stored the API key as plaintext.
                config.ApiKey = legacyElement.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(config.ApiKey)) config.Save();
            }

            return config;
        }
        catch { /* corrupt or unreadable config -> defaults */ }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var node = JsonSerializer.SerializeToNode(this, new JsonSerializerOptions { WriteIndented = true }) as JsonObject
                       ?? new JsonObject();

            // Never serialize ApiKey in plaintext. DPAPI uses the current Windows user profile.
            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                node["ApiKeyProtected"] = CurrentUserProtection.ProtectToBase64(ApiKey);
                ApiKeyProtectedOnDisk = true;
            }
            else
            {
                node["ApiKeyProtected"] = "";
                ApiKeyProtectedOnDisk = false;
            }

            File.WriteAllText(FilePath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Do not fall back to writing credentials in plaintext if DPAPI is unavailable.
            ApiKeyProtectedOnDisk = false;
        }
    }
}
