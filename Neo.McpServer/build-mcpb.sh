#!/bin/bash
# Build an .mcpb bundle for Neo.McpServer.
# Usage: ./build-mcpb.sh [win-x64|linux-x64|osx-x64|osx-arm64]
# Output: dist/neo-live-preview-<rid>.mcpb

set -e

RID="${1:-win-x64}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
MCPB_STAGE="$REPO_ROOT/dist/mcpb-stage-$RID"
MCPB_OUT="$REPO_ROOT/dist/neo-live-preview-$RID.mcpb"

echo "=== Building N.E.O. MCPB Bundle ==="
echo "RID:    $RID"
echo "Output: $MCPB_OUT"
echo ""

rm -rf "$MCPB_STAGE"
mkdir -p "$MCPB_STAGE/server"

# 1. Publish McpServer (self-contained)
echo "[1/4] Publishing Neo.McpServer (self-contained, $RID)..."
dotnet publish "$REPO_ROOT/Neo.McpServer/Neo.McpServer.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false \
    -o "$MCPB_STAGE/server" \
    --nologo -v quiet

# 2. Publish PluginWindowAvalonia (self-contained, same RID)
echo "[2/4] Publishing Neo.PluginWindowAvalonia (self-contained, $RID)..."
PLUGIN_TEMP="$MCPB_STAGE/plugin-temp"
dotnet publish "$REPO_ROOT/Neo.PluginWindowAvalonia/Neo.PluginWindowAvalonia.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false \
    -o "$PLUGIN_TEMP" \
    --nologo -v quiet

# Merge PluginWindow into server directory (shared runtime)
cp -n "$PLUGIN_TEMP"/* "$MCPB_STAGE/server/" 2>/dev/null || true
rm -rf "$PLUGIN_TEMP"

# 3. Create platform-specific manifest
echo "[3/4] Creating manifest.json..."

# Determine platform string and executable extension
case "$RID" in
    win-*)   PLATFORM="win32";  EXE_EXT=".exe" ;;
    linux-*) PLATFORM="linux";  EXE_EXT="" ;;
    osx-*)   PLATFORM="darwin"; EXE_EXT="" ;;
    *)       PLATFORM="win32";  EXE_EXT=".exe" ;;
esac

cat > "$MCPB_STAGE/manifest.json" << MANIFEST_EOF
{
  "manifest_version": "0.3",
  "name": "neo-live-preview",
  "display_name": "N.E.O. Live Preview",
  "version": "0.1.0",
  "description": "Compile and display live Avalonia desktop apps from natural language. Describe an app — Claude generates C# code, compiles it via Roslyn in ~1 second, and shows it in a real native window on your desktop.",
  "long_description": "N.E.O. (Native Executable Orchestrator) MCP Server enables Claude to create real, native desktop applications on the fly. Claude writes C# Avalonia code, the server compiles it at runtime using Roslyn, and streams the compiled DLL to a live preview window via Named Pipes. No SDK required — the server is fully self-contained. Supports hot-reload: ask for changes and the preview updates in place.",
  "author": {
    "name": "hf75",
    "url": "https://github.com/hf75"
  },
  "repository": {
    "type": "git",
    "url": "https://github.com/hf75/N.E.O"
  },
  "homepage": "https://github.com/hf75/N.E.O",
  "documentation": "https://github.com/hf75/N.E.O/wiki/MCP-Server",
  "support": "https://github.com/hf75/N.E.O/issues",
  "server": {
    "type": "binary",
    "entry_point": "server/Neo.McpServer${EXE_EXT}",
    "mcp_config": {
      "command": "\${__dirname}/server/Neo.McpServer${EXE_EXT}",
      "args": [],
      "env": {}
    }
  },
  "tools": [
    {
      "name": "compile_and_preview",
      "description": "Compile C# Avalonia code and show it in a live desktop window"
    },
    {
      "name": "update_preview",
      "description": "Hot-reload modified code in the existing preview window"
    },
    {
      "name": "close_preview",
      "description": "Close the preview window"
    },
    {
      "name": "get_preview_status",
      "description": "Check if a preview window is running"
    }
  ],
  "keywords": ["avalonia", "desktop", "live-preview", "roslyn", "csharp", "native", "gui", "app-builder"],
  "license": "MIT",
  "compatibility": {
    "platforms": ["${PLATFORM}"],
    "runtimes": {}
  }
}
MANIFEST_EOF

# 4. Pack as .mcpb (ZIP)
echo "[4/4] Packing .mcpb bundle..."
rm -f "$MCPB_OUT"
if command -v zip &>/dev/null; then
    (cd "$MCPB_STAGE" && zip -r -q "$MCPB_OUT" .)
else
    # Fallback: use PowerShell on Windows
    MCPB_STAGE_WIN=$(cygpath -w "$MCPB_STAGE" 2>/dev/null || echo "$MCPB_STAGE")
    MCPB_OUT_WIN=$(cygpath -w "$MCPB_OUT" 2>/dev/null || echo "$MCPB_OUT")
    MCPB_ZIP_WIN="${MCPB_OUT_WIN%.mcpb}.zip"
    powershell -NoProfile -Command "Compress-Archive -Path '$MCPB_STAGE_WIN\\*' -DestinationPath '$MCPB_ZIP_WIN' -Force"
    mv "${MCPB_OUT%.mcpb}.zip" "$MCPB_OUT"
fi

# Cleanup staging
rm -rf "$MCPB_STAGE"

# Summary
echo ""
echo "=== Done ==="
echo "Bundle: $MCPB_OUT"
echo "Size:   $(du -sh "$MCPB_OUT" | cut -f1)"
echo ""
echo "Install in Claude Desktop:"
echo "  1. Double-click the .mcpb file, or"
echo "  2. Drag-and-drop into Claude Desktop, or"
echo "  3. Developer > Extensions > Install Extension"
