using System.Globalization;
using CdBurner.Core.Archival.Rs03;
using CdBurner.Core.Primitives;

namespace CdBurner.Cli;

public static partial class CliCommandRunner
{
    private static async Task<int> HandleEccRs03Async(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        string verb = cmd.Positionals.Count > 0 ? cmd.Positionals[0].ToLowerInvariant() : string.Empty;
        return verb switch
        {
            "create" => await HandleEccRs03CreateAsync(cmd, stdout).ConfigureAwait(false),
            "verify" => await HandleEccRs03VerifyAsync(cmd, stdout, stderr).ConfigureAwait(false),
            "repair" => await HandleEccRs03RepairAsync(cmd, stdout, stderr).ConfigureAwait(false),
            _ => throw new CommandLineException("Usage: ecc-rs03 (create | verify | repair) --image <file.iso> [options] [--json]")
        };
    }

    private static async Task<int> HandleEccRs03CreateAsync(CommandLineArgs cmd, TextWriter stdout)
    {
        string imagePath = cmd.GetRequiredOption("image", "--image <file.iso>");
        string modeRaw = (cmd.GetOption("mode") ?? "ecc").ToLowerInvariant();
        Rs03Mode mode = modeRaw switch
        {
            "ecc" or "file" => Rs03Mode.EccFile,
            "augmented" or "image" => Rs03Mode.AugmentedImage,
            _ => throw new CommandLineException($"Invalid --mode '{modeRaw}'. Supported: ecc (default), augmented.")
        };

        string? eccPath = cmd.GetOption("ecc") ?? cmd.GetOption("out");
        if (mode == Rs03Mode.AugmentedImage && !string.IsNullOrWhiteSpace(eccPath))
        {
            throw new CommandLineException("--ecc/--out cannot be combined with --mode augmented (parity is appended into the image).");
        }

        int? parityRoots = null;
        double? redundancyPercent = null;
        string? rootsOpt = cmd.GetOption("roots");
        string? redundancyOpt = cmd.GetOption("redundancy");

        if (!string.IsNullOrWhiteSpace(rootsOpt))
        {
            if (!int.TryParse(rootsOpt, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ||
                r < Rs03Constants.MinRoots || r > Rs03Constants.MaxRoots)
            {
                throw new CommandLineException($"Invalid --roots '{rootsOpt}'. Expected {Rs03Constants.MinRoots} to {Rs03Constants.MaxRoots}.");
            }
            parityRoots = r;
        }
        else if (!string.IsNullOrWhiteSpace(redundancyOpt))
        {
            string trimmed = redundancyOpt.Trim().ToLowerInvariant();
            if (trimmed == "normal")
            {
                parityRoots = Rs03Constants.DefaultNormalRoots;
            }
            else if (trimmed == "high")
            {
                parityRoots = Rs03Constants.DefaultHighRoots;
            }
            else if (trimmed.EndsWith("r", StringComparison.Ordinal) &&
                     int.TryParse(trimmed[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rootsVal))
            {
                if (rootsVal < Rs03Constants.MinRoots || rootsVal > Rs03Constants.MaxRoots)
                {
                    throw new CommandLineException($"Invalid --redundancy '{redundancyOpt}'. Roots must be {Rs03Constants.MinRoots}..{Rs03Constants.MaxRoots}.");
                }
                parityRoots = rootsVal;
            }
            else
            {
                string pctStr = trimmed.EndsWith("%", StringComparison.Ordinal) ? trimmed[..^1] : trimmed;
                if (!double.TryParse(pctStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct) ||
                    pct < 3.2 || pct > 200.0)
                {
                    throw new CommandLineException($"Invalid --redundancy '{redundancyOpt}'. Expected normal, high, 3.2..200%, or 8..170r.");
                }
                redundancyPercent = pct;
            }
        }

        DiscMediaType? mediaType = null;
        if (cmd.GetOption("media") is { } mediaOpt)
        {
            mediaType = mediaOpt.ToLowerInvariant() switch
            {
                "cd" => DiscMediaType.CdR,
                "cd-rw" => DiscMediaType.CdRw,
                "dvd" => DiscMediaType.DvdPlusR,
                "dvd+rw" => DiscMediaType.DvdPlusRw,
                "dvd-rw" => DiscMediaType.DvdMinusRw,
                "dvd-ram" => DiscMediaType.DvdRam,
                "dvd-dl" => DiscMediaType.DvdMinusRDualLayer,
                "bd" => DiscMediaType.BdR,
                "bd-re" => DiscMediaType.BdRe,
                "bd-dl" => DiscMediaType.BdRDualLayer,
                "bdxl" => DiscMediaType.BdxlRTripleLayer,
                "bdxl-ql" => DiscMediaType.BdxlRQuadLayer,
                _ => throw new CommandLineException($"Invalid --media '{mediaOpt}'.")
            };
        }

        long? mediumSectors = null;
        if (cmd.GetOption("medium-sectors") is { } secOpt)
        {
            if (!long.TryParse(secOpt, NumberStyles.Integer, CultureInfo.InvariantCulture, out long secVal) ||
                secVal < Rs03Constants.FieldMax)
            {
                throw new CommandLineException($"Invalid --medium-sectors '{secOpt}'. Must be >= {Rs03Constants.FieldMax}.");
            }
            mediumSectors = secVal;
        }

        var options = new Rs03CreateOptions
        {
            Mode = mode,
            EccFilePath = eccPath,
            ParityRoots = parityRoots,
            RedundancyPercent = redundancyPercent,
            MediaType = mediaType,
            MediumCapacitySectors = mediumSectors,
            NoBdrDefectManagement = cmd.HasFlag("no-defect-management"),
            AllowRewritableAugmented = cmd.HasFlag("allow-rewritable"),
            Overwrite = cmd.HasFlag("overwrite")
        };

        var result = await Rs03Codec.CreateAsync(imagePath, options).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.Success,
                mode = result.Mode.ToString(),
                image = Path.GetFullPath(result.ImagePath),
                eccFile = result.EccFilePath != null ? Path.GetFullPath(result.EccFilePath) : null,
                dataSectors = result.Layout.DataSectors,
                sectorsPerLayer = result.Layout.SectorsPerLayer,
                totalVirtualSectors = result.Layout.TotalVirtualSectors,
                dataLayers = result.Layout.DataLayerCount,
                parityRoots = result.Layout.ParityRootCount,
                redundancyPercent = Math.Round(result.Layout.RedundancyPercent, 2),
                warning = result.Warning,
                summary = result.Summary
            });
            return 0;
        }

