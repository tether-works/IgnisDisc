<#
.SYNOPSIS
    Single source of truth synchronizer for the IgnisDisc Support Matrix.

.DESCRIPTION
    Reads canonical feature rows from docs/support-matrix.json and regenerates
    the support table blocks in:
      - README.md (Markdown table between <!-- SUPPORT-TABLE-START --> and <!-- SUPPORT-TABLE-END -->)
      - web/public/index.html (HTML <tbody> between <!-- SUPPORT-TABLE-START --> and <!-- SUPPORT-TABLE-END -->)
      - web/public/details.html (one block per feature between <!-- SUPPORT-DETAILS-START --> and <!-- SUPPORT-DETAILS-END -->)

    README.md and details.html carry the full notes. index.html shows a row's short "summaryHtml"
    with a "Full notes" link when it has one; notes longer than 240 characters must have one.

.PARAMETER DryRun
    Displays the changes that would be made without writing files.

.PARAMETER CheckOnly
    Checks whether README.md and web/public/index.html are in sync with docs/support-matrix.json.
    Exits with code 0 if in sync, or non-zero if out of sync.

.EXAMPLE
    pwsh scripts/Sync-SupportTable.ps1
    pwsh scripts/Sync-SupportTable.ps1 -CheckOnly
    pwsh scripts/Sync-SupportTable.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

$MatrixPath = Join-Path $RepoRoot "docs\support-matrix.json"
$ReadmePath = Join-Path $RepoRoot "README.md"
$HtmlPath = Join-Path $RepoRoot "web\public\index.html"
$DetailsPath = Join-Path $RepoRoot "web\public\details.html"

if (-not (Test-Path $DetailsPath)) {
    throw "web/public/details.html not found at $DetailsPath"
}
if (-not (Test-Path $MatrixPath)) {
    throw "Support matrix canonical data not found at $MatrixPath"
}
if (-not (Test-Path $ReadmePath)) {
    throw "README.md not found at $ReadmePath"
}
if (-not (Test-Path $HtmlPath)) {
    throw "web/public/index.html not found at $HtmlPath"
}

$matrix = Get-Content -Path $MatrixPath -Raw -Encoding UTF8 | ConvertFrom-Json

function Get-Newline([string]$text) {
    if ($text.Contains("`r`n")) { return "`r`n" }
    return "`n"
}

# ---------------------------------------------------------------------------
# 1. Generate Markdown Table for README.md
# ---------------------------------------------------------------------------
$readmeContent = Get-Content -Path $ReadmePath -Raw -Encoding UTF8
$readmeNl = Get-Newline $readmeContent

$mdLines = [System.Collections.Generic.List[string]]::new()
$mdLines.Add("| Feature | Physical drives | Virtual drive | Notes |")
$mdLines.Add("| --- | :---: | :---: | --- |")
foreach ($row in $matrix) {
    $note = if ([string]::IsNullOrEmpty($row.notesMarkdown)) { "" } else { "$($row.notesMarkdown) " }
    $mdLines.Add("| $($row.featureMarkdown) | $($row.physical) | $($row.virtual) | $note|")
}
$newMdBlock = ($mdLines -join $readmeNl)

$readmeMarkerPattern = '<!-- SUPPORT-TABLE-START -->[\s\S]*?<!-- SUPPORT-TABLE-END -->'
if ($readmeContent -notmatch $readmeMarkerPattern) {
    throw "Could not find <!-- SUPPORT-TABLE-START --> ... <!-- SUPPORT-TABLE-END --> in $ReadmePath"
}
$replacementReadme = "<!-- SUPPORT-TABLE-START -->$readmeNl$newMdBlock$readmeNl<!-- SUPPORT-TABLE-END -->"
$updatedReadme = [regex]::Replace($readmeContent, $readmeMarkerPattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacementReadme })

# ---------------------------------------------------------------------------
# 2. Generate HTML <tbody> Block for web/public/index.html
# ---------------------------------------------------------------------------
function Format-HtmlStatusBadge([string]$status, [string]$notes) {
    switch ($status) {
        "✅" { return '<span class="badge-pass">[ PASS ]</span>' }
        "⚠️" { return '<span class="badge-warn">[ PARTIAL ]</span>' }
        "❌" {
            if ($notes -match 'IMAPIv2') {
                return '<span class="badge-na">[ N/A - IMAPIv2 ]</span>'
            }
            return '<span class="badge-na">[ N/A ]</span>'
        }
        default { return '<span class="badge-na">[ N/A ]</span>' }
    }
}

