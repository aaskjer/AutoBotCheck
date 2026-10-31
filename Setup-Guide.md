# Import

Click `Import` in [streamer.bot](https://streamer.bot/) and drag the [AutoBotCheck.sb](https://github.com/aaskjer/AutoBotCheck/releases/latest) file into the window, or paste the [import string](https://github.com/aaskjer/AutoBotCheck/blob/main/Import-String.md), then click `Import`.

The import brings three actions in the `AutoBotCheck` group and the queue `Backend - Bot`:

| Action | Bot list | Group it fills |
| --- | --- | --- |
| `TwitchInsight` | [TwitchInsights](https://twitchinsights.net/bots) | `TwitchInsightBots` |
| `TwitchBots.Info` | [TwitchBots.info](https://twitchbots.info) (multi-channel bots) | `TwitchBotsInfo` |
| `ISDS.BanLists` | [ISDS Twitch ban lists](https://github.com/isdsdataarchive/twitch_ban_lists) | `ISDSBanLists` |

All three run the same code; the `botSource` argument decides which list it is.

## Upgrading from 1.0

Import 1.1 over 1.0: `TwitchInsight` and `TwitchBots.Info` keep their action IDs and are replaced,
`ISDS.BanLists` is added. The import resets the group names to `Bots` and `WhitelistedBots`, so set yours
again if you changed them.

# Setup

## Groups

Create two groups under `Settings > Groups`:

- `Bots`: your own extra bots. They are banned even if they are on a safelist. Accounts that no longer
  exist on Twitch are removed from the group.
- `WhitelistedBots`: accounts that are never banned. If one of them is on the bot list or in your bot
  group, or AutoBotCheck banned it, it is unbanned.

Add the bots you want to keep to the whitelist before the first run. The groups `TwitchInsightBots`,
`TwitchBotsInfo` and `ISDSBanLists` are created and filled by the actions themselves.

## Arguments

Each action starts with a few `Set Argument` sub-actions:

| Argument | Default | |
| --- | --- | --- |
| `botGroupName` | `Bots` | name of your bot group |
| `whitelistGroupName` | `WhitelistedBots` | name of your whitelist group |
| `botSource` | set per action | `twitchinsights`, `twitchbotsinfo` or `isds` |
| `isdsLists` | `follower_bot_list,viewer_bot_list,spam_bot_list,seller_advertising_list` | `ISDS.BanLists` only, see below |
| `dryRun` | `False` | `True` only logs what would be banned or unbanned |

A dry run still fills the groups and the cache of accounts that no longer exist.

`isdsLists` takes a comma separated list of: `follower_bot_list`, `viewer_bot_list`, `spam_bot_list`,
`seller_advertising_list`, `porn_bot_acc_list`, `hate_troll_list` (all three parts), `mad_tos_list`,
`security_ban_list`.

# Running a check

Go to `Actions`, select one of the AutoBotCheck actions, right-click the first `Test` trigger and choose
`Test Trigger`. The check runs in the background and writes its progress and a summary to the
Streamer.bot log.

To stop it, do the same with the second `Test` trigger, the one with `command = killprocess`. It stops at
the next checkpoint and keeps what it has looked up so far. This only works while the action stays in a
non-blocking queue such as `Backend - Bot`.

- The first run takes a while: Twitch allows about 800 API calls per minute. Later runs only ban the
  accounts that are not banned yet.
- Don't run a check while you are live.
- With Streamer.bot's log on debug level, a run can write a very large log file.

## Protected accounts

- **Never banned:** the broadcaster, the Streamer.bot bot account, mods, VIPs and the whitelist group.
- **Skipped unless they are in your bot group:** well-known service bots (Nightbot, StreamElements,
  Streamlabs, Moobot, Fossabot, ...), bots that TwitchBots.info marks as hosted services, and the
  accounts on the ISDS whitelisted, greylisted and unban lists.
- **Unbanned automatically:** protected service bots and your own accounts, but only if AutoBotCheck
  banned them (the ban reason starts with `Suspected Harmful Bot` or `AutoBotCheck`). Bans with any other
  reason are never lifted.

## Broadcaster token

AutoBotCheck talks to the Twitch API directly with Streamer.bot's token:

| Token | Lookups | Bans |
| --- | --- | --- |
| Broadcaster account | 100 accounts per request | your ban list is read, only new bots are banned |
| another account | 100 accounts per request | through Streamer.bot, every run bans all bots again |
| not available | one call per account | through Streamer.bot, every run bans all bots again |

The log warns when it falls back to a slower way.

## Files

- `<Streamer.bot folder>/AutoBotCheck/NonExistentBot.txt` keeps the accounts that no longer exist on
  Twitch, so they are not looked up again.
- The downloaded bot lists are reused for one hour.

## Listed by mistake?

AutoBotCheck only downloads the lists; it does not maintain them. Ask the list's maintainer to remove an
account. ISDS takes unban requests at unban@isds.tech.
