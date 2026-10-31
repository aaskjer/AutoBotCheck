using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

// Shared by all AutoBotCheck actions. The "botSource" argument selects the bot list:
//   twitchinsights | twitchbotsinfo | isds
public class CPHInline
{
    // Statics are per action (every Execute C# Code sub-action is compiled separately).
    // stopRequested is written by the "killprocess" run and read by the running check on another thread.
    private static volatile bool stopRequested = false;
    private static int running = 0;

    // The public lists change rarely, so the download is reused for an hour.
    private static SourceList cachedSource = null;
    private static DateTime cachedTime = DateTime.MinValue;
    private static readonly TimeSpan cacheDuration = TimeSpan.FromHours(1);

    private const string HelixBaseUrl = "https://api.twitch.tv/helix/";
    private const string IsdsBaseUrl = "https://raw.githubusercontent.com/isdsdataarchive/twitch_ban_lists/main/";
    private const string DefaultIsdsLists = "follower_bot_list,viewer_bot_list,spam_bot_list,seller_advertising_list";
    private const int HelixBatchSize = 100;
    // Streamer.bot uses the same token bucket, so leave it some room.
    private const int RateLimitReserve = 30;
    private const int CphCallDelayMs = 250;
    private const int ProgressLogInterval = 500;
    private const string Separator = "---------------------------------------------------------------------------------------------------------";

