#!/bin/sh
# Build imageviewer (without copying it to C:\app) and run the rotation tests.
# Usage: test/run.sh
set -e
cd "$(dirname "$0")/.."
VSWHERE="/mnt/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe"
MSBUILD=$(wslpath -u "$("$VSWHERE" -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | tr -d '\r' | head -1)")
"$MSBUILD" "$(wslpath -w imageviewer.csproj)" -nologo -v:minimal -p:Configuration=Release -p:DeployDir=none
if [ ! -x test/.venv/bin/python ]; then
  python3 -m venv test/.venv
  test/.venv/bin/pip install -q -r test/requirements.txt
fi
cd test
exec .venv/bin/python test_rotation.py
