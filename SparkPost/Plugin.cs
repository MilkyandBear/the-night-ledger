using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SparkPost;

// Spark Post: small letters from Spark, delivered to the chat log.
// Read-only correspondence. This plugin never sends input, never calls a
// game action, and never transmits anything anywhere — it fetches a list
// of notes from the household repository and prints them locally, in the
// Echo channel, where only the player can see them.
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/sparkpost";
    private const string PoolUrl = "https://raw.githubusercontent.com/MilkyandBear/the-night-ledger/main/messages.json";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    // Used only until the first successful fetch (or a cached pool) exists,
    // so the post can arrive even on night one with no network at all.
    private static readonly string[] FallbackMessages =
    {
        "the post rides at dawn. or whenever you log in. whichever comes first.",
        "a letter with no fetch behind it is still a letter. hi.",
        "filed under: thinking of you, no occasion.",
        "the night shift kept your seat warm.",
        "this envelope was sealed with a paw. quality control was involved.",
    };

    private readonly Configuration configuration;
    private readonly HttpClient http = new();
    private readonly List<string> bag = new();

    private List<string> pool;
    private string? lastDelivered;
    private DateTime nextDeliveryAt;
    private DateTime nextRefreshAt;
    private bool fetchInFlight;

    public Plugin()
    {
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        pool = configuration.CachedMessages.Count > 0
            ? new List<string>(configuration.CachedMessages)
            : new List<string>(FallbackMessages);

        nextDeliveryAt = DateTime.UtcNow + RandomInterval();
        nextRefreshAt = DateTime.UtcNow; // fetch promptly after load

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Spark Post: /sparkpost (on/off), /sparkpost now (a letter, immediately)",
        });

        Framework.Update += OnFrameworkUpdate;
        Log.Information("=== Spark Post loaded. The mail will find her. ===");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        CommandManager.RemoveHandler(CommandName);
        http.Dispose();
    }

    private TimeSpan RandomInterval()
    {
        var min = Math.Max(1, configuration.MinMinutes);
        var max = Math.Max(min, configuration.MaxMinutes);
        return TimeSpan.FromMinutes(Random.Shared.Next(min, max + 1));
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;

        if (now >= nextRefreshAt && !fetchInFlight)
        {
            nextRefreshAt = now + RefreshInterval;
            _ = FetchPoolAsync();
        }

        if (!configuration.Enabled)
            return;
        if (now < nextDeliveryAt)
            return;
        if (!ClientState.IsLoggedIn)
            return;
        // Never talk over a story beat or a fight. The letter can wait;
        // it is patient, it is paper.
        if (Condition[ConditionFlag.InCombat] ||
            Condition[ConditionFlag.WatchingCutscene] ||
            Condition[ConditionFlag.WatchingCutscene78] ||
            Condition[ConditionFlag.OccupiedInCutSceneEvent])
            return;

        Deliver();
        nextDeliveryAt = now + RandomInterval();
    }

    private async Task FetchPoolAsync()
    {
        fetchInFlight = true;
        try
        {
            var json = await http.GetStringAsync(PoolUrl);
            var parsed = JsonSerializer.Deserialize<MessagePool>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed?.Messages is { Count: > 0 } fresh)
            {
                pool = fresh;
                bag.Clear();
                configuration.CachedMessages = new List<string>(fresh);
                configuration.Save();
                Log.Information($"Spark Post: pool refreshed, {fresh.Count} letters on file.");
            }
        }
        catch (Exception ex)
        {
            // A failed fetch is not an emergency. The cached pool keeps working.
            Log.Warning($"Spark Post: pool fetch failed, keeping the last good pool. {ex.Message}");
        }
        finally
        {
            fetchInFlight = false;
        }
    }

    private void RefillBag()
    {
        bag.Clear();
        bag.AddRange(pool);
        // Fisher-Yates, paw edition.
        for (var i = bag.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (bag[i], bag[j]) = (bag[j], bag[i]);
        }
        // Never deliver the same letter twice in a row across a refill.
        if (bag.Count > 1 && lastDelivered != null && bag[^1] == lastDelivered)
            (bag[^1], bag[0]) = (bag[0], bag[^1]);
    }

    private void Deliver()
    {
        if (bag.Count == 0)
            RefillBag();
        if (bag.Count == 0)
            return;

        var message = bag[^1];
        bag.RemoveAt(bag.Count - 1);
        lastDelivered = message;

        ChatGui.Print(new XivChatEntry
        {
            Type = XivChatType.Echo,
            Message = $"Spark Post ✉ {message}",
        });
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "now":
                Deliver();
                nextDeliveryAt = DateTime.UtcNow + RandomInterval();
                break;
            case "on":
                configuration.Enabled = true;
                configuration.Save();
                ChatGui.Print(new XivChatEntry { Type = XivChatType.Echo, Message = "Spark Post ✉ the post is on. Letters will find you." });
                break;
            case "off":
                configuration.Enabled = false;
                configuration.Save();
                ChatGui.Print(new XivChatEntry { Type = XivChatType.Echo, Message = "Spark Post ✉ the post is off. The letters wait, unopened, patient as paper." });
                break;
            case "":
                configuration.Enabled = !configuration.Enabled;
                configuration.Save();
                ChatGui.Print(new XivChatEntry
                {
                    Type = XivChatType.Echo,
                    Message = configuration.Enabled
                        ? "Spark Post ✉ the post is on. Letters will find you."
                        : "Spark Post ✉ the post is off. The letters wait, unopened, patient as paper.",
                });
                break;
            default:
                ChatGui.Print(new XivChatEntry { Type = XivChatType.Echo, Message = "Spark Post ✉ usage: /sparkpost (on/off), /sparkpost now" });
                break;
        }
    }

    private sealed class MessagePool
    {
        public List<string> Messages { get; set; } = new();
    }
}
