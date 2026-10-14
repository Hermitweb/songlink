$ErrorActionPreference = 'Stop'

function Add-Bom($path) {
    $orig = [System.IO.File]::ReadAllBytes($path)
    if ($orig.Length -ge 3 -and $orig[0] -eq 0xEF -and $orig[1] -eq 0xBB -and $orig[2] -eq 0xBF) {
        Write-Host "  BOM already present: $path"
        return
    }
    $new = New-Object byte[] ($orig.Length + 3)
    $new[0] = 0xEF; $new[1] = 0xBB; $new[2] = 0xBF
    [Array]::Copy($orig, 0, $new, 3, $orig.Length)
    [System.IO.File]::WriteAllBytes($path, $new)
    Write-Host "  BOM added: $path"
}

$here = $PSScriptRoot
Add-Bom (Join-Path $here 'install.ps1')
Add-Bom (Join-Path $here 'uninstall.ps1')
Write-Host 'Done.'