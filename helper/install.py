#!/usr/bin/env python3
"""Install only the packaged helper; never modify CLI credentials or launchers."""
import argparse
import json
import os
from pathlib import Path
import shutil
import sys

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--destination", type=Path, default=Path.home() / ".local/share/code-usage-monitor")
args = parser.parse_args()
if sys.version_info < (3, 11):
    parser.error("Python 3.11 or later is required")
os.umask(0o077)
source = Path(__file__).resolve().parent
target = args.destination.expanduser().resolve()
if target == source or source in target.parents or target in source.parents:
    parser.error("Choose an installation directory separate from the source")
target.mkdir(parents=True, exist_ok=True, mode=0o700)
target.chmod(0o700)
for path in [source / "monitor.py", *sorted((source / "code_usage_monitor").glob("*.py"))]:
    relative = path.relative_to(source)
    destination = target / relative
    destination.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    temporary = destination.with_suffix(".tmp")
    shutil.copyfile(path, temporary)
    temporary.chmod(0o600)
    temporary.replace(destination)
print(json.dumps({"version": 1, "status": "ready", "helper_path": str(target / "monitor.py")}))
