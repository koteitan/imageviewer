#!/bin/sh
# Build from WSL with the MSBuild of Visual Studio on Windows.
# Output: bin/Release/ImageViewer.exe
set -e
cd "$(dirname "$0")"
VSWHERE="/mnt/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe"
MSBUILD_WIN=$("$VSWHERE" -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | tr -d '\r' | head -1)
MSBUILD=$(wslpath -u "$MSBUILD_WIN")
"$MSBUILD" "$(wslpath -w ImageViewer.csproj)" -nologo -v:minimal -p:Configuration="${1:-Release}"
# WSL needs the execute bit to run a Windows exe.
chmod +x "bin/${1:-Release}/ImageViewer.exe"
