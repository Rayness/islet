"""Islet plugin example: passwords and UUIDs.

Islet starts this script once and talks to it with one JSON object per line
over stdin/stdout. Everything the protocol can do is shown here:

  <- {"type": "hello", "api": 1, "language": "ru", ...}
  <- {"type": "query", "id": 7, "text": "24", "scoped": true}
  -> {"type": "results", "id": 7, "items": [...]}
  <- {"type": "invoke", "action": "regenerate", "data": {...}}
  -> {"type": "notify", ...}      a peek on the island
  -> {"type": "activity", ...}    a live activity in the collapsed capsule

Nothing is written to stdout except protocol messages: print() debugging goes
to stderr, which Islet copies into its log.
"""

import json
import secrets
import string
import sys
import time
import uuid

LANGUAGE = "en"

# Islet sets PYTHONIOENCODING=utf-8, but a plugin started by hand from a console
# must not fall back to the console code page either.
sys.stdin.reconfigure(encoding="utf-8")
sys.stdout.reconfigure(encoding="utf-8")


def t(en: str, ru: str) -> str:
    return ru if LANGUAGE == "ru" else en


def send(message: dict) -> None:
    sys.stdout.write(json.dumps(message, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def password(length: int, symbols: bool) -> str:
    alphabet = string.ascii_letters + string.digits + ("!@#$%^&*-_=+?" if symbols else "")
    while True:
        value = "".join(secrets.choice(alphabet) for _ in range(length))
        # At least one digit and one of each case — some sites insist.
        if any(c.isdigit() for c in value) and any(c.islower() for c in value) and any(c.isupper() for c in value):
            return value


def row(title: str, subtitle: str, value: str) -> dict:
    return {
        "title": title,
        "subtitle": subtitle,
        "trailing": t("Enter to copy", "Enter — копировать"),
        "glyph": "\ue8d7",
        # Built-in actions need no round trip: Islet copies the text itself.
        "action": {"copy": value},
        "actions": [
            {"title": t("Paste into the window", "Вставить в окно"), "glyph": "\ue77f", "action": {"paste": value}},
            # An "invoke" comes back to this script as {"type": "invoke", ...}.
            {"title": t("Generate 5 more (notify)", "Ещё 5 штук уведомлением"), "glyph": "\ue72c",
             "action": {"invoke": "batch", "data": {"length": len(value)}}},
        ],
    }


def answer(query_id: int, text: str) -> None:
    text = text.strip().lower()
    if text in ("uuid", "guid"):
        values = [str(uuid.uuid4()) for _ in range(3)]
        items = [row(v, "UUID v4", v) for v in values]
    else:
        length = int(text) if text.isdigit() and 6 <= int(text) <= 128 else 20
        strong = password(length, True)
        plain = password(length, False)
        fresh = str(uuid.uuid4())
        items = [
            row(strong, t(f"{length} characters, with symbols", f"{length} символов, с символами"), strong),
            row(plain, t(f"{length} characters, letters and digits", f"{length} символов, буквы и цифры"), plain),
            row(fresh, "UUID v4", fresh),
        ]
    send({"type": "results", "id": query_id, "items": items})


def batch(length: int) -> None:
    # A live activity while "working" — shows up in the collapsed capsule.
    for step in range(1, 6):
        send({"type": "activity", "id": "batch", "text": t("Generating…", "Генерирую…"), "progress": step / 5})
        time.sleep(0.25)
    send({"type": "activity", "id": "batch", "clear": True})
    passwords = "\n".join(password(length, True) for _ in range(5))
    send({
        "type": "notify",
        "title": t("5 new passwords are ready", "Готово 5 новых паролей"),
        "body": t("Click to copy them, one per line", "Щёлкните, чтобы скопировать — по одному на строку"),
        "glyph": "\ue8d7",
        "action": {"copy": passwords},
    })


def main() -> None:
    global LANGUAGE
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            message = json.loads(line)
        except json.JSONDecodeError:
            continue
        kind = message.get("type")
        if kind == "hello":
            LANGUAGE = message.get("language", "en")
        elif kind == "query":
            answer(message["id"], message.get("text", ""))
        elif kind == "invoke" and message.get("action") == "batch":
            batch(int(message.get("data", {}).get("length", 20)))
        elif kind == "shutdown":
            break


if __name__ == "__main__":
    main()