# Anchor id for a row on the details page, e.g. "Burn an existing `.iso`" -> "burn-an-existing-iso".
function Get-RowSlug([string]$featureMarkdown) {
    $slug = ($featureMarkdown.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
    if ([string]::IsNullOrEmpty($slug)) { throw "Feature name '$featureMarkdown' gives an empty anchor id" }
    return $slug
}

# The main page shows a short summary; long notes must have one so the table stays readable.
$MaxInlineNoteLength = 240
$seenSlugs = @{}
foreach ($row in $matrix) {
    $slug = Get-RowSlug $row.featureMarkdown
    if ($seenSlugs.ContainsKey($slug)) { throw "Two rows give the same anchor id '$slug'" }
    $seenSlugs[$slug] = $true
    $hasSummary = -not [string]::IsNullOrWhiteSpace($row.summaryHtml)
    if (-not $hasSummary -and $row.notesHtml.Length -gt $MaxInlineNoteLength) {
        throw "Row '$($row.featureMarkdown)' has $($row.notesHtml.Length) characters of notes; add a short summaryHtml (a few sentences) to docs/support-matrix.json"
    }
}

$htmlContent = Get-Content -Path $HtmlPath -Raw -Encoding UTF8
$htmlNl = Get-Newline $htmlContent

$htmlLines = [System.Collections.Generic.List[string]]::new()
$htmlLines.Add("              <tbody>")
foreach ($row in $matrix) {
    $physBadge = Format-HtmlStatusBadge $row.physical $row.notesHtml
    $virtBadge = Format-HtmlStatusBadge $row.virtual $row.notesHtml
    $slug = Get-RowSlug $row.featureMarkdown
    $cellNote = if ([string]::IsNullOrWhiteSpace($row.summaryHtml)) {
        $row.notesHtml
    } else {
        "$($row.summaryHtml) <a class=`"details-link`" href=`"/details#$slug`">Full notes</a>"
    }
    $htmlLines.Add("                <tr><td>$($row.featureHtml)</td><td>$physBadge</td><td>$virtBadge</td><td>$cellNote</td></tr>")
}
$htmlLines.Add("              </tbody>")
$newHtmlBlock = ($htmlLines -join $htmlNl)

$htmlMarkerPattern = '<!-- SUPPORT-TABLE-START -->[\s\S]*?<!-- SUPPORT-TABLE-END -->'
if ($htmlContent -notmatch $htmlMarkerPattern) {
    throw "Could not find <!-- SUPPORT-TABLE-START --> ... <!-- SUPPORT-TABLE-END --> in $HtmlPath"
}
$replacementHtml = "<!-- SUPPORT-TABLE-START -->$htmlNl$newHtmlBlock$htmlNl              <!-- SUPPORT-TABLE-END -->"
$updatedHtml = [regex]::Replace($htmlContent, $htmlMarkerPattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacementHtml })

# ---------------------------------------------------------------------------
# 2b. Generate the full-notes blocks for web/public/details.html
# ---------------------------------------------------------------------------
$detailsContent = Get-Content -Path $DetailsPath -Raw -Encoding UTF8
$detailsNl = Get-Newline $detailsContent

