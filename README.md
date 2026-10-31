# AutoBotCheck

Three separate [Streamer.bot](https://streamer.bot) actions that go through public bot lists and ban every listed bot that is not already banned in your Twitch channel.

| Action | Bot list |
| --- | --- |
| `TwitchInsight` | [TwitchInsights](https://twitchinsights.net/bots) |
| `TwitchBots.Info` | [TwitchBots.info](https://twitchbots.info) (multi-channel bots) |
| `ISDS.BanLists` | [ISDS Twitch ban lists](https://github.com/isdsdataarchive/twitch_ban_lists) |

All three actions run the same C# code ([src/AutoBotCheck.cs](src/AutoBotCheck.cs)); the `botSource` argument picks the list.

## Installation

1. Download `AutoBotCheck_v1.1.sb` from the [latest release](https://github.com/aaskjer/AutoBotCheck/releases/latest).
2. In Streamer.bot click **Import**, drop the file into the import field (or paste its content) and import.
3. Create two groups under **Settings > Groups**: `Bots` and `WhitelistedBots` (other names work too, see [Arguments](#arguments)).
4. Optional: set `dryRun` to `True` for the first run to see what would be banned.

The groups `TwitchInsightBots`, `TwitchBotsInfo` and `ISDSBanLists` are created and filled automatically.

### Upgrading from 1.0

`TwitchInsight` and `TwitchBots.Info` keep their action IDs, so importing 1.1 replaces the 1.0 actions. `ISDS.BanLists` is added as a new action. If you changed the group names in 1.0, set them again after the import.

## Usage

- The first **Test** trigger of an action starts a check.
- The second **Test** trigger (argument `command = killprocess`) stops a running check at the next checkpoint. This only works while the action stays in a non-blocking queue (the import uses `Backend - Bot`).
- The first run can take a while: Twitch allows about 800 API calls per minute. Later runs only ban accounts that are not banned yet.
- Running a check while you are live is not recommended.
- With Streamer.bot on debug log level a run can write a very large log file.

Every run ends with a summary in the Streamer.bot log (new bans, already banned, unbans, protected accounts).

## Arguments

| Argument | Default | Description |
| --- | --- | --- |
| `botGroupName` | `Bots` | Your own extra bots. They are banned even if they are on a safelist. Accounts that no longer exist are removed from the group. |
| `whitelistGroupName` | `WhitelistedBots` | Never banned. Unbanned again if they are on the bot list or in the bot group, or if AutoBotCheck banned them. |
| `botSource` | set per action | `twitchinsights`, `twitchbotsinfo` or `isds` |
| `isdsLists` | `follower_bot_list,viewer_bot_list,spam_bot_list,seller_advertising_list` | `ISDS.BanLists` only. Comma separated; also available: `porn_bot_acc_list`, `hate_troll_list` (all three parts), `mad_tos_list`, `security_ban_list` |
| `dryRun` | `False` | `True` only logs what would be banned or unbanned. Groups and the non-existent cache are still updated. |

## Protected accounts

- **Never banned:** the broadcaster, the Streamer.bot bot account, mods, VIPs and the whitelist group.
- **Skipped unless they are in your bot group:** well-known service bots (Nightbot, StreamElements, Streamlabs, Moobot, Fossabot, ...), bots that TwitchBots.info marks as hosted services, and the ISDS whitelisted, greylisted and unban lists.
- **Unbanned automatically:** protected service bots and your own accounts, but only if AutoBotCheck banned them (ban reason starts with `Suspected Harmful Bot` or `AutoBotCheck`). Bans with any other reason are never lifted.

## Fast mode (broadcaster token)

AutoBotCheck uses Streamer.bot's Twitch token for direct Helix API calls:

| Token | Lookups | Ban list / bans |
| --- | --- | --- |
| Broadcaster token | 100 accounts per request | Existing bans are read, so only new bots are banned |
| Token of another account | 100 accounts per request | Bans go through Streamer.bot and every run re-bans all bots |
| No token available | One call per account | Same as above |

The log warns when it falls back to a slower mode.

## Files and caching

- `<Streamer.bot folder>/AutoBotCheck/NonExistentBot.txt` stores accounts that no longer exist on Twitch so they are not looked up again.
- Downloaded bot lists are reused for one hour.

## Building the import file

`AutoBotCheck_v1.1.sb` is generated from the 1.0 export and the C# source:

```bash
python -I src/build_sb.py src/AutoBotCheck_v1.0.sb AutoBotCheck_v1.1.sb
```

## Bot lists

AutoBotCheck only downloads the public lists; it does not maintain them. If an account is listed by mistake, contact the list maintainer. ISDS unban requests go to unban@isds.tech.

## License

[MIT](LICENSE)
