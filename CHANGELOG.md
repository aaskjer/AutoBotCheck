# Changelog

Releases are in the [release notes](https://github.com/aaskjer/AutoBotCheck/releases).
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.1.0] - 2026-10-31

### Added

- **The ISDS ban lists.** A third action, `ISDS.BanLists`, bans the accounts on the
  [ISDS Twitch ban lists](https://github.com/isdsdataarchive/twitch_ban_lists). The `isdsLists` argument
  picks which of them are used.
- **A dry run.** With `dryRun` set to `True` the log shows what would be banned or unbanned, and nobody
  is.
- **Fast runs with the broadcaster token.** Accounts are looked up 100 per request and banned directly
  through the Twitch API. The existing ban list is read first, so accounts that are already banned are
  skipped.
- **Your own people are protected.** The broadcaster, the bot account, mods and VIPs are never banned.
- **Service bots are left alone.** Nightbot, StreamElements and other well-known hosted bots, the hosted
  bots on TwitchBots.info and the ISDS whitelisted, greylisted and unban lists are skipped unless they are
  in your bot group.
- **Wrong bans are undone.** Protected service bots and your own accounts that AutoBotCheck banned are
  unbanned. Bans with any other reason are never touched.

### Changed

- **One script for all actions.** The `botSource` argument selects the bot list.
- **Bot lists are cached for an hour** instead of a minute.
- **One check at a time.** Starting an action while its check is still running no longer starts a
  second one.
- **The cache of accounts that no longer exist** is also saved when a check is stopped, and is locked
  while it is written.
- **Twitch's rate limit** is waited out until its reset instead of for a fixed 5 seconds.

## [1.0.0]

The first release. Two actions, `TwitchInsight` and `TwitchBots.Info`, with a bot group of your own, a
whitelist group and a cache of accounts that no longer exist.
