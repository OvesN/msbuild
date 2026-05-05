#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validates that every '- template: <path>' reference in repo YAML files
    points to a file that actually exists in the repo.

.DESCRIPTION
    Walks every .yml / .yaml file under the repo root (excluding artifacts/
    and .git/) and finds template references of the form:

        - template: <path>
          template: <path>

    Path may be:
      * Absolute (starts with '/')          - resolved from repo root
      * Relative                            - resolved from the YAML file's directory
      * Suffixed with @self                 - same as no suffix (this repo)
      * Suffixed with @<other-resource>     - external repo, NOT validated (skipped)

    Exits non-zero if any reference cannot be resolved. Designed to catch
    breakages where eng/common (arcade) templates are removed or renamed
    but our own pipeline YAMLs still reference them by path.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

$yamlFiles = Get-ChildItem -Path $RepoRoot -Recurse -File -Include '*.yml', '*.yaml' |
    Where-Object {
        $rel = $_.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
        ($rel -notmatch '^(artifacts|\.git|\.dotnet|node_modules)([\\/]|$)')
    }

$pattern = '^\s*-?\s*template:\s*(?<ref>[^\s#]+)'
$problems = [System.Collections.Generic.List[object]]::new()
$checked = 0

foreach ($file in $yamlFiles) {
    $lineNo = 0
    foreach ($line in [System.IO.File]::ReadAllLines($file.FullName)) {
        $lineNo++

        # Skip comments
        if ($line -match '^\s*#') { continue }

        $m = [regex]::Match($line, $pattern)
        if (-not $m.Success) { continue }

        $ref = $m.Groups['ref'].Value.Trim().Trim('"').Trim("'")

        # Strip @resource suffix
        $resource = $null
        if ($ref -match '^(?<path>[^@]+)@(?<res>.+)$') {
            $ref = $matches['path']
            $resource = $matches['res']
        }

        # External repo references can't be validated locally
        if ($resource -and $resource -ne 'self') { continue }

        # Skip parameter-style values like "$(var)" or expression "${{ ... }}"
        if ($ref -match '^\$' -or $ref.Contains('${{')) { continue }

        # Resolve the reference
        if ($ref.StartsWith('/')) {
            $resolved = Join-Path $RepoRoot ($ref.TrimStart('/').Replace('/', [IO.Path]::DirectorySeparatorChar))
        }
        else {
            $resolved = Join-Path $file.DirectoryName ($ref.Replace('/', [IO.Path]::DirectorySeparatorChar))
        }

        $checked++

        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            $relFile = $file.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
            $problems.Add([pscustomobject]@{
                File     = $relFile
                Line     = $lineNo
                Ref      = $m.Groups['ref'].Value.Trim()
                Resolved = $resolved
            })
        }
    }
}

Write-Host "Checked $checked template reference(s) across $($yamlFiles.Count) YAML file(s)."

if ($problems.Count -gt 0) {
    Write-Host ""
    Write-Host "::error::Found $($problems.Count) broken YAML template reference(s):" -ForegroundColor Red
    foreach ($p in $problems) {
        Write-Host ""
        Write-Host "  $($p.File):$($p.Line)" -ForegroundColor Yellow
        Write-Host "    template: $($p.Ref)"
        Write-Host "    resolved: $($p.Resolved)"
        Write-Host "    -> file does not exist"
    }
    Write-Host ""
    exit 1
}

Write-Host "All template references resolved successfully." -ForegroundColor Green
exit 0
