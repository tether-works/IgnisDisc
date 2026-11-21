using System.Runtime.InteropServices;

namespace CdBurner.Core.Subtitles;

/// <summary>
/// Which whisper.cpp program build is installed.
/// </summary>
public enum WhisperBuildKind
{
    /// <summary>x64 build with the NVIDIA CUDA 12.4 back end (runs on the GPU).</summary>
    CudaX64,

    /// <summary>x64 build that runs on the processor only.</summary>
    CpuX64,

    /// <summary>Windows on ARM build that runs on the processor only.</summary>
    CpuArm64,

    /// <summary>x64 build with the Vulkan back end, for AMD and Intel GPUs (runs on the GPU).</summary>
    VulkanX64
}

/// <summary>
/// One pinned download: the exact file, where it comes from and the SHA-256 it must have.
/// </summary>
/// <param name="FileName">File name of the download.</param>
/// <param name="Url">Download address (a fixed release or a fixed repository revision, never "latest").</param>
/// <param name="Sha256">SHA-256 the download must have (64 lowercase hex digits).</param>
/// <param name="SizeBytes">Download size in bytes.</param>
/// <param name="InstalledBytes">Disk space the installed result takes, for the free-space check.</param>
public sealed record WhisperDownload(string FileName, string Url, string Sha256, long SizeBytes, long InstalledBytes);

/// <summary>
/// What the 1-click subtitle install will put on this PC, and why.
/// </summary>
/// <param name="Build">Program build.</param>
/// <param name="Program">Program archive download.</param>
/// <param name="Model">Speech model download.</param>
/// <param name="Reason">One sentence for the user: which processor or GPU will do the work and why.</param>
public sealed record WhisperInstallPlan(WhisperBuildKind Build, WhisperDownload Program, WhisperDownload Model, string Reason)
{
    /// <summary>
    /// Voice activity detection model; whisper.cpp uses it to skip music and silence, where the speech model
    /// otherwise invents text. Default <see cref="WhisperCatalog.SileroVadModel"/>.
    /// </summary>
    public WhisperDownload Vad { get; init; } = WhisperCatalog.SileroVadModel;

    /// <summary>Total bytes to download.</summary>
    public long DownloadBytes => Program.SizeBytes + Model.SizeBytes + Vad.SizeBytes;

    /// <summary>True when the program runs on a GPU (NVIDIA through CUDA, AMD or Intel through Vulkan).</summary>
    public bool UsesGpu => Build is WhisperBuildKind.CudaX64 or WhisperBuildKind.VulkanX64;
}

/// <summary>
/// The whisper.cpp release and speech models the 1-click "Generate subtitles" install uses, each pinned by SHA-256,
/// and the choice of build for this PC.
/// </summary>
/// <remarks>
/// whisper.cpp (https://github.com/ggml-org/whisper.cpp) and its converted models are MIT licensed. The v1.9.5 release
/// publishes its Windows programs as the b5454 build. Hashes are the asset digests GitHub publishes for that build and
/// the SHA-256 Hugging Face lists for each model file, both re-computed from the downloads on 2026-11-14.
/// whisper.cpp publishes GPU builds for Windows only for NVIDIA (CUDA). For AMD and Intel GPUs, IgnisDisc's own
/// workflow (.github/workflows/whisper-vulkan.yml) builds the same v1.9.5 source with the Vulkan back end on a
/// GitHub-hosted runner, with a build provenance attestation, and attaches it to the whisper-vulkan-1.9.5 pre-release.
/// Measured on an 86-minute film: Intel UHD Graphics 770 (integrated) 18.1 min, word error rate 26.0%; an RTX 3090
/// through the same Vulkan build 60 s (CUDA build 56 s).
/// The CUDA 11.8 build of b5454 is not used: its archive lacks the CUDA 11 cuBLAS libraries, so on a PC without the
/// CUDA 11 toolkit it silently runs on the processor.
/// </remarks>
public static class WhisperCatalog
{
    /// <summary>whisper.cpp version the program downloads come from.</summary>
    public const string WhisperVersion = "1.9.5";

    /// <summary>GitHub release tag that holds the v1.9.5 Windows programs.</summary>
    public const string ReleaseTag = "b5454";

    /// <summary>Revision of the Hugging Face model repository the model downloads are pinned to.</summary>
    public const string ModelRevision = "5359861c739e955e79d9a303bcbc70fb988958b1";

