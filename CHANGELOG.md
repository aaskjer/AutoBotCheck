# Changelog

## [1.1.0] - 2026-10-31

### Added

- `ISDS.BanLists` action for the [ISDS Twitch ban lists](https://github.com/isdsdataarchive/twitch_ban_lists); the `isdsLists` argument selects the lists.
- `dryRun` argument that only logs what would be banned or unbanned.
- Helix API mode with Streamer.bot's broadcaster token: user lookups in batches of 100, direct bans, and the existing ban list is read so already banned accounts are skipped.
- Mods, VIPs, the broadcaster and the bot account are never banned.
- Safelist for well-known service bots (Nightbot, StreamElements, ...), plus the hosted service bots from TwitchBots.info and the ISDS whitelisted, greylisted and unban lists.
- Accounts that AutoBotCheck banned by mistake (protected service bots, own accounts) are unbanned automatically. Bans with other reasons are never touched.

### Changed

- All actions share one C# script; the `botSource` argument selects the bot list.
- Downloaded bot lists are cached for one hour instead of one minute.
- Starting an action while its check is still running no longer starts a second check.
- The non-existent account cache is also saved when a check is stopped, and is locked while writing.
- Helix calls wait for Twitch's rate limit reset instead of a fixed 5 seconds.

## [1.0.0]

- `TwitchInsight` and `TwitchBots.Info` actions with a manual bot group, a whitelist group and a cache for accounts that no longer exist.

[1.1.0]: https://github.com/aaskjer/AutoBotCheck/releases/tag/v1.1.0
