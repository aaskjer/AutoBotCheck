<h1 align="center">AutoBotCheck</h1>

<p align="center">
  <b>Bans the bots from public bot lists in your Twitch channel.</b><br>
  Three lists, three actions, and every later run only bans what is new.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Twitch-9146FF" alt="Platform">
  <img src="https://img.shields.io/badge/tool-streamer.bot-0b73ff" alt="Streamer.bot">
  <img src="https://img.shields.io/github/license/aaskjer/AutoBotCheck" alt="License">
  <img src="https://img.shields.io/github/v/release/aaskjer/AutoBotCheck" alt="Release">
  <img src="https://img.shields.io/github/downloads/aaskjer/AutoBotCheck/total" alt="Downloads">
</p>

<p align="center">
  <a href="https://github.com/aaskjer/AutoBotCheck/releases/latest"><b>Download</b></a> &nbsp;·&nbsp;
  <a href="Setup-Guide.md"><b>Setup Guide</b></a> &nbsp;·&nbsp;
  <a href="Import-String.md"><b>Import String</b></a> &nbsp;·&nbsp;
  <a href="CHANGELOG.md"><b>Changelog</b></a>
</p>

---

## 📖 Overview

AutoBotCheck is a set of three Streamer.bot actions. Each one downloads a public bot list (TwitchInsights,
TwitchBots.info or the ISDS ban lists), keeps a Streamer.bot group in sync with it and bans every listed
account that is not banned in your channel yet. The bots in your own bot group are banned along with them,
the accounts in your whitelist never.

```
SUMMARY (twitchinsights):
  TwitchInsightBots: 14248 members (+236 / -12)
  Bots: 3 members (-0)
  Bans: 230 new, 13980 already banned, 0 failed
  Unbans: 0 done, 0 failed | Protected: 41
```

## ✨ Highlights

- 📋 **Three bot lists.** TwitchInsights, TwitchBots.info and the ISDS ban lists, each in an action of its
  own. You pick which of the ISDS lists are used.
- ⚡ **Only what is new.** With the broadcaster token, AutoBotCheck reads your ban list once and bans only
  the accounts that are not banned yet. Accounts are looked up 100 at a time.
- 🛡️ **Your people stay.** The broadcaster, your bot account, mods, VIPs and the whitelist group are
  never banned.
- 🤖 **Service bots are left alone.** Nightbot, StreamElements, Streamlabs and other hosted bots are
  skipped, unless you put them in your own bot group.
- ↩️ **Mistakes undone.** A protected account that AutoBotCheck banned is unbanned on the next run. Bans
  you made yourself are never touched.
- 🧪 **Dry run.** With `dryRun` set to `True` the log shows what would be banned, and nobody is.
- ⏹️ **Stops when you say so.** The second `Test` trigger stops a running check at the next checkpoint.

---

## Credits

- **Author:** [aaskjer on Twitch](https://twitch.tv/aaskjer) · [aaskjer.github.io](https://aaskjer.github.io/)
- **Built for:** [Streamer.bot](https://streamer.bot) by [nate1280](https://www.patreon.com/c/nate1280/home)
- **Data & services:**
  - [TwitchInsights](https://twitchinsights.net/bots): bot list
  - [TwitchBots.info](https://twitchbots.info): bot list and hosted service bots
  - [ISDS Twitch ban lists](https://github.com/isdsdataarchive/twitch_ban_lists): ban lists, white, grey and unban lists
- **Thanks:** YOU ♡

None of the projects above is affiliated with or endorses AutoBotCheck.
