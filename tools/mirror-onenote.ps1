# Mirrors the project documents to the OneNote section "ERP" (notebook "Ahmed @ Takamol").
# Only documents whose content (markdown plus referenced images) changed since the last run are rebuilt.
# Must run under Windows PowerShell 5.1 (powershell.exe): the OneNote COM interface fails under pwsh 7.
# Usage:  powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\mirror-onenote.ps1 [-Force] [-Only <substring>]
param([switch]$Force, [string]$Only = "")

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$sectionId = '{AEA300B0-2319-4082-B017-508A25CF5EAC}{1}{B0}'
$state = Join-Path $env:LOCALAPPDATA 'erp-onenote'
$render = Join-Path $state 'render'
New-Item -ItemType Directory -Force $state, $render | Out-Null
$manifestPath = Join-Path $state 'manifest.json'
$log = Join-Path $state 'last-run.log'
$mmdc = 'npx'   # @mermaid-js/mermaid-cli, cached by npx

function Log($m) { $line = "{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $m; Add-Content -Path $log -Value $line; Write-Output $line }

# One mirror at a time.
$mutex = New-Object System.Threading.Mutex($false, 'Global\erp-onenote-mirror')
if (-not $mutex.WaitOne(900000)) { Log 'another mirror run held the lock for 15 minutes; giving up'; exit 1 }

try {
  $manifest = @{}
  if (Test-Path $manifestPath) { (Get-Content $manifestPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $manifest[$_.Name] = $_.Value } }

  $docs = @(Get-ChildItem (Join-Path $repo 'docs') -File | Where-Object { $_.Name -match '^\d\d-.*\.md$' } | Sort-Object Name | ForEach-Object { $_.FullName })
  $docs += (Join-Path $repo 'docs\wireframes\README.md')
  if ($Only) { $docs = $docs | Where-Object { $_ -like "*$Only*" } }

  $sha = [System.Security.Cryptography.SHA256]::Create()
  function HashDoc($path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $text = [System.IO.File]::ReadAllText($path)
    $dir = Split-Path -Parent $path
    foreach ($m in [regex]::Matches($text, '!\[[^\]]*\]\(([^)]+)\)')) {
      $img = Join-Path $dir $m.Groups[1].Value
      if (Test-Path $img) { $bytes += [System.IO.File]::ReadAllBytes($img) }
    }
    return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '')
  }

  $one = New-Object -ComObject OneNote.Application
  $ns = 'http://schemas.microsoft.com/office/onenote/2013/onenote'
  function Pages() {
    $s = ''; $one.GetHierarchy($sectionId, 4, [ref]$s); [xml]$h = $s
    $nsm = New-Object System.Xml.XmlNamespaceManager($h.NameTable); $nsm.AddNamespace('one', $ns)
    return $h.SelectNodes('//one:Page', $nsm)
  }

  $changed = 0
  foreach ($doc in $docs) {
    $rel = $doc.Substring($repo.Length + 1)
    $hash = HashDoc $doc
    if (-not $Force -and $manifest[$rel] -eq $hash) { continue }

    $title = (Get-Content $doc -TotalCount 1).TrimStart('#').Trim()
    Log "mirroring $rel -> '$title'"

    # Render mermaid blocks to PNG, in order.
    $text = [System.IO.File]::ReadAllText($doc)
    $blocks = [regex]::Matches($text, '```mermaid\r?\n(.*?)```', 'Singleline')
    $pngs = @()
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($doc)
    for ($i = 0; $i -lt $blocks.Count; $i++) {
      $mmd = Join-Path $render ("{0}-{1:D2}.mmd" -f $stem, $i)
      $png = Join-Path $render ("{0}-{1:D2}.png" -f $stem, $i)
      [System.IO.File]::WriteAllText($mmd, $blocks[$i].Groups[1].Value, (New-Object System.Text.UTF8Encoding($false)))
      & $mmdc --yes @mermaid-js/mermaid-cli -i $mmd -o $png -w 1600 -b white 2>&1 | Out-Null
      if (-not (Test-Path $png)) { throw "mermaid block $i of $rel failed to render" }
      $pngs += $png
    }

    # Replace the page: delete any page with the same title, then create and fill.
    foreach ($p in @(Pages | Where-Object { $_.name -eq $title })) { $one.DeleteHierarchy($p.ID, [DateTime]::MinValue, $false) }
    $pageId = ''
    $one.CreateNewPage($sectionId, [ref]$pageId, 0)
    $xmlPath = Join-Path $render ("{0}.xml" -f $stem)
    & python (Join-Path $PSScriptRoot 'md2onenote.py') $pageId $xmlPath $doc @pngs | Out-Null
    $xml = [System.IO.File]::ReadAllText($xmlPath, [System.Text.Encoding]::UTF8)
    $one.UpdatePageContent($xml, [DateTime]::MinValue)
    $chk = ''; $one.GetPageContent($pageId, [ref]$chk, 0)
    $imgs = ([regex]::Matches($chk, '<one:Image')).Count; $tbls = ([regex]::Matches($chk, '<one:Table')).Count
    Log "  stored: images=$imgs tables=$tbls"

    $manifest[$rel] = $hash
    $changed++
    ($manifest | ConvertTo-Json) | Set-Content -Path $manifestPath -Encoding UTF8
  }
  Log "done: $changed page(s) rebuilt, $($docs.Count) document(s) checked"
}
catch { Log "ERROR: $($_.Exception.Message)"; exit 1 }
finally { $mutex.ReleaseMutex() | Out-Null }
