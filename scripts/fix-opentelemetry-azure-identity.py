from pathlib import Path

path = Path('Slh.Tms.Api.csproj')
text = path.read_text(encoding='utf-8')
old = '<PackageReference Include="Azure.Identity" Version="1.13.1" />'
new = '<PackageReference Include="Azure.Identity" Version="1.21.0" />'
if text.count(old) != 1:
    raise SystemExit(f'Expected one Azure.Identity 1.13.1 reference, found {text.count(old)}')
path.write_text(text.replace(old, new), encoding='utf-8')
print('Azure.Identity aligned to 1.21.0.')
