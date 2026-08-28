from pathlib import Path

p = Path('Services/TachoDriverMasterSyncService.cs')
text = p.read_text()
old = '''            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
'''
new = '''            await transaction.RollbackAsync(ct);
            await transaction.DisposeAsync();
            db.ChangeTracker.Clear();
'''
if old not in text:
    raise SystemExit('rollback patch target not found')
p.write_text(text.replace(old, new, 1))
