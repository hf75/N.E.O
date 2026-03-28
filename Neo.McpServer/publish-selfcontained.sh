#!/bin/bash
# Publish Neo.McpServer + Neo.PluginWindowAvalonia as a self-contained package.
# Usage: ./publish-selfcontained.sh [win-x64|linux-x64|osx-x64|osx-arm64]
# Output: publish/<rid>/neo-mcp-server/

set -e

RID="${1:-win-x64}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_DIR="$REPO_ROOT/publish/$RID/neo-mcp-server"

echo "=== Neo.McpServer Self-Contained Publish ==="
echo "RID:    $RID"
echo "Output: $OUT_DIR"
echo ""

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

# 1. Publish McpServer (self-contained)
echo "[1/3] Publishing Neo.McpServer..."
dotnet publish "$REPO_ROOT/Neo.McpServer/Neo.McpServer.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -o "$OUT_DIR" \
    --nologo -v quiet

# 2. Publish PluginWindowAvalonia (self-contained, same RID)
echo "[2/3] Publishing Neo.PluginWindowAvalonia..."
PLUGIN_TEMP="$OUT_DIR/plugin-temp"
dotnet publish "$REPO_ROOT/Neo.PluginWindowAvalonia/Neo.PluginWindowAvalonia.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -o "$PLUGIN_TEMP" \
    --nologo -v quiet

# 3. Merge: copy PluginWindow files that aren't already in the output
echo "[3/3] Merging into single directory..."
cp -n "$PLUGIN_TEMP"/* "$OUT_DIR/" 2>/dev/null || true
rm -rf "$PLUGIN_TEMP"

# Summary
echo ""
echo "=== Done ==="
MCP_EXE="Neo.McpServer"
PLUGIN_EXE="Neo.PluginWindowAvalonia"
if [[ "$RID" == win-* ]]; then
    MCP_EXE="Neo.McpServer.exe"
    PLUGIN_EXE="Neo.PluginWindowAvalonia.exe"
fi

echo "Files: $(find "$OUT_DIR" -type f | wc -l)"
echo "Size:  $(du -sh "$OUT_DIR" | cut -f1)"
echo ""
echo "To use with Claude Cowork/Code, add to your MCP settings:"
echo ""
echo '  {'
echo '    "mcpServers": {'
echo '      "neo-preview": {'
echo "        \"command\": \"$OUT_DIR/$MCP_EXE\""
echo '      }'
echo '    }'
echo '  }'
echo ""
echo "No .NET installation required. No NEO_PLUGIN_PATH needed (PluginWindow is in the same directory)."
