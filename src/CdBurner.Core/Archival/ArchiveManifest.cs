using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CdBurner.Core.Archival;

/// <summary>
/// Writes and verifies cryptographic checksum manifests and index metadata for archival discs.
/// </summary>
public static class ArchiveManifest
{
    /// <summary>
    /// Standard filename for the SHA-256 checksum manifest.
    /// </summary>
    public const string ManifestFileName = "IGNISDISC-MANIFEST.sha256";

    /// <summary>
    /// Standard filename for the JSON index metadata.
    /// </summary>
    public const string IndexFileName = "IGNISDISC-ARCHIVE.json";

    /// <summary>
    /// Subfolder name where PAR2 recovery files are placed.
    /// </summary>
    public const string RecoveryFolderName = "RECOVERY";

    /// <summary>
    /// Standalone zero-dependency PowerShell verification script placed on every staged archive disc.
    /// </summary>
    public const string VerifyPowerShellFileName = "VERIFY.PS1";

    /// <summary>
    /// Standalone Windows batch wrapper for <see cref="VerifyPowerShellFileName"/> placed on every staged archive disc.
    /// </summary>
    public const string VerifyBatchFileName = "VERIFY.BAT";

    /// <summary>
    /// Human-readable long-term archival verification and PAR2 repair guide placed on every staged archive disc.
    /// </summary>
    public const string RecoveryReadmeFileName = "README-RECOVERY.TXT";

    /// <summary>
    /// Volume label an archive disc is burned with: "ARC", the first six characters of the set ID and the two-digit
    /// disc number (ARC1A2B3C_01), upper case. 12 characters, well inside the 16 a Joliet volume label allows.
    /// </summary>
    public static string VolumeLabel(string? setId, int discNumber)
    {
        string id = string.IsNullOrWhiteSpace(setId) ? "ARCHIV" : setId.Trim();
        if (id.Length > 6)
        {
            id = id[..6];
        }
        return $"ARC{id}_{discNumber:D2}".ToUpperInvariant();
    }

    /// <summary>
    /// Opens a file for hashing with a 1 MB sequential read buffer. The default 4 KB buffer turns a large file into
    /// hundreds of thousands of small reads, which is slow on optical drives (3.4 MB/s measured on a DVD).
    /// </summary>
    private static FileStream OpenForHashing(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);

    /// <summary>
    /// Resolves a manifest-relative path ('/' separators) under <paramref name="root"/>, or returns null if it is
    /// rooted, a UNC/device path, or escapes the root through "..".
    /// </summary>
    internal static string? ResolveInsideRoot(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.StartsWith('\\') || relativePath.StartsWith('/') || relativePath.Contains(':') ||
            relativePath.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }

