param(
    [Parameter(Mandatory=$true)]
    [string]$ModsRoot
)

$ErrorActionPreference = "SilentlyContinue"

function Get-AssemblyMajor([string]$Path) {
    try {
        return [System.Reflection.AssemblyName]::GetAssemblyName($Path).Version.Major
    } catch {
        return -1
    }
}

$candidates = New-Object System.Collections.Generic.List[object]

# Prefer the actual Harmony mod identified by its top-level packageId.
Get-ChildItem -LiteralPath $ModsRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    $modDir = $_.FullName
    $about = Join-Path $modDir "About\About.xml"

    if (Test-Path -LiteralPath $about) {
        try {
            [xml]$xml = Get-Content -LiteralPath $about -Raw
            $pkg = [string]$xml.ModMetaData.packageId

            if ($pkg -and $pkg.Trim().ToLowerInvariant() -eq "brrainz.harmony") {
                Get-ChildItem -LiteralPath $modDir -Recurse -File -Filter "0Harmony.dll" -ErrorAction SilentlyContinue |
                    ForEach-Object {
                        $major = Get-AssemblyMajor $_.FullName
                        if ($major -ge 2) {
                            $score = 50
                            if ($_.FullName -match "\\Current\\") { $score = 0 }
                            elseif ($_.FullName -match "\\1\.6\\") { $score = 1 }
                            elseif ($_.FullName -match "\\Assemblies\\") { $score = 2 }

                            $candidates.Add([pscustomobject]@{
                                Path = $_.FullName
                                Score = $score
                                Modified = $_.LastWriteTimeUtc
                            })
                        }
                    }
            }
        } catch {
        }
    }
}

# Common Workshop-ID folder used by mod managers.
$workshopHarmony = Join-Path $ModsRoot "2009463077"
if (Test-Path -LiteralPath $workshopHarmony) {
    Get-ChildItem -LiteralPath $workshopHarmony -Recurse -File -Filter "0Harmony.dll" -ErrorAction SilentlyContinue |
        ForEach-Object {
            $major = Get-AssemblyMajor $_.FullName
            if ($major -ge 2) {
                $score = 60
                if ($_.FullName -match "\\Current\\") { $score = 10 }
                elseif ($_.FullName -match "\\1\.6\\") { $score = 11 }
                elseif ($_.FullName -match "\\Assemblies\\") { $score = 12 }

                $candidates.Add([pscustomobject]@{
                    Path = $_.FullName
                    Score = $score
                    Modified = $_.LastWriteTimeUtc
                })
            }
        }
}

# Last resort: any Harmony 2.x DLL under Mods. Old Harmony 1.x copies
# bundled with legacy mods are deliberately rejected.
if ($candidates.Count -eq 0) {
    Get-ChildItem -LiteralPath $ModsRoot -Recurse -File -Filter "0Harmony.dll" -ErrorAction SilentlyContinue |
        ForEach-Object {
            $major = Get-AssemblyMajor $_.FullName
            if ($major -ge 2) {
                $score = 100
                if ($_.FullName -match "\\Current\\") { $score = 80 }
                elseif ($_.FullName -match "\\1\.6\\") { $score = 81 }

                $candidates.Add([pscustomobject]@{
                    Path = $_.FullName
                    Score = $score
                    Modified = $_.LastWriteTimeUtc
                })
            }
        }
}

$chosen = $candidates |
    Sort-Object Score, @{Expression="Modified";Descending=$true} |
    Select-Object -First 1

if ($null -eq $chosen) {
    exit 2
}

# stdout must contain ONLY the DLL path because the BAT captures it.
Write-Output $chosen.Path
exit 0