        stdout.WriteLine(result.Summary);
        if (result.EccFilePath != null)
        {
            stdout.WriteLine($"  ECC file: {result.EccFilePath}");
        }
        if (result.Warning != null)
        {
            stdout.WriteLine($"  Warning:  {result.Warning}");
        }
        return 0;
    }

    private static async Task<int> HandleEccRs03VerifyAsync(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        string imagePath = cmd.GetRequiredOption("image", "--image <file.iso>");
        string? eccPath = ResolveOptionalEccPath(cmd, imagePath);
        var options = new Rs03VerifyOptions { QuickHeaderOnly = cmd.HasFlag("quick") };

        var result = await Rs03Codec.VerifyAsync(imagePath, eccPath, options).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.IsValid,
                isValid = result.IsValid,
                isRecoverable = result.IsRecoverable,
                mode = result.Mode.ToString(),
                image = Path.GetFullPath(imagePath),
                eccFile = eccPath != null ? Path.GetFullPath(eccPath) : null,
                headerReconstructed = result.HeaderReconstructed,
                fingerprintStatus = result.FingerprintStatus.ToString(),
                dataMd5Checked = result.DataMd5Checked,
                dataMd5Matches = result.DataMd5Matches,
                missingDataSectors = result.MissingDataSectors,
                crcErrorSectors = result.CrcErrorSectors,
                missingCrcSectors = result.MissingCrcSectors,
                crcSignatureErrors = result.CrcSignatureErrors,
                missingEccSectors = result.MissingEccSectors,
                syndromeErrorColumns = result.SyndromeErrorColumns,
                maxErasuresInAnyColumn = result.MaxErasuresInAnyColumn,
                dataSectors = result.Layout.DataSectors,
                sectorsPerLayer = result.Layout.SectorsPerLayer,
                parityRoots = result.Layout.ParityRootCount,
                summary = result.Summary
            });
            return result.IsValid ? 0 : 2;
        }

        TextWriter target = result.IsValid ? stdout : stderr;
        target.WriteLine(result.Summary);
        target.WriteLine($"  Mode: {result.Mode}, roots={result.Layout.ParityRootCount}, dataSectors={result.Layout.DataSectors:N0}, fingerprint={result.FingerprintStatus}");
        return result.IsValid ? 0 : 2;
    }

    private static async Task<int> HandleEccRs03RepairAsync(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        string imagePath = cmd.GetRequiredOption("image", "--image <file.iso>");
        string? eccPath = ResolveOptionalEccPath(cmd, imagePath);
        var options = new Rs03RepairOptions
        {
            AllowFingerprintMismatch = cmd.HasFlag("allow-fingerprint-mismatch"),
            VerifyAfterRepair = true
        };

        var result = await Rs03Codec.RepairAsync(imagePath, eccPath, options).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.Success,
                mode = result.Mode.ToString(),
                image = Path.GetFullPath(imagePath),
                eccFile = eccPath != null ? Path.GetFullPath(eccPath) : null,
                headerReconstructed = result.HeaderReconstructed,
                repairedDataSectors = result.RepairedDataSectors,
                repairedCrcSectors = result.RepairedCrcSectors,
                repairedEccSectors = result.RepairedEccSectors,
                unrepairedColumns = result.UnrepairedColumns,
                unrepairedSectors = result.UnrepairedSectors,
                verifiedAfterRepair = result.VerifiedAfterRepair,
                summary = result.Summary
            });
            return result.Success ? 0 : 2;
        }

        TextWriter target = result.Success ? stdout : stderr;
        target.WriteLine(result.Summary);
        return result.Success ? 0 : 2;
    }

    private static string? ResolveOptionalEccPath(CommandLineArgs cmd, string imagePath)
    {
        if (cmd.GetOption("ecc") is { } explicitEcc)
        {
            return explicitEcc;
        }

        if (File.Exists(imagePath + ".ecc"))
        {
            return imagePath + ".ecc";
        }
        string alt = Path.ChangeExtension(imagePath, ".ecc");
        return File.Exists(alt) && !Rs03Codec.TryInspectAugmentedImage(imagePath, out _, out _) ? alt : null;
    }
}