    /// <summary>Revision of the Hugging Face voice activity detection model repository.</summary>
    public const string VadModelRevision = "9ffd54a1e1ee413ddf265af9913beaf518d1639b";

    /// <summary>
    /// Oldest NVIDIA driver that runs the CUDA 12.4 build (the driver shipped with CUDA 12.4.0 for Windows).
    /// </summary>
    public static readonly Version MinimumCuda12Driver = new(551, 61);

    /// <summary>Tag of the IgnisDisc pre-release that holds the Vulkan build of whisper.cpp 1.9.5.</summary>
    public const string VulkanReleaseTag = "whisper-vulkan-" + WhisperVersion;

    private const string ReleaseBase = "https://github.com/ggml-org/whisper.cpp/releases/download/" + ReleaseTag + "/";
    private const string VulkanReleaseBase = "https://github.com/tether-works/IgnisDisc/releases/download/" + VulkanReleaseTag + "/";
    private const string ModelBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/" + ModelRevision + "/";

    /// <summary>CUDA 12.4 x64 program archive (includes the CUDA runtime and cuBLAS libraries).</summary>
    public static readonly WhisperDownload CudaX64Program = new(
        "whisper-bin-win-cuda-12.4.0-x64.zip",
        ReleaseBase + "whisper-bin-win-cuda-12.4.0-x64.zip",
        "afef0b881c500958921c3f5523b50e59ee2ec9b6f5cbd25b324c51ed308a957a",
        684_913_404,
        1_300_000_000);

    /// <summary>Processor-only x64 program archive.</summary>
    public static readonly WhisperDownload CpuX64Program = new(
        "whisper-bin-x64.zip",
        ReleaseBase + "whisper-bin-x64.zip",
        "6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb",
        8_928_640,
        20_000_000);

    /// <summary>
    /// Vulkan x64 program archive for AMD and Intel GPUs: whisper.cpp 1.9.5 source (commit d1be6fde) built by
    /// IgnisDisc's workflow run 44927332341. It uses the vulkan-1.dll that comes with the graphics driver.
    /// </summary>
    public static readonly WhisperDownload VulkanX64Program = new(
        "whisper-bin-win-vulkan-x64.zip",
        VulkanReleaseBase + "whisper-bin-win-vulkan-x64.zip",
        "9184f102d7db9d68d4ecbdab36ade841fe1a06532337c65b7f8367337bba5221",
        18_126_509,
        60_000_000);

    /// <summary>Processor-only Windows on ARM program archive.</summary>
    public static readonly WhisperDownload CpuArm64Program = new(
        "whisper-bin-win-cpu-arm64.zip",
        ReleaseBase + "whisper-bin-win-cpu-arm64.zip",
        "28c37e7b598c3d9bbfef94f3bd67f2ee6f12b7c86f3da5edcf5e4308d615ff6d",
        4_371_193,
        12_000_000);

    /// <summary>
    /// large-v3-turbo speech model (multilingual, 1.6 GB) for GPUs. The f16 file is used rather than a
    /// quantized one: on an RTX 3090 the q5_0 file took 2.5 times as long.
    /// </summary>
    public static readonly WhisperDownload LargeV3TurboModel = new(
        "ggml-large-v3-turbo.bin",
        ModelBase + "ggml-large-v3-turbo.bin",
        "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69",
        1_624_555_275,
        1_624_555_275);

    /// <summary>base speech model (multilingual, 148 MB) for processor-only PCs.</summary>
    public static readonly WhisperDownload BaseModel = new(
        "ggml-base.bin",
        ModelBase + "ggml-base.bin",
        "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
        147_951_465,
        147_951_465);

    /// <summary>
    /// Silero voice activity detection model (MIT, 885 KB) converted for whisper.cpp. With it whisper.cpp only
    /// transcribes the parts that hold speech; without it, a film's music and silence filled with invented lines.
    /// </summary>
    public static readonly WhisperDownload SileroVadModel = new(
        "ggml-silero-v6.2.0.bin",
        "https://huggingface.co/ggml-org/whisper-vad/resolve/" + VadModelRevision + "/ggml-silero-v6.2.0.bin",
        "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987",
        885_098,
        885_098);

    /// <summary>
    /// Every model the installer can put down, best first. The locator uses the first one present.
    /// </summary>
    public static IReadOnlyList<WhisperDownload> ModelsBestFirst { get; } = [LargeV3TurboModel, BaseModel];

