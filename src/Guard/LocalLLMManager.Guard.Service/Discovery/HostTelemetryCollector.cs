using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using LocalLLMManager.Guard.Contracts;

namespace LocalLLMManager.Guard.Service.Discovery;

public interface IHostTelemetryCollector
{
    Task<HostHardwareTelemetry> CollectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only Windows host telemetry collector:
/// - GPU VRAM (Total/Used/Free), Utilization %, Temp, Power, and compute PIDs via nvidia-smi
/// - Physical RAM & Windows Commit Limit/Used/Available via native Win32 GlobalMemoryStatusEx
/// </summary>
public sealed class HostTelemetryCollector : IHostTelemetryCollector
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    public async Task<HostHardwareTelemetry> CollectAsync(CancellationToken cancellationToken = default)
    {
        var mem = ReadWindowsMemory();
        var (gpuOk, name, total, used, free, util, temp, power, procs, gpuErr) = await ReadNvidiaGpuAsync(cancellationToken).ConfigureAwait(false);

        return new HostHardwareTelemetry
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            GpuAvailable = gpuOk,
            GpuName = name,
            GpuTotalVramMiB = total,
            GpuUsedVramMiB = used,
            GpuFreeVramMiB = free,
            GpuUtilizationPct = util,
            GpuTemperatureC = temp,
            GpuPowerDrawW = power,
            GpuProcesses = procs,
            RamAvailable = mem.RamOk,
            RamTotalMiB = mem.RamTotalMiB,
            RamAvailableMiB = mem.RamAvailMiB,
            CommitAvailable = mem.CommitOk,
            CommitLimitMiB = mem.CommitLimitMiB,
            CommitUsedMiB = mem.CommitUsedMiB,
            CommitAvailableMiB = mem.CommitAvailMiB,
            DiagnosticError = gpuErr
        };
    }

    private static (bool RamOk, double? RamTotalMiB, double? RamAvailMiB, bool CommitOk, double? CommitLimitMiB, double? CommitUsedMiB, double? CommitAvailMiB) ReadWindowsMemory()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            return (false, null, null, false, null, null, null);

        const double MiB = 1024.0 * 1024.0;
        var ramTotal = Math.Round(status.ullTotalPhys / MiB, 1);
        var ramAvail = Math.Round(status.ullAvailPhys / MiB, 1);
        var commitLimit = Math.Round(status.ullTotalPageFile / MiB, 1);
        var commitAvail = Math.Round(status.ullAvailPageFile / MiB, 1);
        var commitUsed = Math.Round(Math.Max(0, commitLimit - commitAvail), 1);

        return (ramTotal > 0, ramTotal, ramAvail, commitLimit > 0, commitLimit, commitUsed, commitAvail);
    }

    private static async Task<(bool Ok, string? Name, double? Total, double? Used, double? Free, double? Util, double? Temp, double? Power, IReadOnlyList<GpuComputeProcessEntry> Procs, string? Error)> ReadNvidiaGpuAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var gpuOut = await RunProcessOutputAsync(
                "nvidia-smi",
                "--query-gpu=name,memory.total,memory.used,memory.free,utilization.gpu,temperature.gpu,power.draw --format=csv,noheader,nounits",
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(gpuOut))
                return (false, null, null, null, null, null, null, null, [], "nvidia-smi returned empty output.");

            var firstLine = gpuOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstLine))
                return (false, null, null, null, null, null, null, null, [], "nvidia-smi produced no GPU row.");

            var parts = firstLine.Split(',').Select(s => s.Trim()).ToArray();
            if (parts.Length < 7)
                return (false, null, null, null, null, null, null, null, [], $"Unexpected nvidia-smi column count: {parts.Length}");

            var total = ParseNullableDouble(parts[1]);
            var used = ParseNullableDouble(parts[2]);
            var free = ParseNullableDouble(parts[3]);
            var util = ParseNullableDouble(parts[4]);
            var temp = ParseNullableDouble(parts[5]);
            var power = ParseNullableDouble(parts[6]);

            var procs = new List<GpuComputeProcessEntry>();
            var procOut = await RunProcessOutputAsync(
                "nvidia-smi",
                "--query-compute-apps=pid,process_name,used_memory --format=csv,noheader,nounits",
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(procOut))
            {
                foreach (var line in procOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var cols = line.Split(',').Select(s => s.Trim()).ToArray();
                    if (cols.Length >= 3 && int.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                    {
                        procs.Add(new GpuComputeProcessEntry(pid, cols[1], ParseNullableDouble(cols[2])));
                    }
                }
            }

            return (total.HasValue && used.HasValue && free.HasValue, parts[0], total, used, free, util, temp, power, procs, null);
        }
        catch (Exception ex)
        {
            return (false, null, null, null, null, null, null, null, [], $"nvidia-smi probe failed: {ex.Message}");
        }
    }

    private static double? ParseNullableDouble(string raw)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static async Task<string?> RunProcessOutputAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        if (!proc.Start())
            return null;

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
        await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);
        return proc.ExitCode == 0 ? stdout : null;
    }
}
