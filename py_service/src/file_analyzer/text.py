from __future__ import annotations

import re
import unicodedata


def normalize_text(value: str) -> str:
    value = unicodedata.normalize("NFKC", value.replace("\x00", ""))
    value = "".join(char for char in value if char in "\n\t" or unicodedata.category(char) != "Cc")
    lines = [re.sub(r"[ \t]+", " ", line).strip() for line in value.splitlines()]
    return "\n".join(line for line in lines if line)


def meaningful_character_count(value: str) -> int:
    return sum(1 for char in value if not char.isspace() and unicodedata.category(char)[0] != "C")

