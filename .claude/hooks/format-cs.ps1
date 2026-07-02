# PostToolUse hook: run dotnet whitespace formatting on an edited C# file.
# Whitespace-only so it is safe without an .editorconfig; always exits 0 so a
# formatting hiccup never fails the edit.
$raw = [Console]::In.ReadToEnd()
if (-not $raw) { exit 0 }
try { $data = $raw | ConvertFrom-Json } catch { exit 0 }
$path = $data.tool_input.file_path
if (-not $path -or $path -notlike '*.cs') { exit 0 }
if ($path -match '[\\/](bin|obj|artifacts)[\\/]') { exit 0 }
if (-not (Test-Path -LiteralPath $path)) { exit 0 }
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$rel = [System.IO.Path]::GetRelativePath($repoRoot, (Resolve-Path -LiteralPath $path).Path)
if ($rel.StartsWith('..')) { exit 0 }
try { dotnet format whitespace $repoRoot --folder --include $rel *> $null } catch {}
exit 0
