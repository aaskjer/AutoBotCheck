"""Builds AutoBotCheck_v1.1.sb (Streamer.bot import string) from the original export and src/AutoBotCheck.cs.

Usage: python -I src/build_sb.py <original.sb> <output.sb>
"""
import base64
import copy
import gzip
import json
import sys
import uuid
from pathlib import Path

HEADER = b"SBAE"
SRC = Path(__file__).with_name("AutoBotCheck.cs")

# Comment colors as used in the original export.
WARNING, SEPARATOR, NOTE, IMPORTANT = "#FF0000", "#323232", "#FFFF80", "#FF8080"

COMMENTS = [
    ("First run can take a while (Twitch allows ~800 API calls/min). Later runs only ban accounts that are not banned yet.", WARNING),
    ("NOT recommended running this script while being LIVE on stream!", WARNING),
    ("CAUTION: Running this with SB on debug level can cause a very big log file!", WARNING),
    ("==========================================", SEPARATOR),
    ("First 'Test' trigger starts the script", NOTE),
    ("Second 'Test' trigger stops the running check of this action (keep the action in a non-blocking queue).", NOTE),
    ("==========================================", SEPARATOR),
    ("Create a Bot and a whitelist group here: Settings > Groups", NOTE),
    ("Whitelist group: never banned; unbanned if it is on the bot list/group or AutoBotCheck banned it.", NOTE),
    ("Bot group: your own extra bots, banned even if on a safelist. Accounts that no longer exist are removed.", NOTE),
    ("Never banned: broadcaster, bot account, mods, VIPs. Known service bots (Nightbot, StreamElements, ...) are skipped unless they are in your bot group.", IMPORTANT),
    ("Set dryRun to True to only log what would be (un)banned. Groups and the non-existent cache are still updated.", NOTE),
    ("Fast runs need Streamer.bot's broadcaster token. Without it every run is slow and re-bans all bots (see log warnings).", IMPORTANT),
]
ISDS_COMMENTS = [
    ("isdsLists (comma separated, default: the first four): follower_bot_list, viewer_bot_list, spam_bot_list, seller_advertising_list,", NOTE),
    ("porn_bot_acc_list, hate_troll_list (= all 3 parts), mad_tos_list, security_ban_list", NOTE),
    ("Lists: github.com/isdsdataarchive/twitch_ban_lists - unban requests: unban@isds.tech", NOTE),
]
DEFAULT_ISDS_LISTS = "follower_bot_list,viewer_bot_list,spam_bot_list,seller_advertising_list"


def decode(text: str) -> dict:
    raw = base64.b64decode(text)
    assert raw[:4] == HEADER, "not a Streamer.bot export"
    return json.loads(gzip.decompress(raw[4:]).decode("utf-8"))


def encode(data: dict) -> str:
    payload = json.dumps(data, indent=2, ensure_ascii=False).encode("utf-8")
    return base64.b64encode(HEADER + gzip.compress(payload, mtime=0)).decode("ascii")


def new_id() -> str:
    return str(uuid.uuid4())


def rebuild_action(action: dict, name: str, source: str, code_b64: str, fresh_ids: bool) -> dict:
    action = copy.deepcopy(action)
    subs = action["subActions"]
    comment_tpl = next(s for s in subs if s["type"] == 1009)
    arg_tpl = next(s for s in subs if s["type"] == 123)
    group = next(s for s in subs if s["type"] == 99900)
    old_args = {s["variableName"]: s for s in subs if s["type"] == 123}

    def comment(text, color):
        c = copy.deepcopy(comment_tpl)
        c.update(id=new_id(), value=text, color=color)
        return c

    def argument(variable, value):
        a = copy.deepcopy(old_args.get(variable, arg_tpl))
        if variable not in old_args or fresh_ids:
            a["id"] = new_id()
        a.update(variableName=variable, value=value)
        return a

    comments = COMMENTS + (ISDS_COMMENTS if source == "isds" else [])
    arguments = [
        argument("botGroupName", old_args["botGroupName"]["value"]),
        argument("whitelistGroupName", old_args["whitelistGroupName"]["value"]),
        argument("botSource", source),
    ]
    if source == "isds":
        arguments.append(argument("isdsLists", DEFAULT_ISDS_LISTS))
    arguments.append(argument("dryRun", "False"))

    group = copy.deepcopy(group)
    code = group["subActions"][0]
    code["byteCode"] = code_b64
    if fresh_ids:
        group["id"] = new_id()
        code["id"] = new_id()
        code["parentId"] = group["id"]
        action["id"] = new_id()
        for trigger in action["triggers"]:
            trigger["id"] = new_id()
    action["collapsedGroups"] = [group["id"]]

    new_subs = [comment(t, c) for t, c in comments] + arguments + [group]
    for index, sub in enumerate(new_subs):
        sub["index"] = index
    action["subActions"] = new_subs
    action["name"] = name
    return action


def main() -> None:
    original, output = Path(sys.argv[1]), Path(sys.argv[2])
    data = decode(original.read_text(encoding="ascii").strip())
    source_code = SRC.read_text(encoding="utf-8").replace("\r\n", "\n").replace("\n", "\r\n")
    code_b64 = base64.b64encode(source_code.encode("utf-8")).decode("ascii")

    actions = {a["name"]: a for a in data["data"]["actions"]}
    data["data"]["actions"] = [
        rebuild_action(actions["TwitchInsight"], "TwitchInsight", "twitchinsights", code_b64, False),
        rebuild_action(actions["TwitchBots.Info"], "TwitchBots.Info", "twitchbotsinfo", code_b64, False),
        rebuild_action(actions["TwitchInsight"], "ISDS.BanLists", "isds", code_b64, True),
    ]
    data["meta"]["version"] = "1.1.0"
    data["meta"]["description"] = (
        "Bans known bots from TwitchInsights, TwitchBots.info and the ISDS ban lists. "
        "Batch Helix lookups, skips already banned accounts, protects service bots."
    )
    encoded = encode(data)
    output.write_text(encoded, encoding="ascii", newline="")
    assert decode(encoded) == data
    print(f"wrote {output} ({len(encoded)} chars)")


if __name__ == "__main__":
    main()
