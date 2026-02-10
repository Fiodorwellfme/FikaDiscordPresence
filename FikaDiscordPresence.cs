using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Net.Http.Headers;

namespace _FikaDiscordPresence;

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.fikadiscordpresence.fiodor";
    public override string Name { get; init; } = "Fika Discord Presence";
    public override string Author { get; init; } = "Fiodor";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");

    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; }
    public override string License { get; init; } = "MIT";
}

[Injectable(TypePriority = int.MaxValue)]
public class ReadJsonConfig(
    ISptLogger<ReadJsonConfig> logger,
    ModHelper modHelper) : IOnLoad
{
    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private bool ValidateConfig(ModConfig config, ISptLogger<ReadJsonConfig> logger)
    {
        var errors = new List<string>();

        // Webhook validation
        if (string.IsNullOrWhiteSpace(config.Discord.WebhookUrl))
        {
            errors.Add("Discord.WebhookUrl is missing or empty.");
        }

        // API key validation
        if (string.IsNullOrWhiteSpace(config.Fika.ApiKey))
        {
            errors.Add("Fika.ApiKey is missing or empty.");
        }

        // Log path validation (only if enabled)
        if (config.LogMonitor.Enabled)
        {
            if (string.IsNullOrWhiteSpace(config.LogMonitor.LogFolderPath))
            {
                errors.Add("LogMonitor.LogFolderPath is missing or empty.");
            }
            else if (!Directory.Exists(config.LogMonitor.LogFolderPath))
            {
                errors.Add($"LogMonitor.LogFolderPath does not exist: {config.LogMonitor.LogFolderPath}");
            }
        }

        if (errors.Count > 0)
        {
            logger.Error("Fika Discord Presence configuration error:");
            foreach (var e in errors)
            {
                logger.Error($"  - {e}");
            }

            logger.Error("Fix config.json and restart the server.");
            return false;
        }

        return true;
    }

    public Task OnLoad()
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = modHelper.GetJsonDataFromFile<ModConfig>(pathToMod, "config.json");

        if (!config.Enabled)
        {
            logger.Info("Mod disabled via config.json");
            return Task.CompletedTask;
        }

        if (!ValidateConfig(config, logger))
        {
            logger.Error("Mod will NOT start due to invalid configuration.");
            return Task.CompletedTask;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunLoop(logger, pathToMod, config);
            }
            catch (Exception e)
            {
                logger.Error($"RunLoop error: {e}");
            }
        });

        return Task.CompletedTask;
    }

    private async Task RunLoop(ISptLogger<ReadJsonConfig> logger, string pathToMod, ModConfig initialConfig)
    {
        var config = initialConfig;
        var configPath = Path.Combine(pathToMod, "config.json");

        string statePath = Path.Combine(pathToMod, config.Discord.StateFile);
        BotState state = LoadState(statePath);

        ulong? statusMessageId = null;

        if (config.Discord.StatusMessageId > 0)
        {
            statusMessageId = (ulong)config.Discord.StatusMessageId;
        }
        else if (state.StatusMessageId > 0)
        {
            statusMessageId = state.StatusMessageId;
        }

        var httpHandler = new HttpClientHandler();
        if (config.Fika.IgnoreSslErrors)
        {
            httpHandler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        using var http = new HttpClient(httpHandler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, config.Fika.TimeoutSeconds))
        };

        var baseUrl = (config.Fika.BaseUrl ?? "").Trim().TrimEnd('/');
        AuthenticationHeaderValue fikaHeaders = new("Bearer", config.Fika.ApiKey ?? "");

        var logMon = config.LogMonitor.Enabled
            ? new LogMonitorLite(
                config.LogMonitor.LogFolderPath ?? "",
                TimeSpan.FromHours(config.LogMonitor.TimezoneOffsetHours))
            : null;

        while (true)
        {
            try
            {
                // 🔁 Live reload config.json each cycle
                try
                {
                    var json = File.ReadAllText(configPath, Encoding.UTF8);
                    var reloaded = JsonSerializer.Deserialize<ModConfig>(json, _jsonOpts);
                    if (reloaded != null)
                    {
                        config = reloaded;

                        // refresh config-dependent variables
                        baseUrl = (config.Fika.BaseUrl ?? "").Trim().TrimEnd('/');
                        fikaHeaders = new AuthenticationHeaderValue("Bearer", config.Fika.ApiKey ?? "");

                        var newStatePath = Path.Combine(pathToMod, config.Discord.StateFile);
                        if (!string.Equals(newStatePath, statePath, StringComparison.OrdinalIgnoreCase))
                        {
                            statePath = newStatePath;
                            state = LoadState(statePath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Warning($"Failed to reload config.json — keeping previous config. ({ex.Message})");
                }

                if (!config.Enabled)
                {
                    await Task.Delay(5000);
                    continue;
                }

                if (logMon != null)
                {
                    logMon.Poll();
                }

                var players = await GetOnlinePlayers(http, baseUrl, fikaHeaders);
                var presence = await GetPresence(http, baseUrl, fikaHeaders);

                var presenceByNick = new Dictionary<string, PresenceEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in presence)
                {
                    if (!string.IsNullOrWhiteSpace(p.Nickname))
                        presenceByNick[p.Nickname] = p;
                }

                var embed = RenderEmbed(config, players, presenceByNick, logMon?.WeeklyBoss, logMon?.WeeklyBossMap);

                if (statusMessageId is null || statusMessageId == 0)
                {
                    var created = await WebhookCreateMessage(http, config, embed);

                    if (ulong.TryParse(created.Id, out var mid) && mid > 0)
                    {
                        statusMessageId = mid;

                        if (config.Discord.StatusMessageId <= 0)
                        {
                            state.StatusMessageId = mid;
                            SaveState(statePath, state);
                        }
                    }
                    else
                    {
                        statusMessageId = 0;
                    }
                }
                else
                {
                    try
                    {
                        await WebhookEditMessage(http, config, statusMessageId.Value, embed);
                    }
                    catch (HttpRequestException ex) when (ex.Message.Contains("404"))
                    {
                        statusMessageId = 0;
                    }
                }
            }
            catch (Exception e)
            {
                logger.Error($"Update error: {e}");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, config.Update.IntervalSeconds)));
        }
    }

    private EmbedPayload RenderEmbed(
        ModConfig config,
        List<OnlinePlayer> players,
        Dictionary<string, PresenceEntry> presenceByNick,
        string? weeklyBoss,
        string? weeklyBossMap)
    {
        var inRaid = new List<OnlinePlayer>();
        var tetris = new List<OnlinePlayer>();

        foreach (var p in players)
        {
            presenceByNick.TryGetValue(p.Nickname, out var pres);

            if (pres != null && pres.Activity == 1)
            {
                inRaid.Add(p);
            }
            else
            {
                if (p.LocationId is not (0 or 1))
                    inRaid.Add(p);
                else
                    tetris.Add(p);
            }
        }

        var embed = new EmbedPayload
        {
            Title = config.Text.Title,
            Color = config.Colors.EmbedColorDecimal,
            Fields = new List<EmbedFieldPayload>()
        };

        if (players.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(weeklyBoss))
            {
                var bossName = config.BossNames.TryGetValue(weeklyBoss!, out var bn) ? bn : weeklyBoss!;
                var mapDisp = "";

                if (!string.IsNullOrWhiteSpace(weeklyBossMap))
                {
                    mapDisp = config.MapNamesLog.TryGetValue(weeklyBossMap!, out var mn)
                        ? mn
                        : weeklyBossMap!;
                }

                var bossInfo = string.IsNullOrWhiteSpace(mapDisp)
                    ? $"**{bossName}**"
                    : $"**{bossName}** on **{mapDisp}**";

                embed.Fields.Add(new EmbedFieldPayload
                {
                    Name = config.Text.BossTitle,
                    Value = bossInfo,
                    Inline = false
                });
            }

            embed.Fields.Add(new EmbedFieldPayload
            {
                Name = "\u200b",
                Value = config.Text.NoOnlineDescription,
                Inline = false
            });

            embed.Footer = new EmbedFooterPayload
            {
                Text = $"{config.Text.FooterPrefix} {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
            };

            return embed;
        }

        if (!string.IsNullOrWhiteSpace(weeklyBoss))
        {
            var bossName = config.BossNames.TryGetValue(weeklyBoss!, out var bn) ? bn : weeklyBoss!;
            var mapDisp = "";

            if (!string.IsNullOrWhiteSpace(weeklyBossMap))
            {
                mapDisp = config.MapNamesLog.TryGetValue(weeklyBossMap!, out var mn)
                    ? mn
                    : weeklyBossMap!;
            }

            var bossInfo = string.IsNullOrWhiteSpace(mapDisp)
                ? $"**{bossName}**"
                : $"**{bossName}** on **{mapDisp}**";

            embed.Fields.Add(new EmbedFieldPayload { Name = config.Text.BossTitle, Value = bossInfo, Inline = false });
        }

        // In Raid field
        if (inRaid.Count > 0)
        {
            inRaid.Sort((a, b) => string.Compare(a.Nickname, b.Nickname, StringComparison.OrdinalIgnoreCase));
            var lines = new List<string>();

            foreach (var p in inRaid)
            {
                var mapName = config.LocationNames.TryGetValue(p.LocationId.ToString(), out var ln) ? ln : $"Unknown({p.LocationId})";
                var mapEmoji = config.MapEmoji.TryGetValue(mapName, out var me) ? me : config.Icons.DefaultMap;

                var extra = "";
                if (presenceByNick.TryGetValue(p.Nickname, out var pres) && pres != null)
                {
                    string? side = null;
                    if (pres.Activity == 1 && pres.Side is not null)
                    {
                        var key = pres.Side.Value.ToString();
                        side = config.SideNames.TryGetValue(key, out var sn) ? sn : "Unknown";
                    }

                    var since = FmtSince(pres.ActivityStartedTimestamp);
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(side)) parts.Add(side);
                    if (!string.IsNullOrWhiteSpace(since)) parts.Add(since);

                    if (parts.Count > 0)
                        extra = $" — _{string.Join(" · ", parts)}_";
                }

                lines.Add($"• **{p.Nickname}** — {mapEmoji} {mapName}{extra}");
            }

            embed.Fields.Add(new EmbedFieldPayload
            {
                Name = config.Text.InRaidTitle,
                Value = string.Join("\n", lines),
                Inline = false
            });
        }
        else
        {
            embed.Fields.Add(new EmbedFieldPayload
            {
                Name = config.Text.InRaidTitle,
                Value = config.Text.InRaidEmpty,
                Inline = false
            });
        }

        // Tetris field
        if (tetris.Count > 0)
        {
            tetris.Sort((a, b) => string.Compare(a.Nickname, b.Nickname, StringComparison.OrdinalIgnoreCase));
            var lines = new List<string>();

            foreach (var p in tetris)
            {
                if (presenceByNick.TryGetValue(p.Nickname, out var pres) && pres != null)
                {
                    var actKey = pres.Activity.ToString();
                    var activityName = config.ActivityNames.TryGetValue(actKey, out var an) ? an : $"Activity({pres.Activity})";
                    var since = FmtSince(pres.ActivityStartedTimestamp);

                    var detail = string.IsNullOrWhiteSpace(since) ? activityName : $"{activityName} · {since}";
                    var icon = pres.Activity == 3 ? "🏠" : pres.Activity == 0 ? "📋" : pres.Activity == 2 ? "🧰" : pres.Activity == 4 ? "🛒" : "🧩";

                    lines.Add($"• **{p.Nickname}** — {icon} {detail}");
                }
                else
                {
                    var loc = config.LocationNames.TryGetValue(p.LocationId.ToString(), out var ln) ? ln : $"Unknown({p.LocationId})";
                    var detail = loc == "Hideout" ? "🏠 Hideout" : "📋 Menu";
                    lines.Add($"• **{p.Nickname}** — {detail}");
                }
            }

            embed.Fields.Add(new EmbedFieldPayload
            {
                Name = config.Text.OutOfRaidTitle,
                Value = string.Join("\n", lines),
                Inline = false
            });
        }
        else
        {
            embed.Fields.Add(new EmbedFieldPayload
            {
                Name = config.Text.OutOfRaidTitle,
                Value = config.Text.OutOfRaidEmpty,
                Inline = false
            });
        }

        var countsLine = $"{players.Count} Online | {inRaid.Count} In Raid | {tetris.Count} Playing Tetris";
        embed.Fields.Add(new EmbedFieldPayload { Name = "\u200b", Value = $"**{countsLine}**", Inline = false });

        embed.Footer = new EmbedFooterPayload { Text = $"{config.Text.FooterPrefix} {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        return embed;
    }

    private static string FmtSince(long startedTs)
    {
        if (startedTs <= 0) return "";

        try
        {
            var started = DateTimeOffset.FromUnixTimeSeconds(startedTs).LocalDateTime;
            var delta = DateTime.Now - started;
            if (delta.TotalSeconds < 0) return "";

            var mins = (int)(delta.TotalSeconds / 60);
            if (mins < 1) return "just now";
            if (mins < 60) return $"{mins}m";

            var hrs = mins / 60;
            var rem = mins % 60;
            return $"{hrs}h{rem:00}m";
        }
        catch
        {
            return "";
        }
    }

    private async Task<List<OnlinePlayer>> GetOnlinePlayers(HttpClient http, string baseUrl, AuthenticationHeaderValue auth)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/fika/api/players");
        req.Headers.Authorization = auth;
        req.Headers.Add("responsecompressed", "0");

        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<PlayersResponse>(json, _jsonOpts);

        var outList = new List<OnlinePlayer>();
        if (data?.Players != null)
        {
            foreach (var p in data.Players)
            {
                outList.Add(new OnlinePlayer
                {
                    ProfileId = p.ProfileId ?? "",
                    Nickname = p.Nickname ?? "",
                    LocationId = (int)p.Location
                });
            }
        }

        return outList;
    }

    private async Task<List<PresenceEntry>> GetPresence(HttpClient http, string baseUrl, AuthenticationHeaderValue auth)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/fika/presence/get");
        req.Headers.Authorization = auth;
        req.Headers.Add("responsecompressed", "0");

        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync();

        var outList = new List<PresenceEntry>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return outList;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                try
                {
                    var nick = el.TryGetProperty("nickname", out var nEl) ? nEl.GetString() ?? "" : "";
                    var level = el.TryGetProperty("level", out var lEl) ? lEl.GetInt32() : 0;
                    var activity = el.TryGetProperty("activity", out var aEl) ? aEl.GetInt32() : 0;
                    var started = el.TryGetProperty("activityStartedTimestamp", out var sEl) ? sEl.GetInt64() : 0;

                    int? side = null;
                    if (el.TryGetProperty("raidInformation", out var rEl) && rEl.ValueKind == JsonValueKind.Object)
                    {
                        if (rEl.TryGetProperty("side", out var sideEl) && sideEl.ValueKind is JsonValueKind.Number)
                        {
                            side = sideEl.GetInt32();
                        }
                    }

                    outList.Add(new PresenceEntry
                    {
                        Nickname = nick,
                        Level = level,
                        Activity = activity,
                        ActivityStartedTimestamp = started,
                        Side = side
                    });
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore
        }

        return outList;
    }

    private async Task<WebhookMessage> WebhookCreateMessage(HttpClient http, ModConfig config, EmbedPayload embed)
    {
        var url = (config.Discord.WebhookUrl ?? "").Trim();

        // wait=true makes Discord return the created message JSON
        var postUrl = url.Contains('?') ? $"{url}&wait=true" : $"{url}?wait=true";

        var payload = new WebhookSendPayload
        {
            Content = null,
            Username = config.Discord.Username,
            AvatarUrl = config.Discord.AvatarUrl,
            Tts = config.Discord.Tts,
            Embeds = new List<EmbedPayload> { embed }
        };

        var body = JsonSerializer.Serialize(payload, _jsonOpts);
        using var resp = await http.PostAsync(postUrl, new StringContent(body, Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync();
        var msg = JsonSerializer.Deserialize<WebhookMessage>(json, _jsonOpts);
        return msg ?? new WebhookMessage();
    }

    private async Task WebhookEditMessage(HttpClient http, ModConfig config, ulong messageId, EmbedPayload embed)
    {
        var url = (config.Discord.WebhookUrl ?? "").Trim().TrimEnd('/');
        var patchUrl = $"{url}/messages/{messageId}";

        var payload = new WebhookSendPayload
        {
            Content = null,
            Username = config.Discord.Username,
            AvatarUrl = config.Discord.AvatarUrl,
            Tts = config.Discord.Tts,
            Embeds = new List<EmbedPayload> { embed }
        };

        var body = JsonSerializer.Serialize(payload, _jsonOpts);

        var req = new HttpRequestMessage(new HttpMethod("PATCH"), patchUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    private static BotState LoadState(string path)
    {
        try
        {
            if (!File.Exists(path)) return new BotState();
            var json = File.ReadAllText(path, Encoding.UTF8);
            var s = JsonSerializer.Deserialize<BotState>(json);
            return s ?? new BotState();
        }
        catch
        {
            return new BotState();
        }
    }

    private static void SaveState(string path, BotState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        catch
        {
            // ignore
        }
    }
}

public record ModConfig
{
    public bool Enabled { get; set; }

    public DiscordConfig Discord { get; set; } = new();
    public FikaConfig Fika { get; set; } = new();
    public UpdateConfig Update { get; set; } = new();
    public LogMonitorConfig LogMonitor { get; set; } = new();

    public TextConfig Text { get; set; } = new();
    public ColorConfig Colors { get; set; } = new();
    public IconConfig Icons { get; set; } = new();

    public Dictionary<string, string> MapEmoji { get; set; } = new();
    public Dictionary<string, string> LocationNames { get; set; } = new();
    public Dictionary<string, string> ActivityNames { get; set; } = new();
    public Dictionary<string, string> SideNames { get; set; } = new();
    public Dictionary<string, string> BossNames { get; set; } = new();
    public Dictionary<string, string> MapNamesLog { get; set; } = new();
}

public record DiscordConfig
{
    public string WebhookUrl { get; set; } = "";
    public string Username { get; set; } = "Fika Status";
    public string AvatarUrl { get; set; } = "";
    public bool Tts { get; set; }
    public long StatusMessageId { get; set; }
    public string StateFile { get; set; } = "bot_state.json";
}

public record FikaConfig
{
    public string BaseUrl { get; set; } = "https://127.0.0.1:6969";
    public string ApiKey { get; set; } = "";
    public bool IgnoreSslErrors { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 10;
}

public record UpdateConfig
{
    public int IntervalSeconds { get; set; } = 30;
}

public record LogMonitorConfig
{
    public bool Enabled { get; set; }
    public string LogFolderPath { get; set; } = "";
    public int TimezoneOffsetHours { get; set; } = 1;
}

public record TextConfig
{
    public string Title { get; set; } = "Fika Server Status";
    public string NoOnlineDescription { get; set; } = "🌵💨 _No one is online right now._";
    public string InRaidTitle { get; set; } = "⚔️ In Raid";
    public string InRaidEmpty { get; set; } = "_Nobody currently in raid._";
    public string OutOfRaidTitle { get; set; } = "🧩 Playing Tetris";
    public string OutOfRaidEmpty { get; set; } = "_Everyone online is in raid._";
    public string BossTitle { get; set; } = "👑 Boss of the Week";
    public string FooterPrefix { get; set; } = "Last updated:";
}

public record ColorConfig
{
    public int EmbedColorDecimal { get; set; } = 3447003;
}

public record IconConfig
{
    public string InRaid { get; set; } = "⚔️";
    public string OutOfRaid { get; set; } = "🧩";
    public string DefaultMap { get; set; } = "🗺️";
}

// --- Fika response models ---
public class PlayersResponse
{
    [JsonPropertyName("players")]
    public List<PlayerEntry>? Players { get; set; }
}

public class PlayerEntry
{
    [JsonPropertyName("profileId")]
    public string? ProfileId { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("location")]
    public int Location { get; set; }
}

public class OnlinePlayer
{
    public string ProfileId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public int LocationId { get; set; }
}

public class PresenceEntry
{
    public string Nickname { get; set; } = "";
    public int Level { get; set; }
    public int Activity { get; set; }
    public long ActivityStartedTimestamp { get; set; }
    public int? Side { get; set; }
}

// --- Webhook payload models ---
public class WebhookSendPayload
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("tts")]
    public bool Tts { get; set; }

    [JsonPropertyName("embeds")]
    public List<EmbedPayload>? Embeds { get; set; }
}

public class WebhookMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "0";
}

public class EmbedPayload
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("color")]
    public int Color { get; set; }

    [JsonPropertyName("fields")]
    public List<EmbedFieldPayload>? Fields { get; set; }

    [JsonPropertyName("footer")]
    public EmbedFooterPayload? Footer { get; set; }
}

