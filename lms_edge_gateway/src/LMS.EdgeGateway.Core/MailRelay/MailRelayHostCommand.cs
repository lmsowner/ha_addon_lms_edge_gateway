using System.Diagnostics;
using System.Text;

namespace LMS.EdgeGateway.Core;

public sealed class MailRelayHostCommand : IMailRelayHostCommand
{
    public async Task<MailRelayHostCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        byte[]? standardInput = null,
        TimeSpan? timeout = null)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                output.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                error.AppendLine(args.Data);
            }
        };

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
        {
            timeoutSource.CancelAfter(limit);
        }

        try
        {
            if (!process.Start())
            {
                return new MailRelayHostCommandResult(127, string.Empty, $"Failed to start {fileName}.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (standardInput is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(standardInput, timeoutSource.Token);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(timeoutSource.Token);
            return new MailRelayHostCommandResult(process.ExitCode, output.ToString(), error.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new MailRelayHostCommandResult(124, output.ToString(), string.IsNullOrWhiteSpace(error.ToString())
                ? $"{fileName} timed out."
                : error.ToString());
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new MailRelayHostCommandResult(127, string.Empty, exception.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup of a timed-out host command.
        }
    }
}