        string fullRoot = Path.GetFullPath(root);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullRoot += Path.DirectorySeparatorChar;
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>
    /// Maximum size accepted for manifest and index metadata files (64 MiB).
    /// </summary>
    public const long MaxMetadataFileSizeBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Parses one manifest line in sha256sum format: 64 hex digits, a space, then either a second space (text
    /// mode) or '*' (binary mode), then the path. Supports GNU coreutils backslash-escaped lines starting with
    /// '\'. Returns false for anything else, including lines damaged on the disc.
    /// </summary>
    public static bool TryParseManifestLine(string line, out string sha256Hex, out string relativePath)
    {
        sha256Hex = string.Empty;
        relativePath = string.Empty;

        const int HashLength = 64;
        if (line is null)
        {
            return false;
        }

        bool escaped = line.StartsWith('\\');
        ReadOnlySpan<char> effective = escaped ? line.AsSpan(1) : line.AsSpan();
        if (effective.Length < HashLength + 2 || effective[HashLength] != ' ')
        {
            return false;
        }

        for (int i = 0; i < HashLength; i++)
        {
            if (!char.IsAsciiHexDigit(effective[i]))
            {
                return false;
            }
        }

        int pathStart = HashLength + 1;
        if (effective.Length > pathStart + 1 && (effective[pathStart] == ' ' || effective[pathStart] == '*'))
        {
            pathStart++;
        }

        sha256Hex = new string(effective[..HashLength]);
        ReadOnlySpan<char> rawPath = effective[pathStart..];
        if (!escaped)
        {
            relativePath = new string(rawPath);
            return true;
        }

        var sb = new StringBuilder(rawPath.Length);
        for (int i = 0; i < rawPath.Length; i++)
        {
            if (rawPath[i] == '\\' && i + 1 < rawPath.Length)
            {
                i++;
                sb.Append(rawPath[i] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    _ => rawPath[i]
                });
            }
            else
            {
                sb.Append(rawPath[i]);
            }
        }
        relativePath = sb.ToString();
        return true;
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// Computes SHA-256 digests for all files on the staged disc and writes manifest and index files.
    /// </summary>
    /// <param name="discRoot">Local directory containing the staged disc files.</param>
    /// <param name="plan">The overall archive plan.</param>
    /// <param name="disc">The disc plan for this specific disc.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="catalog">
    /// The whole set's file list. When given, CATALOG.JSON and INDEX.HTML are written to the disc first and are
    /// listed in the manifest and the index like any other file, so the cross-check covers them too.
    /// </param>
    /// <returns>The generated <see cref="ArchiveIndex"/> metadata.</returns>
    public static async Task<ArchiveIndex> WriteAsync(
        string discRoot,
        ArchivePlan plan,
        ArchiveDiscPlan disc,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ArchiveCatalogEntry>? catalog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discRoot);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(disc);

        var listed = new List<(string RelativePath, long SizeBytes)>(disc.Files.Count + 2);
        foreach (var file in disc.Files)
        {
            listed.Add((file.RelativePath, file.SizeBytes));
        }

        if (catalog != null)
        {
            await ArchiveCatalog.WriteAsync(discRoot, plan, disc.DiscNumber, catalog, cancellationToken).ConfigureAwait(false);
            foreach (string name in new[] { ArchiveCatalog.CatalogFileName, ArchiveCatalog.HtmlIndexFileName })
            {
                listed.Add((name, new FileInfo(Path.Combine(discRoot, name)).Length));
            }
        }

        var entries = new List<ArchiveManifestEntry>(listed.Count);
        var manifestBuilder = new StringBuilder();

        foreach (var file in listed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string normalizedRelative = file.RelativePath.Replace('\\', '/');
            string diskPath = Path.Combine(discRoot, normalizedRelative.Replace('/', Path.DirectorySeparatorChar));

            string hashHex;
            using (var stream = OpenForHashing(diskPath))
            {
                byte[] hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                hashHex = Convert.ToHexStringLower(hashBytes);
            }

            var entry = new ArchiveManifestEntry(normalizedRelative, file.SizeBytes, hashHex);
            entries.Add(entry);

            bool needsEscape = normalizedRelative.IndexOfAny(['\\', '\n', '\r']) >= 0;
            if (needsEscape)
            {
                manifestBuilder.Append('\\');
            }
            manifestBuilder.Append(hashHex);
            manifestBuilder.Append("  ");
            if (!needsEscape)
            {
                manifestBuilder.Append(normalizedRelative);
            }
            else
            {
                foreach (char c in normalizedRelative)
                {
                    switch (c)
                    {
                        case '\\': manifestBuilder.Append("\\\\"); break;
                        case '\n': manifestBuilder.Append("\\n"); break;
                        case '\r': manifestBuilder.Append("\\r"); break;
                        default: manifestBuilder.Append(c); break;
                    }
                }
            }
            manifestBuilder.Append('\n');
        }

        string manifestPath = Path.Combine(discRoot, ManifestFileName);
        await File.WriteAllTextAsync(manifestPath, manifestBuilder.ToString(), Utf8NoBom, cancellationToken).ConfigureAwait(false);

        var index = new ArchiveIndex(
            SetId: plan.SetId,
            DiscNumber: disc.DiscNumber,
            DiscCount: plan.TotalDiscCount > 0 ? plan.TotalDiscCount : plan.Discs.Count,
            MediaType: plan.MediaType.ToString(),
            CreatedUtc: DateTime.UtcNow,
            RedundancyPercent: plan.RedundancyPercent,
            Files: entries);

        string json = JsonSerializer.Serialize(index, JsonOptions);
        string indexPath = Path.Combine(discRoot, IndexFileName);
        await File.WriteAllTextAsync(indexPath, json, Utf8NoBom, cancellationToken).ConfigureAwait(false);

        await WriteStandaloneVerificationScriptsAsync(discRoot, plan, disc.DiscNumber, readmeOverride: null, cancellationToken).ConfigureAwait(false);

        return index;
    }