public class EmbedFieldPayload
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("inline")]
    public bool Inline { get; set; }
}

public class EmbedFooterPayload
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

// --- State file model ---
public class BotState
{
    [JsonPropertyName("status_message_id")]
    public ulong StatusMessageId { get; set; }
}

// --- Log monitor (weekly boss) ---
public class LogMonitorLite
{
    private readonly string _logFolderPath;
    private readonly TimeSpan _tzOffset; // currently unused but kept for future
    private string? _logFilePath;
    private long _pos;

    public string? WeeklyBoss { get; private set; }
    public string? WeeklyBossMap { get; private set; }

    // IMPORTANT: values must match your config.MapNamesLog keys (lowercase, underscores)
    private static readonly Dictionary<string, string> BossToMapKey =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["bossBully"]    = "bigmap",
            ["bossGluhar"]   = "rezervbase",
            ["bossKilla"]    = "interchange",
            ["bossKojaniy"]  = "woods",
            ["bossSanitar"]  = "shoreline",
            ["bossKolontay"] = "tarkovstreets",
            ["bossKnight"]   = "lighthouse",
            ["bossTagilla"]  = "factory4_day",
        };

    public LogMonitorLite(string logFolderPath, TimeSpan tzOffset)
    {
        _logFolderPath = logFolderPath;
        _tzOffset = tzOffset;
        InitFile();
    }

    private void InitFile()
    {
        _logFilePath = FindLatestLog();
        if (_logFilePath == null) return;

        try
        {
            using var fs = new FileStream(
                _logFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                ProcessLine(line, initialLoad: true);
            }

            // start tailing from end
            _pos = fs.Position;
        }
        catch
        {
            _logFilePath = null;
        }
    }

    private string? FindLatestLog()
    {
        if (string.IsNullOrWhiteSpace(_logFolderPath)) return null;
        if (!Directory.Exists(_logFolderPath)) return null;

        var files = Directory.GetFiles(_logFolderPath, "spt*.log");
        if (files.Length == 0) return null;

        string latest = files[0];
        DateTime latestTime = File.GetLastWriteTimeUtc(latest);

        foreach (var f in files)
        {
            var t = File.GetLastWriteTimeUtc(f);
            if (t > latestTime)
            {
                latestTime = t;
                latest = f;
            }
        }

        return latest;
    }

    public void Poll()
    {
        if (_logFilePath == null || !File.Exists(_logFilePath))
        {
            InitFile();
            return;
        }

        try
        {
            using var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(_pos, SeekOrigin.Begin);

            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    ProcessLine(line, initialLoad: false);
            }

            _pos = fs.Position;
        }
        catch
        {
            // ignore
        }
    }

    private void ProcessLine(string line, bool initialLoad)
    {
        // 1) ABPS/acidbotplacementsystem line (best signal: includes map)
        if (line.Contains("Weekly Boss:") && line.Contains("_botplacementsystem"))
        {
            var m = Regex.Match(
                line,
                @"Weekly Boss:\s+(boss\w+)\s+\|\s+\d+%\s+Chance\s+on\s+(\w+)",
                RegexOptions.CultureInvariant);

            if (m.Success)
            {
                WeeklyBoss = m.Groups[1].Value;
                WeeklyBossMap = m.Groups[2].Value;
            }

            return;
        }

        // 2) Fallback: core SPT line (no map)
        // Example: "[...][Debug][SPTarkov.Server.Core.Services.PostDbLoadService] bossTagilla is boss of the week"
        if (line.IndexOf(" is boss of the week", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        var m2 = Regex.Match(
            line,
            @"\b(boss\w+)\b\s+is\s+boss\s+of\s+the\s+week\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!m2.Success)
            return;

        // Don't overwrite ABPS-derived boss+map
        if (!string.IsNullOrWhiteSpace(WeeklyBoss) && !string.IsNullOrWhiteSpace(WeeklyBossMap))
            return;

        WeeklyBoss = m2.Groups[1].Value;

        // Derive map key to match config.MapNamesLog keys
        if (BossToMapKey.TryGetValue(WeeklyBoss, out var mapKey))
            WeeklyBossMap = mapKey;
        else
            WeeklyBossMap = null;
    }
}
