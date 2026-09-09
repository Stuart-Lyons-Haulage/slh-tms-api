from pathlib import Path

path = Path("tools/apply-review-hardening.py")
text = path.read_text(encoding="utf-8")

needle1 = r'''collectionLabel)\n            ?? ExtractMatch(CollectionTimeRegex'''
replacement1 = '''collectionLabel)
            ?? ExtractMatch(CollectionTimeRegex'''
count1 = text.count(needle1)
if count1 != 2:
    raise RuntimeError(f"Expected two collectionLabel escaped-newline matches, found {count1}")
text = text.replace(needle1, replacement1)

needle2 = r'''ExtractMatch(CollectionTimeRegex, body, "time")\n            ??'''
replacement2 = '''ExtractMatch(CollectionTimeRegex, body, "time")
            ??'''
count2 = text.count(needle2)
if count2 != 2:
    raise RuntimeError(f"Expected two CollectionTimeRegex escaped-newline matches, found {count2}")
text = text.replace(needle2, replacement2)

path.write_text(text, encoding="utf-8")
print("Hardening patch source newline matching corrected.")