    /// <summary>
    /// Writes <c>PARITY-SET.JSON</c>, <c>CATALOG.JSON</c>, <c>INDEX.HTML</c>, <c>IGNISDISC-MANIFEST.sha256</c>,
    /// <c>IGNISDISC-ARCHIVE.json</c>, verification scripts, and cross-disc recovery instructions on a parity disc.
    /// </summary>
    public static async Task<ArchiveIndex> WriteParityDiscAsync(
        string discRoot,
        ArchivePlan plan,
        ParitySetDocument paritySet,
        IReadOnlyList<ArchiveCatalogEntry> catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discRoot);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(paritySet);
        ArgumentNullException.ThrowIfNull(catalog);

        await ArchiveParitySet.WriteParitySetJsonAsync(discRoot, paritySet, cancellationToken).ConfigureAwait(false);
        await ArchiveCatalog.WriteAsync(discRoot, plan, paritySet.ThisDiscNumber, catalog, cancellationToken).ConfigureAwait(false);

        var listed = new List<(string RelativePath, long SizeBytes)>(paritySet.ParityPayload.Chunks.Count + 3);
        foreach (var chunk in paritySet.ParityPayload.Chunks)
        {
            string chunkPath = Path.Combine(discRoot, chunk.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            listed.Add((chunk.RelativePath, new FileInfo(chunkPath).Length));
        }
        foreach (string name in new[] { ArchiveParitySet.ParitySetFileName, ArchiveCatalog.CatalogFileName, ArchiveCatalog.HtmlIndexFileName })
        {
            listed.Add((name, new FileInfo(Path.Combine(discRoot, name)).Length));
        }

        var entries = new List<ArchiveManifestEntry>(listed.Count);
        var manifestBuilder = new StringBuilder();

        foreach (var file in listed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string normalizedRelative = file.RelativePath.Replace('\\', '/');
            string diskPath = Path.Combine(discRoot, normalizedRelative.Replace('/', Path.DirectorySeparatorChar));

            string hashHex;
            using (var stream = OpenForHashing(diskPath))
            {
                byte[] hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                hashHex = Convert.ToHexStringLower(hashBytes);
            }

            entries.Add(new ArchiveManifestEntry(normalizedRelative, file.SizeBytes, hashHex));
            AppendManifestLine(manifestBuilder, hashHex, normalizedRelative);
        }

        await File.WriteAllTextAsync(Path.Combine(discRoot, ManifestFileName), manifestBuilder.ToString(), Utf8NoBom, cancellationToken).ConfigureAwait(false);

        var index = new ArchiveIndex(
            SetId: plan.SetId,
            DiscNumber: paritySet.ThisDiscNumber,
            DiscCount: plan.TotalDiscCount,
            MediaType: plan.MediaType.ToString(),
            CreatedUtc: DateTime.UtcNow,
            RedundancyPercent: plan.RedundancyPercent,
            Files: entries);

        await File.WriteAllTextAsync(Path.Combine(discRoot, IndexFileName), JsonSerializer.Serialize(index, JsonOptions), Utf8NoBom, cancellationToken).ConfigureAwait(false);

        string readme = ArchiveParitySet.BuildParityReadmeText(paritySet);
        await WriteStandaloneVerificationScriptsAsync(discRoot, plan, paritySet.ThisDiscNumber, readme, cancellationToken).ConfigureAwait(false);

        return index;
    }

    private static void AppendManifestLine(StringBuilder manifestBuilder, string hashHex, string normalizedRelative)
    {
        bool needsEscape = normalizedRelative.IndexOfAny(['\\', '\n', '\r']) >= 0;
        if (needsEscape)
        {
            manifestBuilder.Append('\\');
        }
        manifestBuilder.Append(hashHex);
        manifestBuilder.Append("  ");
        if (!needsEscape)
        {
            manifestBuilder.Append(normalizedRelative);
        }
        else
        {
            foreach (char c in normalizedRelative)
            {
                switch (c)
                {
                    case '\\': manifestBuilder.Append("\\\\"); break;
                    case '\n': manifestBuilder.Append("\\n"); break;
                    case '\r': manifestBuilder.Append("\\r"); break;
                    default: manifestBuilder.Append(c); break;
                }
            }
        }
        manifestBuilder.Append('\n');
    }

