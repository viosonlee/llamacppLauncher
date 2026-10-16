# Documentation gate: run before committing doc or behavior changes.
#
#   pwsh -File tools/docs-gate.ps1
#
# Checks (see REQ-070 / docs/REQUIREMENTS.md):
#   1. Required documents exist.
#   2. Every REQ-xxx id in REQUIREMENTS.md is defined exactly once.
#   3. Every REQ-xxx referenced by CHANGELOG-DECISIONS.md or AGENTS.md is defined.
#   4. Every status line in REQUIREMENTS carries a status.
#   5. README describes the same engine ids the code registers.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$failures = [System.Collections.Generic.List[string]]::new()
function Fail($m) { $script:failures.Add($m) }

# 1. Required documents.
foreach ($doc in 'README.md', 'docs/REQUIREMENTS.md', 'docs/CHANGELOG-DECISIONS.md',
                 'docs/architecture.md', 'docs/engine-plugins.md',
                 'docs/AGENT-GUIDE.md', 'THIRD-PARTY-NOTICES.md') {
    if (-not (Test-Path (Join-Path $root $doc))) { Fail "缺少文档：$doc" }
}

# 2/4. Requirement ids unique and each carries a status.
$reqText = Get-Content (Join-Path $root 'docs/REQUIREMENTS.md') -Raw -Encoding UTF8
$defined = [System.Collections.Generic.List[string]]::new()
foreach ($m in [regex]::Matches($reqText, 'REQ-\d{3}')) {
    if ($defined -notcontains $m.Value) { $defined.Add($m.Value) }
}
$dupes = [regex]::Matches($reqText, 'REQ-\d{3}') |
    Group-Object Value | Where-Object Count -gt 1 |
    ForEach-Object { "$($_.Name) 定义出现 $($_.Count) 次" }
# Ids may repeat in cross-references; only count the leading id of each bullet as a definition.
$definitionLines = Select-String (Join-Path $root 'docs/REQUIREMENTS.md') -Pattern '^- (REQ-\d{3})' -AllMatches
$defs = $definitionLines.Matches.Groups[1].Matches.Value  # placeholder, rebuilt below
$definitionIds = @($definitionLines | ForEach-Object { $_.Matches[0].Groups[1].Value })
$definitionDuplicates = $definitionIds | Group-Object | Where-Object Count -gt 1
foreach ($d in $definitionDuplicates) { Fail "需求编号重复定义：$($d.Name)" }
foreach ($id in ($defined | Where-Object { $_ -notin $definitionIds })) { Fail "需求编号被引用但未定义（未以 '- $id' 开头）：$id" }
foreach ($line in $definitionLines) {
    if ($line.Line -notmatch '已实现|部分实现|规划中|已废弃') { Fail "需求缺状态：$($line.Line)" }
}

# 3. Cross-references resolve.
foreach ($doc in 'docs/CHANGELOG-DECISIONS.md', 'AGENTS.md', 'docs/engine-plugins.md', 'docs/AGENT-GUIDE.md', 'README.md') {
    $path = Join-Path $root $doc
    if (-not (Test-Path $path)) { continue }
    $text = Get-Content $path -Raw -Encoding UTF8
    foreach ($m in [regex]::Matches($text, 'REQ-\d{3}')) {
        if ($definitionIds -notcontains $m.Value) { Fail "$doc 引用了未定义的 $($m.Value)" }
    }
}

# 5. README engine ids match the registry ids in code.
$readme = Get-Content (Join-Path $root 'README.md') -Raw -Encoding UTF8
foreach ($id in 'llamacpp', 'strata-upstream', 'polystrata') {
    if ($readme -notmatch [regex]::Escape($id)) { Fail "README 未描述引擎 $id" }
}

if ($failures.Count -gt 0) {
    Write-Host "文档门禁未通过："
    $failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
Write-Host "文档门禁通过（$($definitionIds.Count) 条需求）。"