$detailLines = [System.Collections.Generic.List[string]]::new()
foreach ($row in $matrix) {
    $slug = Get-RowSlug $row.featureMarkdown
    $physBadge = Format-HtmlStatusBadge $row.physical $row.notesHtml
    $virtBadge = Format-HtmlStatusBadge $row.virtual $row.notesHtml
    $detailLines.Add("        <div class=`"cell`" id=`"$slug`">")
    $detailLines.Add("          <h3 class=`"spec-module-title`">$($row.featureHtml)</h3>")
    $detailLines.Add("          <p class=`"feature-detail-status`">PHYSICAL $physBadge · VIRTUAL $virtBadge</p>")
    if ([string]::IsNullOrWhiteSpace($row.notesHtml)) {
        $detailLines.Add("          <p class=`"feature-detail-notes`">No further notes.</p>")
    } else {
        # One bullet per sentence: a sentence ends at ". " followed by a capital letter or a tag (e.g. "<code>").
        $sentences = @([regex]::Split($row.notesHtml.Trim(), '(?<=\.)\s+(?=[A-Z<])') | Where-Object { $_ -ne '' })
        if ($sentences.Count -lt 2) {
            $detailLines.Add("          <p class=`"feature-detail-notes`">$($row.notesHtml)</p>")
        } else {
            $detailLines.Add("          <ul class=`"spec-module-list feature-detail-notes`">")
            foreach ($sentence in $sentences) { $detailLines.Add("            <li>$sentence</li>") }
            $detailLines.Add("          </ul>")
        }
    }
    $detailLines.Add("        </div>")
}
$newDetailsBlock = ($detailLines -join $detailsNl)

$detailsMarkerPattern = '<!-- SUPPORT-DETAILS-START -->[\s\S]*?<!-- SUPPORT-DETAILS-END -->'
if ($detailsContent -notmatch $detailsMarkerPattern) {
    throw "Could not find <!-- SUPPORT-DETAILS-START --> ... <!-- SUPPORT-DETAILS-END --> in $DetailsPath"
}
$replacementDetails = "<!-- SUPPORT-DETAILS-START -->$detailsNl$newDetailsBlock$detailsNl        <!-- SUPPORT-DETAILS-END -->"
$updatedDetails = [regex]::Replace($detailsContent, $detailsMarkerPattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacementDetails })

# ---------------------------------------------------------------------------
# 3. Apply / Verify
# ---------------------------------------------------------------------------
$readmeDiffers = ($updatedReadme -ne $readmeContent)
$htmlDiffers = ($updatedHtml -ne $htmlContent)
$detailsDiffers = ($updatedDetails -ne $detailsContent)

if ($CheckOnly) {
    if ($readmeDiffers -or $htmlDiffers -or $detailsDiffers) {
        # Write-Host, not Write-Error: with $ErrorActionPreference = Stop the first Write-Error would end the script
        # before the second file is reported.
        if ($readmeDiffers) {
            Write-Host "README.md support table is out of sync with docs/support-matrix.json" -ForegroundColor Red
        }
        if ($htmlDiffers) {
            Write-Host "web/public/index.html support table is out of sync with docs/support-matrix.json" -ForegroundColor Red
        }
        if ($detailsDiffers) {
            Write-Host "web/public/details.html feature notes are out of sync with docs/support-matrix.json" -ForegroundColor Red
        }
        exit 1
    }
    Write-Host "Support tables in README.md, web/public/index.html and web/public/details.html are in sync with docs/support-matrix.json." -ForegroundColor Green
    return
}

if ($DryRun) {
    if ($readmeDiffers) {
        Write-Host "  [DRY-RUN] Would update support table in README.md" -ForegroundColor Yellow
    } else {
        Write-Host "  [IN SYNC] README.md support table is already up-to-date" -ForegroundColor Green
    }
    if ($htmlDiffers) {
        Write-Host "  [DRY-RUN] Would update support table in web/public/index.html" -ForegroundColor Yellow
    } else {
        Write-Host "  [IN SYNC] web/public/index.html support table is already up-to-date" -ForegroundColor Green
    }
    if ($detailsDiffers) {
        Write-Host "  [DRY-RUN] Would update feature notes in web/public/details.html" -ForegroundColor Yellow
    } else {
        Write-Host "  [IN SYNC] web/public/details.html feature notes are already up-to-date" -ForegroundColor Green
    }
    return
}

if ($readmeDiffers) {
    Set-Content -Path $ReadmePath -Value $updatedReadme -Encoding UTF8 -NoNewline
    Write-Host "  [UPDATED] $ReadmePath (Support table regenerated from docs/support-matrix.json)" -ForegroundColor Green
} else {
    Write-Host "  [IN SYNC] $ReadmePath (Already matches docs/support-matrix.json)" -ForegroundColor Green
}

if ($htmlDiffers) {
    Set-Content -Path $HtmlPath -Value $updatedHtml -Encoding UTF8 -NoNewline
    Write-Host "  [UPDATED] $HtmlPath (Support table regenerated from docs/support-matrix.json)" -ForegroundColor Green
} else {
    Write-Host "  [IN SYNC] $HtmlPath (Already matches docs/support-matrix.json)" -ForegroundColor Green
}

if ($detailsDiffers) {
    Set-Content -Path $DetailsPath -Value $updatedDetails -Encoding UTF8 -NoNewline
    Write-Host "  [UPDATED] $DetailsPath (Feature notes regenerated from docs/support-matrix.json)" -ForegroundColor Green
} else {
    Write-Host "  [IN SYNC] $DetailsPath (Already matches docs/support-matrix.json)" -ForegroundColor Green
}