    private static async Task WriteStandaloneVerificationScriptsAsync(
        string discRoot,
        ArchivePlan plan,
        int discNumber,
        string? readmeOverride,
        CancellationToken cancellationToken)
    {
        string ps1Path = Path.Combine(discRoot, VerifyPowerShellFileName);
        if (!File.Exists(ps1Path))
        {
            // The script hashes with .NET directly, not Get-FileHash: Windows PowerShell cannot find Get-FileHash
            // when it is started from a PowerShell 7 session, because it inherits that session's module path.
            // A file that cannot be read (a damaged disc) is reported and the remaining files are still checked.
            const string ps1Content = """
                $ErrorActionPreference = 'Stop'
                $manifest = Join-Path $PSScriptRoot 'IGNISDISC-MANIFEST.sha256'
                if (-not (Test-Path -LiteralPath $manifest)) {
                    Write-Error "Manifest file not found: $manifest"
                    exit 2
                }
                $rootFull = [IO.Path]::GetFullPath($PSScriptRoot)
                if (-not $rootFull.EndsWith([IO.Path]::DirectorySeparatorChar)) {
                    $rootFull += [IO.Path]::DirectorySeparatorChar
                }
                $checked = 0
                $failed = 0
                $verifiedHashes = @{}
                foreach ($line in Get-Content -LiteralPath $manifest -Encoding UTF8) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $isEscaped = $line.StartsWith('\')
                    $rawLine = if ($isEscaped) { $line.Substring(1) } else { $line }
                    if ($rawLine -notmatch '^([0-9A-Fa-f]{64}) [ *]?(.+)$') {
                        Write-Host "[UNREADABLE] a line of IGNISDISC-MANIFEST.sha256" -ForegroundColor Red
                        $failed++
                        continue
                    }
                    $expected = $Matches[1].ToLowerInvariant()
                    $listedPath = $Matches[2]
                    if ($isEscaped) {
                        $listedPath = [regex]::Replace($listedPath, '\\(.)', {
                            param($m)
                            switch ($m.Groups[1].Value) {
                                'n' { "`n" }
                                'r' { "`r" }
                                default { $m.Groups[1].Value }
                            }
                        })
                    }
                    $checked++
                    if ([IO.Path]::IsPathRooted($listedPath) -or $listedPath.Contains(':') -or $listedPath.StartsWith('\') -or $listedPath.StartsWith('/') -or $listedPath -match '(^|[\\/])\.\.([\\/]|$)') {
                        Write-Host "[REJECTED] $listedPath (points outside the disc)" -ForegroundColor Red
                        $failed++
                        continue
                    }
                    $relPath = $listedPath -replace '/', [IO.Path]::DirectorySeparatorChar
                    $fullPath = Join-Path $PSScriptRoot $relPath
                    $resolvedFull = $null
                    try { $resolvedFull = [IO.Path]::GetFullPath($fullPath) } catch { $resolvedFull = $null }
                    if ($null -eq $resolvedFull -or -not $resolvedFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
                        Write-Host "[REJECTED] $listedPath (points outside the disc)" -ForegroundColor Red
                        $failed++
                        continue
                    }
                    if (-not (Test-Path -LiteralPath $fullPath)) {
                        Write-Host "[MISSING] $relPath" -ForegroundColor Red
                        $failed++
                        continue
                    }
                    try {
                        $stream = [System.IO.File]::OpenRead($fullPath)
                        try {
                            $sha = [System.Security.Cryptography.SHA256]::Create()
                            try {
                                $actual = ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
                            } finally { $sha.Dispose() }
                        } finally { $stream.Dispose() }
                    } catch {
                        Write-Host "[UNREADABLE] $relPath" -ForegroundColor Red
                        $failed++
                        continue
                    }
                    if ($actual -ne $expected) {
                        Write-Host "[CORRUPT] $relPath" -ForegroundColor Red
                        $failed++
                    } else {
                        $verifiedHashes[$listedPath.ToLowerInvariant()] = $actual
                        Write-Host "[OK]      $relPath" -ForegroundColor Green
                    }
                }
                if ($checked -eq 0) {
                    Write-Host "[UNREADABLE] IGNISDISC-MANIFEST.sha256 lists no files" -ForegroundColor Red
                    $failed++
                }
                $index = Join-Path $PSScriptRoot 'IGNISDISC-ARCHIVE.json'
                if (Test-Path -LiteralPath $index) {
                    try {
                        $doc = Get-Content -LiteralPath $index -Raw -Encoding UTF8 | ConvertFrom-Json
                        $listed = if ($null -eq $doc.files) { 0 } else { @($doc.files).Count }
                        if ($listed -ne $checked) {
                            Write-Host "[MISMATCH] the manifest lists $checked files, IGNISDISC-ARCHIVE.json lists $listed" -ForegroundColor Red
                            $failed++
                        }
                    } catch {
                        Write-Host "[UNREADABLE] IGNISDISC-ARCHIVE.json" -ForegroundColor Red
                        $failed++
                    }
                }
                $paritySetPath = Join-Path $PSScriptRoot 'PARITY-SET.JSON'
                if (Test-Path -LiteralPath $paritySetPath) {
                    try {
                        $psDoc = Get-Content -LiteralPath $paritySetPath -Raw -Encoding UTF8 | ConvertFrom-Json
                        if ([string]::IsNullOrWhiteSpace($psDoc.setId) -or $psDoc.dataDiscCount -lt 1 -or $psDoc.parityDiscCount -lt 1 -or $null -eq $psDoc.parityPayload -or $null -eq $psDoc.parityPayload.chunks) {
                            Write-Host "[CORRUPT] PARITY-SET.JSON is missing required parity-set fields" -ForegroundColor Red
                            $failed++
                        } else {
                            $payloadOk = $true
                            foreach ($ch in @($psDoc.parityPayload.chunks)) {
                                $chRel = $ch.relativePath -replace '/', [IO.Path]::DirectorySeparatorChar
                                $chFull = Join-Path $PSScriptRoot $chRel
                                if (-not (Test-Path -LiteralPath $chFull)) {
                                    Write-Host "[MISSING] $chRel (listed in PARITY-SET.JSON)" -ForegroundColor Red
                                    $failed++
                                    $payloadOk = $false
                                    continue
                                }
                                $chLen = (Get-Item -LiteralPath $chFull).Length
                                if ($chLen -ne [int64]$ch.sizeBytes) {
                                    Write-Host "[CORRUPT] $chRel size $chLen does not match PARITY-SET.JSON ($($ch.sizeBytes))" -ForegroundColor Red
                                    $failed++
                                    $payloadOk = $false
                                    continue
                                }
                                $chKey = $ch.relativePath.ToLowerInvariant()
                                $chHash = $verifiedHashes[$chKey]
                                if ([string]::IsNullOrWhiteSpace($chHash)) {
                                    $s = [System.IO.File]::OpenRead($chFull)
                                    try {
                                        $h = [System.Security.Cryptography.SHA256]::Create()
                                        try { $chHash = ([System.BitConverter]::ToString($h.ComputeHash($s)) -replace '-', '').ToLowerInvariant() } finally { $h.Dispose() }
                                    } finally { $s.Dispose() }
                                }
                                if ($chHash -ne $ch.sha256.ToLowerInvariant()) {
                                    Write-Host "[CORRUPT] $chRel checksum does not match PARITY-SET.JSON" -ForegroundColor Red
                                    $failed++
                                    $payloadOk = $false
                                }
                            }
                            $catalogPath = Join-Path $PSScriptRoot 'CATALOG.JSON'
                            if (Test-Path -LiteralPath $catalogPath) {
                                $catDoc = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
                                if ($catDoc.setId -ne $psDoc.setId -or $null -eq $catDoc.paritySet -or $catDoc.paritySet.dataDiscCount -ne $psDoc.dataDiscCount -or $catDoc.paritySet.parityDiscCount -ne $psDoc.parityDiscCount) {
                                    Write-Host "[MISMATCH] PARITY-SET.JSON disagrees with CATALOG.JSON" -ForegroundColor Red
                                    $failed++
                                    $payloadOk = $false
                                }
                            }
                            if ($payloadOk) {
                                Write-Host "[OK]      PARITY-SET.JSON (parity disc $($psDoc.paritySlotIndex + 1) of $($psDoc.parityDiscCount), protects $($psDoc.dataDiscCount) data discs)" -ForegroundColor Green
                            }
                        }
                    } catch {
                        Write-Host "[UNREADABLE] PARITY-SET.JSON" -ForegroundColor Red
                        $failed++
                    }
                }
                Write-Host ""
                if ($failed -eq 0) {
                    Write-Host "Archive verification PASSED ($checked files intact)." -ForegroundColor Green
                    exit 0
                } else {
                    Write-Host "Archive verification FAILED ($failed problems, $checked files listed)." -ForegroundColor Red
                    exit 1
                }
                """;
            await File.WriteAllTextAsync(ps1Path, ps1Content.Replace("\r\n", "\n"), Utf8NoBom, cancellationToken).ConfigureAwait(false);
        }

        string batPath = Path.Combine(discRoot, VerifyBatchFileName);
        if (!File.Exists(batPath))
        {
            const string batContent = "@echo off\r\n" +
                "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0VERIFY.PS1\"\r\n" +
                "set EXITCODE=%ERRORLEVEL%\r\n" +
                "if not \"%1\"==\"--nopause\" pause\r\n" +
                "exit /b %EXITCODE%\r\n";
            await File.WriteAllTextAsync(batPath, batContent, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        }

        string readmePath = Path.Combine(discRoot, RecoveryReadmeFileName);
        if (!File.Exists(readmePath))
        {
            int totalDiscs = plan.TotalDiscCount > 0 ? plan.TotalDiscCount : plan.Discs.Count;
            string parityLine = plan.ParityDiscCount > 0
                ? $"\nCross-Disc:  {plan.ParityDiscCount} parity disc(s) (discs {plan.Discs.Count + 1}..{totalDiscs})"
                : string.Empty;
            string readmeContent = readmeOverride ?? $"""
                ================================================================================
                IGNISDISC ARCHIVAL DISC VERIFICATION & RECOVERY GUIDE
                Set ID:      {plan.SetId}
                Disc:        {discNumber} of {totalDiscs}
                Media Type:  {plan.MediaType}
                PAR2 Parity: {plan.RedundancyPercent}%{parityLine}
                ================================================================================

                This disc is self-describing and can be verified on any computer without
                IgnisDisc installed using standard operating system tools.

                1. ZERO-DEPENDENCY VERIFICATION ON WINDOWS
                   - Double-click VERIFY.BAT (or run .\VERIFY.PS1 in PowerShell).
                   - Or verify any individual file manually using built-in Windows tools:
                       certutil -hashfile <filename> SHA256
                     and compare the output against IGNISDISC-MANIFEST.sha256.

                2. ZERO-DEPENDENCY VERIFICATION ON LINUX / MACOS
                   - Open a terminal in the root of this disc and run:
                       sha256sum -c IGNISDISC-MANIFEST.sha256

                3. REPAIRING DAMAGED FILES WITH PAR2 (IF RECOVERY FOLDER IS PRESENT)
                   - Copy the entire contents of this disc to a writable folder on disk.
                   - Open a terminal in that writable folder and run par2cmdline:
                       par2 repair -B. RECOVERY\recovery.par2      (Windows)
                       par2 repair -B. RECOVERY/recovery.par2      (Linux / macOS)
                """.Replace("\r\n", "\n");
            await File.WriteAllTextAsync(readmePath, readmeContent, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies all files in a disc directory against its manifest and reports status.
    /// </summary>
    /// <param name="discRoot">Local directory containing the disc root.</param>
    /// <param name="progress">Optional progress reporter for status messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verification result including missing, corrupt, and recovery status.</returns>
    /// <exception cref="DirectoryNotFoundException">Thrown if discRoot does not exist.</exception>
    /// <exception cref="FileNotFoundException">Thrown if the manifest file does not exist.</exception>
    public static async Task<ArchiveVerificationResult> VerifyAsync(
        string discRoot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discRoot);

        if (!Directory.Exists(discRoot))
        {
            throw new DirectoryNotFoundException($"Disc root directory not found: '{discRoot}'.");
        }

        string manifestPath = Path.Combine(discRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Archive manifest file not found: '{manifestPath}'.", manifestPath);
        }

        var missing = new List<string>();
        var corrupt = new List<string>();

        // The disc carries the file list twice: the sha256sum manifest and the JSON index. Each is used to check
        // the other, so damage to either one cannot shrink the set of files that gets verified.
        string? setId = null;
        int? discNumber = null;
        int? discCount = null;
        IReadOnlyList<ArchiveManifestEntry>? indexFiles = null;

        string indexPath = Path.Combine(discRoot, IndexFileName);
        if (File.Exists(indexPath))
        {
            try
            {
                var indexInfo = new FileInfo(indexPath);
                if (indexInfo.Length > MaxMetadataFileSizeBytes)
                {
                    corrupt.Add($"{IndexFileName} (unreadable)");
                }
                else
                {
                    await using var indexStream = new FileStream(
                        indexPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        bufferSize: 4096, FileOptions.SequentialScan | FileOptions.Asynchronous);
                    var index = await JsonSerializer.DeserializeAsync<ArchiveIndex>(indexStream, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }, cancellationToken).ConfigureAwait(false);
                    if (index?.Files != null)
                    {
                        setId = index.SetId;
                        discNumber = index.DiscNumber;
                        discCount = index.DiscCount;
                        indexFiles = index.Files;
                    }
                    else
                    {
                        corrupt.Add($"{IndexFileName} (unreadable)");
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                corrupt.Add($"{IndexFileName} (unreadable)");
            }
        }

        string recoveryPath = Path.Combine(discRoot, RecoveryFolderName);
        bool hasRecoveryData = Directory.Exists(recoveryPath) &&
                               Directory.EnumerateFiles(recoveryPath, "*.par2").Any();

        var entries = new List<(string Hash, string RelativePath)>();
        int unreadableLines = 0;

        var manifestInfo = new FileInfo(manifestPath);
        if (manifestInfo.Length > MaxMetadataFileSizeBytes)
        {
            corrupt.Add($"{ManifestFileName} (exceeds maximum readable size)");
        }
        else
        {
            await using var manifestStream = new FileStream(
                manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);
            using var manifestReader = new StreamReader(manifestStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (await manifestReader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (TryParseManifestLine(line, out string lineHash, out string linePath))
                {
                    entries.Add((lineHash, linePath));
                }
                else
                {
                    unreadableLines++;
                }
            }
        }

        if (unreadableLines > 0)
        {
            corrupt.Add($"{ManifestFileName} ({unreadableLines} unreadable line(s))");
        }

        if (indexFiles != null)
        {
            var manifestHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (hash, path) in entries)
            {
                manifestHashes[path] = hash;
            }

            int absentFromManifest = 0;
            bool listsDisagree = false;
            foreach (var indexEntry in indexFiles)
            {
                if (indexEntry?.RelativePath is not { Length: > 0 } indexedPath || indexEntry.Sha256 is not { } indexedHash)
                {
                    listsDisagree = true;
                }
                else if (!manifestHashes.TryGetValue(indexedPath, out string? manifestHash))
                {
                    // Still verify the file, against the hash the index recorded for it.
                    entries.Add((indexedHash, indexedPath));
                    absentFromManifest++;
                }
                else if (!string.Equals(manifestHash, indexedHash, StringComparison.OrdinalIgnoreCase))
                {
                    listsDisagree = true;
                }
            }

            if (absentFromManifest > 0)
            {
                corrupt.Add($"{ManifestFileName} ({absentFromManifest} file(s) listed in {IndexFileName} are absent)");
            }

            if (listsDisagree)
            {
                corrupt.Add($"{IndexFileName} (does not match {ManifestFileName})");
            }
        }

        if (entries.Count == 0)
        {
            corrupt.Add($"{ManifestFileName} (lists no files)");
        }

        await ValidateParitySetMetadataAsync(discRoot, setId, discNumber, corrupt, cancellationToken).ConfigureAwait(false);

        int filesChecked = 0;

        foreach (var (expectedHash, relativePath) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            filesChecked++;
            progress?.Report($"Verifying {filesChecked}/{entries.Count}: {relativePath}");

            // The manifest comes from the disc and is untrusted: never open anything outside the disc root.
            string? diskFilePath = ResolveInsideRoot(discRoot, relativePath);
            if (diskFilePath == null)
            {
                corrupt.Add($"{relativePath} (rejected: path points outside the disc)");
                continue;
            }

            if (!File.Exists(diskFilePath))
            {
                missing.Add(relativePath);
                continue;
            }

            string actualHash;
            try
            {
                using var stream = OpenForHashing(diskFilePath);
                byte[] hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                actualHash = Convert.ToHexStringLower(hashBytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                corrupt.Add(relativePath);
                continue;
            }

            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                corrupt.Add(relativePath);
            }
        }

        return new ArchiveVerificationResult(
            FilesChecked: filesChecked,
            Missing: missing,
            Corrupt: corrupt,
            HasRecoveryData: hasRecoveryData,
            SetId: setId,
            DiscNumber: discNumber,
            DiscCount: discCount);
    }

    private static async Task ValidateParitySetMetadataAsync(
        string discRoot,
        string? indexSetId,
        int? indexDiscNumber,
        List<string> corrupt,
        CancellationToken cancellationToken)
    {
        string paritySetPath = Path.Combine(discRoot, ArchiveParitySet.ParitySetFileName);
        if (!File.Exists(paritySetPath))
        {
            return;
        }

        ParitySetDocument? parityDoc;
        try
        {
            var info = new FileInfo(paritySetPath);
            if (info.Length > MaxMetadataFileSizeBytes)
            {
                corrupt.Add($"{ArchiveParitySet.ParitySetFileName} (exceeds maximum readable size)");
                return;
            }

            string json = await File.ReadAllTextAsync(paritySetPath, cancellationToken).ConfigureAwait(false);
            parityDoc = ArchiveParitySet.ParseJson(json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            corrupt.Add($"{ArchiveParitySet.ParitySetFileName} (unreadable)");
            return;
        }

        if (parityDoc == null ||
            string.IsNullOrWhiteSpace(parityDoc.SetId) ||
            parityDoc.DataDiscCount < 1 ||
            parityDoc.ParityDiscCount < 1 ||
            parityDoc.ParityPayload?.Chunks == null ||
            parityDoc.ParityPayload.Chunks.Count == 0 ||
            parityDoc.DataDiscs == null ||
            parityDoc.DataDiscs.Count != parityDoc.DataDiscCount)
        {
            corrupt.Add($"{ArchiveParitySet.ParitySetFileName} (invalid parity metadata)");
            return;
        }

        if (!string.IsNullOrWhiteSpace(indexSetId) &&
            !string.Equals(parityDoc.SetId, indexSetId, StringComparison.Ordinal))
        {
            corrupt.Add($"{ArchiveParitySet.ParitySetFileName} (set ID mismatch)");
        }
        if (indexDiscNumber.HasValue && parityDoc.ThisDiscNumber != indexDiscNumber.Value)
        {
            corrupt.Add($"{ArchiveParitySet.ParitySetFileName} (disc number mismatch)");
        }

        string catalogPath = Path.Combine(discRoot, ArchiveCatalog.CatalogFileName);
        if (File.Exists(catalogPath))
        {
            try
            {
                string catJson = await File.ReadAllTextAsync(catalogPath, cancellationToken).ConfigureAwait(false);
                var catDoc = ArchiveCatalog.ParseJson(catJson);
                if (catDoc == null ||
                    !string.Equals(catDoc.SetId, parityDoc.SetId, StringComparison.Ordinal) ||
                    catDoc.ParitySet == null ||
                    catDoc.ParitySet.DataDiscCount != parityDoc.DataDiscCount ||
                    catDoc.ParitySet.ParityDiscCount != parityDoc.ParityDiscCount)
                {
                    corrupt.Add($"{ArchiveParitySet.ParitySetFileName} (does not match {ArchiveCatalog.CatalogFileName})");
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                corrupt.Add($"{ArchiveCatalog.CatalogFileName} (unreadable)");
            }
        }
    }
}
