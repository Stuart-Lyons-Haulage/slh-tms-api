from pathlib import Path


def add(path: str, prefix: str) -> None:
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    if text.startswith(prefix):
        return
    p.write_text(prefix + text, encoding='utf-8')

add('Slh.Tms.Jobs/ScheduledJobRunner.cs', 'using Microsoft.Extensions.Logging;\n')
add('Slh.Tms.Jobs/TachoMasterScheduledJob.cs', 'using Microsoft.Extensions.Logging;\n')
add('Slh.Tms.Jobs/EtaRecalculationJob.cs', 'using Microsoft.Extensions.Configuration;\n')
add('Slh.Tms.Jobs/Program.cs', 'using Microsoft.Extensions.Configuration;\nusing Microsoft.Extensions.DependencyInjection;\nusing Microsoft.Extensions.Hosting;\n')
