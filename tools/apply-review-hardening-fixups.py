from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one fixup match, found {count}: {old!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")


replace_once(
    "Services/BetaRequestRouteCache.cs",
    """        lock (gate)\n        {\n            if (!entries.TryGetValue(key, out task!))\n            {\n                task = factory();\n                entries[key] = task;\n            }\n        }""",
    """        lock (gate)\n        {\n            if (entries.TryGetValue(key, out var existing))\n            {\n                task = existing;\n            }\n            else\n            {\n                task = factory();\n                entries[key] = task;\n            }\n        }""")

wallboard = Path("Services/WallboardPlannedRunPreparation.cs")
text = wallboard.read_text(encoding="utf-8")
if "\\n" not in text:
    raise RuntimeError("Expected escaped newline fixup in WallboardPlannedRunPreparation.cs")
wallboard.write_text(text.replace("\\n", "\n"), encoding="utf-8")

print("Hardening patch fixups applied successfully.")
