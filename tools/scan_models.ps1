param([string]$Path)
$fs = [IO.File]::OpenRead($Path)
$br = New-Object IO.BinaryReader($fs)
$null = $br.ReadBytes(4)
$null = $br.ReadUInt32()
$tensorCount = $br.ReadUInt64()
$kvCount = $br.ReadUInt64()
function Read-Str($br) { $len = $br.ReadUInt64(); [Text.Encoding]::UTF8.GetString($br.ReadBytes([int]$len)) }
function Skip-Val($br, $type) {
    switch ($type) {
        0 { $null = $br.ReadByte() } 1 { $null = $br.ReadSByte() } 2 { $null = $br.ReadUInt16() }
        3 { $null = $br.ReadInt16() } 4 { $null = $br.ReadUInt32() } 5 { $null = $br.ReadInt32() }
        6 { $null = $br.ReadSingle() } 7 { $null = $br.ReadBoolean() }
        8 { $null = Read-Str $br } 10 { $null = $br.ReadUInt64() } 11 { $null = $br.ReadInt64() } 12 { $null = $br.ReadDouble() }
        9 { $et = $br.ReadUInt32(); $cnt = $br.ReadUInt64(); for ($i=0; $i -lt $cnt; $i++) { Skip-Val $br $et } }
    }
}
$arch = ''; $layers = 0; $ctx = 0; $moe = $false; $mtp = $false; $fileType = 0; $ssm = $false
for ($i=0; $i -lt $kvCount; $i++) {
    $key = Read-Str $br
    $type = $br.ReadUInt32()
    if ($key -eq 'general.architecture' -and $type -eq 8) { $arch = Read-Str $br; continue }
    if ($type -eq 9) { $et = $br.ReadUInt32(); $cnt = $br.ReadUInt64(); for ($j=0; $j -lt $cnt; $j++) { Skip-Val $br $et }; continue }
    if ($type -eq 4 -or $type -eq 5) { $v = [uint32]$br.ReadInt32() } elseif ($type -eq 8) { $null = Read-Str $br; continue } else { Skip-Val $br $type; continue }
    if ($key -match '\.block_count$') { $layers = $v }
    elseif ($key -match '\.context_length$') { $ctx = $v }
    elseif ($key -match '\.expert_count$' -and $v -gt 1) { $moe = $true }
    elseif ($key -match '\.n_expert$' -and $v -gt 1) { $moe = $true }
    elseif ($key -match '\.nextn_predict_layers$' -and $v -gt 0) { $mtp = $true }
    elseif ($key -match '\.ssm\.' -or $key -match 'recurrent') { $ssm = $true }
    elseif ($key -eq 'general.file_type') { $fileType = $v }
}
$names = New-Object System.Collections.Generic.List[string]
for ($i=0; $i -lt $tensorCount; $i++) {
    $name = Read-Str $br
    if (-not $mtp -and $name.Contains('.nextn.')) { $mtp = $true }
    $nd = $br.ReadUInt32()
    $null = $br.ReadBytes(8 * $nd)
    $null = $br.ReadUInt32()
    $null = $br.ReadUInt64()
}
$fs.Close()
$name = [IO.Path]::GetFileName($Path)
"{0,-52} arch={1,-8} layers={2,-3} ctx={3,-7} MoE={4,-5} MTP={5,-5} SSM={6}" -f $name, $arch, $layers, $ctx, $moe, $mtp, $ssm