    /// <summary>The program download for a build.</summary>
    public static WhisperDownload ProgramFor(WhisperBuildKind build) => build switch
    {
        WhisperBuildKind.CudaX64 => CudaX64Program,
        WhisperBuildKind.CpuArm64 => CpuArm64Program,
        WhisperBuildKind.VulkanX64 => VulkanX64Program,
        _ => CpuX64Program
    };

    /// <summary>
    /// Plan for this PC, from the display adapters Windows lists and whether the NVIDIA CUDA driver library is present.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static WhisperInstallPlan ChoosePlanForThisPc() => ChoosePlan(
        WindowsGpuDetector.DetectAdapters(),
        WindowsGpuDetector.IsNvidiaCudaDriverInstalled(),
        RuntimeInformation.OSArchitecture);

    /// <summary>
    /// Chooses the program build and model: the CUDA build and large-v3-turbo when an NVIDIA GPU with a new enough
    /// driver is present; the Vulkan build and large-v3-turbo when there is no NVIDIA GPU but an AMD or Intel one
    /// (integrated graphics included: slower than a graphics card, but on the measured film 26.0% word errors against
    /// 34.3% for the base model on the processor); otherwise the processor build and the base model.
    /// </summary>
    public static WhisperInstallPlan ChoosePlan(
        IReadOnlyList<GpuAdapterInfo> adapters,
        bool nvidiaCudaDriverInstalled,
        Architecture osArchitecture)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        if (osArchitecture == Architecture.Arm64)
        {
            return new WhisperInstallPlan(WhisperBuildKind.CpuArm64, CpuArm64Program, BaseModel,
                "Runs on the processor: whisper.cpp publishes no GPU build for Windows on ARM PCs like this one.");
        }

        GpuAdapterInfo? nvidia = adapters
            .Where(a => a.Vendor == GpuVendor.Nvidia)
            .OrderByDescending(a => a.NvidiaDriverVersion ?? new Version(0, 0))
            .FirstOrDefault();

        if (nvidia != null)
        {
            Version? driver = nvidia.NvidiaDriverVersion;
            if (!nvidiaCudaDriverInstalled)
            {
                return CpuPlan($"Runs on the processor: {nvidia.Name} was found but its CUDA driver library is not " +
                               "installed. Install the NVIDIA driver, then install again to use the GPU.");
            }

            if (driver == null || driver < MinimumCuda12Driver)
            {
                string found = driver == null ? "an unknown driver version" : $"driver {FormatDriver(driver)}";
                return CpuPlan($"Runs on the processor: {nvidia.Name} has {found}; the GPU build needs NVIDIA driver " +
                               $"{FormatDriver(MinimumCuda12Driver)} or newer. Update the driver, then install again.");
            }

            return new WhisperInstallPlan(WhisperBuildKind.CudaX64, CudaX64Program, LargeV3TurboModel,
                $"Runs on {nvidia.Name} (NVIDIA driver {FormatDriver(driver)}) with the large-v3-turbo model.");
        }

        // AMD first: on a PC with both, the Intel adapter is usually the processor's integrated graphics. The name is
        // only for the message; whisper.cpp itself uses the first GPU the Vulkan driver lists.
        GpuAdapterInfo? other = adapters.FirstOrDefault(a => a.Vendor == GpuVendor.Amd) ??
                                adapters.FirstOrDefault(a => a.Vendor == GpuVendor.Intel);
        if (other != null)
        {
            return new WhisperInstallPlan(WhisperBuildKind.VulkanX64, VulkanX64Program, LargeV3TurboModel,
                $"Runs on {other.Name} through Vulkan with the large-v3-turbo model. On integrated graphics expect " +
                "about a fifth of the film's running time (an 86-minute film took 18 minutes on Intel UHD Graphics " +
                "770); a graphics card is much faster. If the graphics driver has no Vulkan support, the processor " +
                "is used instead, which is slow with this model: update the driver.");
        }

        return CpuPlan("Runs on the processor: no NVIDIA, AMD or Intel GPU was found.");
    }

    private static WhisperInstallPlan CpuPlan(string reason) =>
        new(WhisperBuildKind.CpuX64, CpuX64Program, BaseModel, reason + " Uses the smaller base model; expect a " +
            "film to take several minutes or more, depending on the processor.");

    private static string FormatDriver(Version v) => $"{v.Major}.{v.Minor:D2}";
}
