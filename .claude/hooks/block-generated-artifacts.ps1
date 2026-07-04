# PreToolUse hook: block Edit/Write into generated-artifact directories.
# AGENTS.md: do not commit generated runtime artifacts.
# Exit 2 blocks the tool call; stderr is fed back to Claude.
$raw = [Console]::In.ReadToEnd()
if (-not $raw) { exit 0 }
try { $data = $raw | ConvertFrom-Json } catch { exit 0 }
$path = $data.tool_input.file_path
if (-not $path) { exit 0 }
if ($path -match '[\\/](bin|obj|artifacts)[\\/]') {
    # The one committed exception kept in .gitignore:
    if ($path -notmatch '[\\/]artifacts[\\/]dashboard-input[\\/]README\.md$') {
        [Console]::Error.WriteLine("Blocked: '$path' is inside a generated-artifacts directory (bin/, obj/, artifacts/). Per AGENTS.md, generated runtime artifacts must not be edited or committed. Edit the source that produces this file instead.")
        exit 2
    }
}
exit 0