    // Ban reasons written by this script (including the original 1.0 version). Only bans with these
    // reasons are ever lifted automatically for protected accounts.
    private static readonly string[] OwnBanReasonPrefixes = { "Suspected Harmful Bot", "AutoBotCheck" };
    private static readonly Regex ValidLogin = new Regex("^[a-z0-9_]{1,25}$", RegexOptions.Compiled);
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    private static readonly HashSet<string> KnownIsdsLists = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "follower_bot_list", "viewer_bot_list", "spam_bot_list", "seller_advertising_list", "porn_bot_acc_list",
        "hate_troll_list_0_g", "hate_troll_list_h_m", "hate_troll_list_n_z", "mad_tos_list", "security_ban_list"
    };

    // Hosted service bots that appear in the public bot lists but are used by many streamers.
    // Accounts in the user's manual bot group are still banned.
    private static readonly HashSet<string> ServiceBotSafeList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "nightbot", "streamelements", "streamlabs", "moobot", "fossabot", "wizebot", "wzbot", "soundalerts",
        "sery_bot", "pokemoncommunitygame", "kofistreambot", "botrixoficial", "frostytoolsdotcom", "own3d",
        "creatisbot", "blerp", "tangiabot", "streamstickers", "restreambot", "songlistbot", "stay_hydrated_bot",
        "botisimo", "coebot", "vivbot", "fourthwallhq", "streamlootsbot", "deepbot", "mixitupbot", "buttsbot",
        "lumiastream", "pretzelrocks", "7tvapp"
    };

    private class SourceList
    {
        public string Source;
        public string Description;
        public Dictionary<string, string> Bots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // login -> ban reason
        public HashSet<string> NeverBan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> UnbanIfOwnBan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool Complete = true;
    }

    private class HelixResponse
    {
        public int Status;
        public string Body = "";
    }

    private enum ModResult { Done, NoChange, Failed }

    private bool helixEnabled;
    private bool helixModeration;
    private string helixClientId;
    private string helixToken;
    private string broadcasterId;
    private bool dryRun;
    private int dryRunLogged;

    private static string NonExistentBotPath
    {
        get
        {
            var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AutoBotCheck");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "NonExistentBot.txt");
        }
    }

    public bool Execute()
    {
        if (string.Equals(GetArg("command", ""), "killprocess", StringComparison.OrdinalIgnoreCase))
        {
            if (Volatile.Read(ref running) == 1)
            {
                stopRequested = true;
                CPH.LogWarn("=== KILLPROCESS COMMAND RECEIVED - AutoBotCheck will stop at the next checkpoint ===");
            }
            else
            {
                CPH.LogWarn("KILLPROCESS received, but no AutoBotCheck run of this action is active.");
            }
            return true;
        }

        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
        {
            CPH.LogWarn("AutoBotCheck is already running for this action. Use the killprocess trigger to stop it.");
            return false;
        }

        stopRequested = false;
        try
        {
            return Run();
        }
        catch (OperationCanceledException)
        {
            CPH.LogWarn("=== EXECUTION STOPPED BY KILLPROCESS COMMAND ===");
            return true;
        }
        catch (Exception ex)
        {
            CPH.LogError("AutoBotCheck error: " + ex);
            return false;
        }
        finally
        {
            stopRequested = false;
            Interlocked.Exchange(ref running, 0);
        }
    }

    private bool Run()
    {
        // Read all arguments up front: the killprocess run shares this instance and its args.
        var source = GetArg("botSource", "twitchinsights").ToLowerInvariant();
        var manualGroup = GetArg("botGroupName", "Bots");
        var whitelistGroup = GetArg("whitelistGroupName", "WhitelistedBots");
        var isdsLists = GetArg("isdsLists", DefaultIsdsLists);
        dryRun = string.Equals(GetArg("dryRun", "false"), "true", StringComparison.OrdinalIgnoreCase);
        dryRunLogged = 0;

        string autoGroup;
        switch (source)
        {
            case "twitchinsights": autoGroup = "TwitchInsightBots"; break;
            case "twitchbotsinfo": autoGroup = "TwitchBotsInfo"; break;
            case "isds": autoGroup = "ISDSBanLists"; break;
            default:
                CPH.LogError($"Unknown botSource '{source}'. Use twitchinsights, twitchbotsinfo or isds.");
                return false;
        }
        const string manualBanReason = "Suspected Harmful Bot (Manual)";

        CPH.LogInfo(Separator);
        CPH.LogInfo($"Starting bot check '{source}'{(dryRun ? " (DRY RUN - no bans/unbans)" : "")}");
        CPH.LogInfo($"Auto group: {autoGroup} | Manual bot group: {manualGroup} | Whitelist group: {whitelistGroup}");
        CPH.LogInfo($"Non-existent cache: {NonExistentBotPath}");
        CPH.LogInfo(Separator);

        EnsureTls12();
        EnsureGroup(autoGroup);
        InitHelix();
        ThrowIfStopRequested();

        // Accounts that are never banned, no matter which list they are on.
        var whitelist = LoginsInGroup(whitelistGroup);
        var hardProtected = new HashSet<string>(whitelist, Cmp);
        var ownAccounts = new HashSet<string>(Cmp);
        try
        {
            var broadcaster = CPH.TwitchGetBroadcaster();
            if (broadcaster != null) AddLogin(ownAccounts, broadcaster.UserLogin);
        }
        catch (Exception ex) { CPH.LogWarn("Could not read broadcaster account: " + ex.Message); }
        try
        {
            var bot = CPH.TwitchGetBot();
            if (bot != null) AddLogin(ownAccounts, bot.UserLogin);
        }
        catch (Exception ex) { CPH.LogDebug("No bot account available: " + ex.Message); }
        hardProtected.UnionWith(ownAccounts);
        try
        {
            var mods = CPH.TwitchGetMods();
            if (mods != null) foreach (var m in mods) AddLogin(hardProtected, m.UserLogin ?? m.UserName);
            var vips = CPH.TwitchGetVips();
            if (vips != null) foreach (var v in vips) AddLogin(hardProtected, v.UserLogin ?? v.UserName);
        }
        catch (Exception ex) { CPH.LogWarn("Could not read mods/VIPs: " + ex.Message); }
        CPH.LogInfo($"Protected accounts: {whitelist.Count} whitelisted, {hardProtected.Count - whitelist.Count} own/mod/VIP.");

        var list = GetSourceList(source, isdsLists);
        if (list.Bots.Count == 0)
        {
            CPH.LogError("Bot list is empty or could not be downloaded - aborting so no group members are removed.");
            return false;
        }
        CPH.LogInfo($"{list.Description}: {list.Bots.Count} accounts{(list.Complete ? "" : " (INCOMPLETE - delisted accounts are kept this run)")}.");

        var softProtected = new HashSet<string>(ServiceBotSafeList, Cmp);
        softProtected.UnionWith(list.NeverBan);
        var unbanIfOwnBan = new HashSet<string>(ServiceBotSafeList, Cmp);
        unbanIfOwnBan.UnionWith(list.UnbanIfOwnBan);
        unbanIfOwnBan.UnionWith(ownAccounts);
        ThrowIfStopRequested();

        var autoMembers = LoginsInGroup(autoGroup);
        var manualMembers = LoginsInGroup(manualGroup);
        CPH.LogInfo($"Group members: {autoMembers.Count} in '{autoGroup}', {manualMembers.Count} in '{manualGroup}'.");

        // Step 1: resolve all accounts to user IDs (100 per Helix request).
        var nonExistent = LoadNonExistentCache();
        var newlyNonExistent = new HashSet<string>(Cmp);
        Dictionary<string, string> userIds;
        try
        {
            var toLookup = new HashSet<string>(list.Bots.Keys, Cmp);
            toLookup.UnionWith(autoMembers);
            toLookup.UnionWith(manualMembers);
            int total = toLookup.Count;
            toLookup.ExceptWith(nonExistent);
            CPH.LogInfo($"Looking up {toLookup.Count} accounts ({total - toLookup.Count} skipped via non-existent cache)...");
            userIds = LookupUserIds(toLookup.ToList(), newlyNonExistent);
        }
        finally
        {
            // Also runs on killprocess, so finished lookups are not lost.
            if (newlyNonExistent.Count > 0) SaveNonExistentCache(newlyNonExistent);
        }
        nonExistent.UnionWith(newlyNonExistent);
        CPH.LogInfo($"Lookup done: {userIds.Count} exist, {newlyNonExistent.Count} newly found non-existent.");
        CPH.LogInfo(Separator);

        // Step 2: sync groups.
        int removedAuto = 0, removedManual = 0, added = 0;
        var autoFinal = new HashSet<string>(autoMembers, Cmp);
        foreach (var login in autoMembers)
        {
            ThrowIfStopRequested();
            bool gone = nonExistent.Contains(login);
            bool delisted = list.Complete && !list.Bots.ContainsKey(login);
            if (!gone && !delisted) continue;
            if (RemoveFromGroup(login, autoGroup))
            {
                autoFinal.Remove(login);
                removedAuto++;
                CPH.LogInfo($"Removed '{login}' from '{autoGroup}' ({(gone ? "account no longer exists" : "no longer listed")}).");
            }
        }
        var manualFinal = new HashSet<string>(manualMembers, Cmp);
        foreach (var login in manualMembers)
        {
            ThrowIfStopRequested();
            if (!nonExistent.Contains(login)) continue;
            if (RemoveFromGroup(login, manualGroup))
            {
                manualFinal.Remove(login);
                removedManual++;
                CPH.LogInfo($"Removed '{login}' from '{manualGroup}' (account no longer exists).");
            }
        }
        foreach (var login in list.Bots.Keys)
        {
            ThrowIfStopRequested();
            if (autoFinal.Contains(login) || !userIds.ContainsKey(login)) continue;
            if (AddToGroup(login, autoGroup))
            {
                autoFinal.Add(login);
                added++;
            }
        }
        CPH.LogInfo($"Groups synced: '{autoGroup}' +{added} / -{removedAuto}, '{manualGroup}' -{removedManual}.");

        // Step 3: work out who actually needs a ban or unban.
        var candidates = new Dictionary<string, string>(Cmp); // login -> reason
        int skippedProtected = 0;
        foreach (var login in manualFinal)
        {
            // The manual group is the user's own decision and overrides the service-bot safelists.
            if (!userIds.ContainsKey(login)) continue;
            if (hardProtected.Contains(login)) { skippedProtected++; continue; }
            candidates[login] = manualBanReason;
        }
        foreach (var login in autoFinal)
        {
            if (!userIds.ContainsKey(login) || candidates.ContainsKey(login)) continue;
            if (hardProtected.Contains(login) || softProtected.Contains(login)) { skippedProtected++; continue; }
            string reason;
            candidates[login] = list.Bots.TryGetValue(login, out reason) ? reason : "Suspected Harmful Bot";
        }

        var bans = GetPermanentBans(); // userId -> {login, reason}; null if Helix ban list is not available
        var toBan = new List<string>();
        var toUnban = new List<KeyValuePair<string, string>>(); // login -> userId
        int alreadyBanned = 0;
        if (bans != null)
        {
            foreach (var login in candidates.Keys)
            {
                if (bans.ContainsKey(userIds[login])) alreadyBanned++;
                else toBan.Add(login);
            }
            foreach (var ban in bans)
            {
                var login = ban.Value.Key;
                if (string.IsNullOrEmpty(login)) continue;
                bool ownBan = IsOwnBanReason(ban.Value.Value);
                bool whitelisted = whitelist.Contains(login) && (ownBan || autoFinal.Contains(login) || manualFinal.Contains(login));
                bool wronglyBanned = ownBan && unbanIfOwnBan.Contains(login) && !manualFinal.Contains(login);
                if (whitelisted || wronglyBanned) toUnban.Add(new KeyValuePair<string, string>(login, ban.Key));
            }
        }
        else
        {
            // No ban list: same as before, every candidate is (re)banned and whitelisted bots are unbanned.
            toBan.AddRange(candidates.Keys);
            foreach (var login in whitelist)
            {
                string id;
                if ((autoFinal.Contains(login) || manualFinal.Contains(login)) && userIds.TryGetValue(login, out id))
                {
                    toUnban.Add(new KeyValuePair<string, string>(login, id));
                }
            }
        }
        CPH.LogInfo($"Ban check: {candidates.Count} candidates, {alreadyBanned} already banned, {toBan.Count} to ban, {toUnban.Count} to unban, {skippedProtected} protected.");
        CPH.LogInfo(Separator);

        // Step 4: unban, then ban.
        int unbanned = 0, unbanFailed = 0;
        foreach (var entry in toUnban)
        {
            ThrowIfStopRequested();
            var result = Unban(entry.Key, entry.Value);
            if (result == ModResult.Done) { unbanned++; CPH.LogInfo($"Unbanned protected account '{entry.Key}'."); }
            else if (result == ModResult.Failed) unbanFailed++;
        }

        int banned = 0, banNoChange = 0, banFailed = 0;
        for (int i = 0; i < toBan.Count; i++)
        {
            ThrowIfStopRequested();
            var login = toBan[i];
            var result = Ban(login, userIds[login], candidates[login]);
            if (result == ModResult.Done) banned++;
            else if (result == ModResult.NoChange) banNoChange++;
            else banFailed++;
            if ((i + 1) % ProgressLogInterval == 0) CPH.LogInfo($"Ban progress: {i + 1}/{toBan.Count}");
        }

        CPH.LogInfo(Separator);
        CPH.LogInfo($"SUMMARY ({source}{(dryRun ? ", dry run" : "")}):");
        CPH.LogInfo($"  {autoGroup}: {autoFinal.Count} members (+{added} / -{removedAuto})");
        CPH.LogInfo($"  {manualGroup}: {manualFinal.Count} members (-{removedManual})");
        CPH.LogInfo($"  Bans: {banned} new, {alreadyBanned + banNoChange} already banned, {banFailed} failed");
        CPH.LogInfo($"  Unbans: {unbanned} done, {unbanFailed} failed | Protected: {skippedProtected}");
        CPH.LogInfo(Separator);
        return true;
    }

    // ---------------------------------------------------------------- bot lists

    private SourceList GetSourceList(string source, string isdsLists)
    {
        var cacheKey = source == "isds" ? source + ":" + isdsLists : source;
        var cached = cachedSource;
        if (cached != null && cached.Source == cacheKey && DateTime.UtcNow - cachedTime < cacheDuration)
        {
            CPH.LogDebug("Using cached bot list.");
            return cached;
        }

        var list = new SourceList { Source = cacheKey };
        if (source == "twitchinsights") FetchTwitchInsights(list);
        else if (source == "twitchbotsinfo") FetchTwitchBotsInfo(list);
        else FetchIsds(list, isdsLists);
        // The ISDS white/grey/unban lists are maintained and also protect against the other sources.
        FetchIsdsProtection(list);

        if (list.Complete && list.Bots.Count > 0)
        {
            cachedSource = list;
            cachedTime = DateTime.UtcNow;
        }
        return list;
    }

    private void FetchTwitchInsights(SourceList list)
    {
        list.Description = "TwitchInsights";
        try
        {
            var json = JObject.Parse(HttpGet("https://api.twitchinsights.net/v1/bots/all"));
            var bots = json["bots"] as JArray;
            if (bots == null) { list.Complete = false; return; }
            foreach (var bot in bots)
            {
                var login = NormalizeLogin((string)bot[0]);
                if (login != null) list.Bots[login] = "Suspected Harmful Bot (TwitchInsights)";
            }
        }
        catch (Exception ex)
        {
            list.Complete = false;
            CPH.LogError("TwitchInsights download failed: " + ex.Message);
        }
    }

    private void FetchTwitchBotsInfo(SourceList list)
    {
        list.Description = "TwitchBots.info (multi-channel bots)";
        // Bot types flagged "managed" are hosted services (Nightbot, StreamElements, Marbles, ...).
        var managedTypes = new HashSet<int>();
        try
        {
            for (int offset = 0, total = 1; offset < total; offset += 100)
            {
                var page = JObject.Parse(HttpGet($"https://api.twitchbots.info/v2/type?limit=100&offset={offset}"));
                total = (int?)page["total"] ?? 0;
                var types = page["types"] as JArray;
                if (types == null || types.Count == 0) break;
                foreach (var type in types)
                {
                    if ((bool?)type["managed"] == true) managedTypes.Add((int)type["id"]);
                }
            }
        }
        catch (Exception ex)
        {
            CPH.LogWarn("TwitchBots.info type list failed, service bots are only filtered by the built-in safelist: " + ex.Message);
        }

        int services = 0;
        for (int offset = 0, total = 1; offset < total; offset += 100)
        {
            ThrowIfStopRequested();
            try
            {
                var page = JObject.Parse(HttpGet($"https://api.twitchbots.info/v2/bot?multiChannel=1&limit=100&offset={offset}"));
                total = (int?)page["total"] ?? 0;
                var bots = page["bots"] as JArray;
                if (bots == null || bots.Count == 0) break;
                foreach (var bot in bots)
                {
                    var login = NormalizeLogin((string)bot["username"]);
                    if (login == null) continue;
                    list.Bots[login] = "Suspected Harmful Bot (TwitchBots.info)";
                    var type = (int?)bot["type"];
                    if (type.HasValue && managedTypes.Contains(type.Value))
                    {
                        list.NeverBan.Add(login);
                        services++;
                    }
                }
            }
            catch (Exception ex)
            {
                list.Complete = false;
                CPH.LogError($"TwitchBots.info page at offset {offset} failed: {ex.Message}");
                break;
            }
        }
        CPH.LogInfo($"TwitchBots.info: {services} hosted service bots are kept in the group but not banned.");
    }

    private void FetchIsds(SourceList list, string isdsLists)
    {
        var names = new List<string>();
        foreach (var raw in isdsLists.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim().ToLowerInvariant();
            if (name.EndsWith(".txt")) name = name.Substring(0, name.Length - 4);
            if (name == "hate_troll_list") names.AddRange(new[] { "hate_troll_list_0_g", "hate_troll_list_h_m", "hate_troll_list_n_z" });
            else if (KnownIsdsLists.Contains(name)) names.Add(name);
            else CPH.LogWarn($"Unknown ISDS list '{raw}' ignored. Known: {string.Join(", ", KnownIsdsLists)}, hate_troll_list.");
        }
        names = names.Distinct().ToList();
        list.Description = "ISDS ban lists (" + string.Join(", ", names) + ")";

        foreach (var name in names)
        {
            ThrowIfStopRequested();
            try
            {
                int count = 0;
                foreach (var login in ReadLoginList(HttpGet(IsdsBaseUrl + name + ".txt")))
                {
                    if (!list.Bots.ContainsKey(login)) list.Bots[login] = "AutoBotCheck: ISDS " + name;
                    count++;
                }
                CPH.LogDebug($"ISDS {name}: {count} accounts.");
            }
            catch (Exception ex)
            {
                list.Complete = false;
                CPH.LogError($"ISDS list '{name}' failed: {ex.Message}");
            }
        }
    }

    private void FetchIsdsProtection(SourceList list)
    {
        // Greylisted accounts are harmless, but ISDS does not recommend actively unbanning them.
        var files = new[] { "whitelisted_bots.txt", "unban_list", "greylisted_bots.txt" };
        foreach (var file in files)
        {
            try
            {
                foreach (var login in ReadLoginList(HttpGet(IsdsBaseUrl + file)))
                {
                    list.NeverBan.Add(login);
                    if (file != "greylisted_bots.txt") list.UnbanIfOwnBan.Add(login);
                }
            }
            catch (Exception ex)
            {
                CPH.LogWarn($"ISDS protection list '{file}' not available: {ex.Message}");
            }
        }
    }

    private static IEnumerable<string> ReadLoginList(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("/unban ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("/ban ", StringComparison.OrdinalIgnoreCase))
            {
                line = line.Substring(line.IndexOf(' ') + 1).Trim();
            }
            var login = NormalizeLogin(line);
            if (login != null) yield return login;
        }
    }

    private static string NormalizeLogin(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var login = value.Trim().TrimStart('@').ToLowerInvariant();
        return ValidLogin.IsMatch(login) ? login : null;
    }

    // ---------------------------------------------------------------- Twitch

    private void InitHelix()
    {
        helixEnabled = false;
        helixModeration = false;
        broadcasterId = null;
        try { broadcasterId = CPH.TwitchGetBroadcaster()?.UserId; } catch { }

        helixClientId = GetProxyString("TwitchClientId");
        helixToken = GetToken();
        if (string.IsNullOrEmpty(helixClientId) || string.IsNullOrEmpty(helixToken))
        {
            CPH.LogWarn("Twitch token/client ID not accessible - using slower Streamer.bot per-user calls.");
            return;
        }

        helixEnabled = true;
        var resp = Helix("GET", "users", null);
        var data = resp.Status == 200 ? JObject.Parse(resp.Body)["data"] as JArray : null;
        if (data == null || data.Count == 0)
        {
            helixEnabled = false;
            CPH.LogWarn($"Helix check failed (HTTP {resp.Status}) - using slower Streamer.bot per-user calls.");
            return;
        }
        var tokenUserId = (string)data[0]["id"];
        helixModeration = !string.IsNullOrEmpty(broadcasterId) && tokenUserId == broadcasterId;
        CPH.LogInfo(helixModeration
            ? "Helix: batch lookups, ban list and direct bans enabled."
            : "Helix: batch lookups enabled; token is not the broadcaster's, so bans go through Streamer.bot.");
    }

    private Dictionary<string, string> LookupUserIds(List<string> logins, HashSet<string> missing)
    {
        var ids = new Dictionary<string, string>(Cmp);
        int i = 0;
        while (i < logins.Count && helixEnabled)
        {
            ThrowIfStopRequested();
            var batch = logins.GetRange(i, Math.Min(HelixBatchSize, logins.Count - i));
            if (!LookupBatch(batch, ids, missing))
            {
                helixEnabled = false;
                CPH.LogWarn("Helix lookups not permitted anymore - continuing with Streamer.bot per-user calls.");
                break;
            }
            i += batch.Count;
            if (i % (HelixBatchSize * 20) == 0) CPH.LogInfo($"Lookup progress: {i}/{logins.Count}");
        }
        for (; i < logins.Count; i++)
        {
            ThrowIfStopRequested();
            LookupViaCph(logins[i], ids, missing);
            if ((i + 1) % ProgressLogInterval == 0) CPH.LogInfo($"Lookup progress: {i + 1}/{logins.Count}");
        }
        return ids;
    }

    // Returns false only when the token is not accepted.
    private bool LookupBatch(List<string> batch, Dictionary<string, string> ids, HashSet<string> missing)
    {
        var resp = Helix("GET", "users?" + string.Join("&", batch.Select(l => "login=" + Uri.EscapeDataString(l))), null);
        if (resp.Status == 200)
        {
            var found = new HashSet<string>(Cmp);
            var data = JObject.Parse(resp.Body)["data"] as JArray;
            if (data != null)
            {
                foreach (var user in data)
                {
                    var login = (string)user["login"];
                    ids[login] = (string)user["id"];
                    found.Add(login);
                }
            }
            foreach (var login in batch) if (!found.Contains(login)) missing.Add(login);
            return true;
        }
        if (resp.Status == 401 || resp.Status == 403) return false;
        if (resp.Status == 400 && batch.Count > 1)
        {
            // A single rejected name fails the whole request: split and retry.
            int half = batch.Count / 2;
            return LookupBatch(batch.GetRange(0, half), ids, missing) && LookupBatch(batch.GetRange(half, batch.Count - half), ids, missing);
        }
        if (resp.Status == 400) missing.Add(batch[0]);
        else CPH.LogWarn($"Lookup of {batch.Count} accounts failed (HTTP {resp.Status}); they are skipped this run.");
        return true;
    }

    private void LookupViaCph(string login, Dictionary<string, string> ids, HashSet<string> missing)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var info = CPH.TwitchGetUserInfoByLogin(login);
                if (info == null) missing.Add(login);
                else ids[login] = info.UserId;
                break;
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Unable to lookup user") || ex.Message.Contains("not found"))
                {
                    missing.Add(login);
                    break;
                }
                if (attempt == 1 && (ex.Message.Contains("Rate limit") || ex.Message.Contains("RateLimitExceeded")))
                {
                    CPH.LogWarn("Rate limit hit, waiting 5 seconds...");
                    SleepInterruptible(5000);
                    continue;
                }
                // Unknown state: neither cached as non-existent nor removed from any group.
                CPH.LogWarn($"Lookup of '{login}' failed, skipped this run: {ex.Message}");
                break;
            }
        }
        SleepInterruptible(CphCallDelayMs);
    }

    // userId -> (login, reason) of all permanent bans, or null if the list is not available.
    private Dictionary<string, KeyValuePair<string, string>> GetPermanentBans()
    {
        if (!helixModeration)
        {
            CPH.LogWarn("Ban list not available without the broadcaster token - all candidates are (re)banned.");
            return null;
        }
        var bans = new Dictionary<string, KeyValuePair<string, string>>();
        string cursor = null;
        do
        {
            ThrowIfStopRequested();
            var query = $"moderation/banned?broadcaster_id={broadcasterId}&first=100" + (cursor != null ? "&after=" + Uri.EscapeDataString(cursor) : "");
            var resp = Helix("GET", query, null);
            if (resp.Status != 200)
            {
                CPH.LogWarn($"Could not read the ban list (HTTP {resp.Status}) - all candidates are (re)banned.");
                return null;
            }
            var json = JObject.Parse(resp.Body);
            var data = json["data"] as JArray;
            if (data != null)
            {
                foreach (var ban in data)
                {
                    // Timeouts have an expiry and are turned into a permanent ban.
                    if (!string.IsNullOrEmpty((string)ban["expires_at"])) continue;
                    bans[(string)ban["user_id"]] = new KeyValuePair<string, string>((string)ban["user_login"], (string)ban["reason"] ?? "");
                }
            }
            cursor = (string)json["pagination"]?["cursor"];
        }
        while (!string.IsNullOrEmpty(cursor));
        CPH.LogInfo($"Loaded {bans.Count} existing permanent bans.");
        return bans;
    }

    private ModResult Ban(string login, string userId, string reason)
    {
        if (dryRun)
        {
            // Keep the log readable: the rest only on debug level.
            var message = $"[DRY RUN] Would ban '{login}' ({reason}).";
            if (dryRunLogged++ < 25) CPH.LogInfo(message);
            else CPH.LogDebug(message);
            return ModResult.Done;
        }
        if (helixModeration)
        {
            var body = new JObject { ["data"] = new JObject { ["user_id"] = userId, ["reason"] = reason } };
            var resp = Helix("POST", $"moderation/bans?broadcaster_id={broadcasterId}&moderator_id={broadcasterId}", body.ToString(Newtonsoft.Json.Formatting.None));
            if (resp.Status == 200)
            {
                CPH.LogDebug($"Banned '{login}'.");
                return ModResult.Done;
            }
            if (resp.Status == 400 && resp.Body.IndexOf("already banned", StringComparison.OrdinalIgnoreCase) >= 0) return ModResult.NoChange;
            if (resp.Status != 401 && resp.Status != 403)
            {
                CPH.LogError($"Ban of '{login}' failed (HTTP {resp.Status}): {Shorten(resp.Body)}");
                return ModResult.Failed;
            }
            helixModeration = false;
            CPH.LogWarn($"Helix ban not permitted (HTTP {resp.Status}) - switching to Streamer.bot ban calls.");
        }
        try
        {
            bool ok = CPH.TwitchBanUser(login, reason, false);
            if (!ok) CPH.LogDebug($"Ban of '{login}' returned false (already banned or not permitted).");
            return ok ? ModResult.Done : ModResult.Failed;
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("already banned")) return ModResult.NoChange;
            CPH.LogError($"Ban of '{login}' failed: {ex.Message}");
            return ModResult.Failed;
        }
        finally
        {
            SleepInterruptible(CphCallDelayMs);
        }
    }

    private ModResult Unban(string login, string userId)
    {
        if (dryRun)
        {
            CPH.LogInfo($"[DRY RUN] Would unban '{login}'.");
            return ModResult.Done;
        }
        if (helixModeration && !string.IsNullOrEmpty(userId))
        {
            var resp = Helix("DELETE", $"moderation/bans?broadcaster_id={broadcasterId}&moderator_id={broadcasterId}&user_id={userId}", null);
            if (resp.Status == 200 || resp.Status == 204) return ModResult.Done;
            if (resp.Status == 400 && resp.Body.IndexOf("not banned", StringComparison.OrdinalIgnoreCase) >= 0) return ModResult.NoChange;
            CPH.LogError($"Unban of '{login}' failed (HTTP {resp.Status}): {Shorten(resp.Body)}");
            return ModResult.Failed;
        }
        try
        {
            return CPH.TwitchUnbanUser(login, false) ? ModResult.Done : ModResult.NoChange;
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("not banned")) return ModResult.NoChange;
            CPH.LogError($"Unban of '{login}' failed: {ex.Message}");
            return ModResult.Failed;
        }
        finally
        {
            SleepInterruptible(CphCallDelayMs);
        }
    }

    private HelixResponse Helix(string method, string pathAndQuery, string jsonBody)
    {
        for (int attempt = 1; ; attempt++)
        {
            ThrowIfStopRequested();
            var request = (HttpWebRequest)WebRequest.Create(HelixBaseUrl + pathAndQuery);
            request.Method = method;
            request.Timeout = 20000;
            request.Headers["Client-Id"] = helixClientId;
            request.Headers["Authorization"] = "Bearer " + helixToken;

            HttpWebResponse response;
            try
            {
                if (jsonBody != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(jsonBody);
                    request.ContentType = "application/json";
                    using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                }
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException ex)
            {
                response = ex.Response as HttpWebResponse;
                if (response == null)
                {
                    if (attempt >= 3)
                    {
                        CPH.LogWarn($"Helix {method} failed: {ex.Message}");
                        return new HelixResponse();
                    }
                    SleepInterruptible(2000 * attempt);
                    continue;
                }
            }

            using (response)
            {
                var result = new HelixResponse { Status = (int)response.StatusCode };
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) result.Body = reader.ReadToEnd();

                int waitMs = RateLimitWaitMs(response.Headers, result.Status == 429);
                if (result.Status == 429 && attempt < 6)
                {
                    CPH.LogWarn($"Twitch rate limit reached, waiting {waitMs / 1000.0:0.#}s...");
                    SleepInterruptible(waitMs);
                    continue;
                }
                if (result.Status == 401 && attempt == 1)
                {
                    // Streamer.bot may have refreshed the token in the meantime.
                    var fresh = GetToken();
                    if (!string.IsNullOrEmpty(fresh) && fresh != helixToken)
                    {
                        helixToken = fresh;
                        continue;
                    }
                }
                if (waitMs > 0) SleepInterruptible(waitMs);
                return result;
            }
        }
    }

    // Helix refills the bucket continuously until "Ratelimit-Reset" (bucket full again).
    private static int RateLimitWaitMs(WebHeaderCollection headers, bool limited)
    {
        int remaining, limit;
        long reset;
        bool known = int.TryParse(headers["Ratelimit-Remaining"], out remaining);
        if (!limited && (!known || remaining > RateLimitReserve)) return 0;
        if (!long.TryParse(headers["Ratelimit-Reset"], out reset)) return limited ? 5000 : 0;
        double msToFull = Math.Max(0, (reset - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) * 1000.0) + 250;
        if (limited) return (int)Math.Min(Math.Max(msToFull, 1000), 65000);
        // Close to the reserve: wait just long enough for the missing points to refill.
        if (!int.TryParse(headers["Ratelimit-Limit"], out limit) || limit <= remaining) limit = 800;
        double msPerPoint = msToFull / (limit - remaining);
        return (int)Math.Min(msPerPoint * (RateLimitReserve - remaining + 1), 65000);
    }

    private string GetToken()
    {
        var token = GetProxyString("TwitchOAuthToken");
        if (token != null && token.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase)) token = token.Substring(6);
        return token;
    }

    // Read via reflection so the code still compiles if a Streamer.bot version lacks the property.
    private string GetProxyString(string propertyName)
    {
        object proxy = CPH;
        var type = proxy.GetType();
        foreach (var t in new[] { type }.Concat(type.GetInterfaces()))
        {
            var property = t.GetProperty(propertyName);
            if (property == null || property.PropertyType != typeof(string)) continue;
            try { return property.GetValue(proxy, null) as string; }
            catch { return null; }
        }
        return null;
    }

    // ---------------------------------------------------------------- Streamer.bot groups

    private void EnsureGroup(string group)
    {
        try
        {
            var groups = CPH.GetGroups();
            if (groups == null || !groups.Any(g => g.Equals(group, StringComparison.OrdinalIgnoreCase)))
            {
                CPH.AddGroup(group);
                CPH.LogInfo($"Created group '{group}'.");
            }
        }
        catch (Exception ex)
        {
            CPH.LogError($"Error creating/checking group '{group}': {ex.Message}");
        }
    }

    private HashSet<string> LoginsInGroup(string group)
    {
        var logins = new HashSet<string>(Cmp);
        var users = CPH.UsersInGroup(group);
        if (users != null) foreach (var user in users) AddLogin(logins, user.Login);
        return logins;
    }

    private bool AddToGroup(string login, string group)
    {
        try
        {
            CPH.AddUserToGroup(login, Platform.Twitch, group);
            return true;
        }
        catch (Exception ex)
        {
            CPH.LogError($"Error adding '{login}' to '{group}': {ex.Message}");
            return false;
        }
    }

    private bool RemoveFromGroup(string login, string group)
    {
        try
        {
            CPH.RemoveUserFromGroup(login, Platform.Twitch, group);
            return true;
        }
        catch (Exception ex)
        {
            CPH.LogError($"Error removing '{login}' from '{group}': {ex.Message}");
            return false;
        }
    }

    // ---------------------------------------------------------------- non-existent cache

    private HashSet<string> LoadNonExistentCache()
    {
        var set = new HashSet<string>(Cmp);
        var path = NonExistentBotPath;
        WithCacheLock(() =>
        {
            if (File.Exists(path)) foreach (var line in File.ReadLines(path)) AddLogin(set, line.Trim());
        });
        CPH.LogInfo($"Loaded {set.Count} known non-existent accounts from cache file.");
        return set;
    }

    private void SaveNonExistentCache(HashSet<string> newEntries)
    {
        var path = NonExistentBotPath;
        try
        {
            int total = 0;
            WithCacheLock(() =>
            {
                // Re-read under the lock so entries written by another AutoBotCheck action are kept.
                var all = new HashSet<string>(Cmp);
                if (File.Exists(path)) foreach (var line in File.ReadLines(path)) AddLogin(all, line.Trim());
                all.UnionWith(newEntries);
                var tmp = path + ".tmp";
                File.WriteAllLines(tmp, all.OrderBy(l => l, StringComparer.Ordinal));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                total = all.Count;
            });
            CPH.LogInfo($"Saved non-existent cache: {total} accounts ({newEntries.Count} new).");
        }
        catch (Exception ex)
        {
            CPH.LogError("Error saving non-existent cache file: " + ex.Message);
        }
    }

    // The file is shared by all AutoBotCheck actions, which are separate assemblies: use a named mutex.
    private static void WithCacheLock(Action action)
    {
        using (var mutex = new Mutex(false, "AutoBotCheck.NonExistentCache"))
        {
            bool owned = false;
            try
            {
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException) { owned = true; }
                action();
            }
            finally
            {
                if (owned) mutex.ReleaseMutex();
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private string GetArg(string name, string fallback)
    {
        object value;
        if (args == null || !args.TryGetValue(name, out value) || value == null) return fallback;
        var text = value.ToString().Trim();
        return text.Length == 0 ? fallback : text;
    }

    // GitHub and Twitch require TLS 1.2+. Only touch the process-wide setting when it is a legacy
    // explicit list; 0 (SystemDefault) already lets Windows choose TLS 1.2/1.3.
    private static void EnsureTls12()
    {
        var protocols = ServicePointManager.SecurityProtocol;
        if ((int)protocols != 0 && (protocols & SecurityProtocolType.Tls12) == 0)
        {
            ServicePointManager.SecurityProtocol = protocols | SecurityProtocolType.Tls12;
        }
    }

    private static string HttpGet(string url)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Timeout = 30000;
        request.UserAgent = "AutoBotCheck (Streamer.bot)";
        request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }

    private static bool IsOwnBanReason(string reason)
    {
        return reason != null && OwnBanReasonPrefixes.Any(p => reason.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddLogin(HashSet<string> set, string login)
    {
        if (!string.IsNullOrWhiteSpace(login)) set.Add(login.Trim().ToLowerInvariant());
    }

    private static string Shorten(string text)
    {
        return text == null || text.Length <= 200 ? text : text.Substring(0, 200) + "...";
    }

    private static void ThrowIfStopRequested()
    {
        if (stopRequested) throw new OperationCanceledException();
    }

    private static void SleepInterruptible(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!stopRequested)
        {
            var left = (until - DateTime.UtcNow).TotalMilliseconds;
            if (left <= 0) return;
            Thread.Sleep((int)Math.Min(250, left));
        }
    }
}
