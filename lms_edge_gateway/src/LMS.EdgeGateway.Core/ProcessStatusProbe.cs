using System.Diagnostics;

namespace LMS.EdgeGateway.Core;

public sealed class ProcessStatusProbe : IProcessStatusProbe
{
    public async Task<bool> IsRunningAsync(string processPattern, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "pgrep",
                ArgumentList = { "-f", processPattern },
                WorkingDirectory = "/",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
