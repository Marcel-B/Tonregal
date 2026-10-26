// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using Tonregal.Application.Abstractions;

namespace Tonregal.Infrastructure.Analysis;

internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Startet ein Werkzeug als Subprozess und sammelt die Ausgabe; bei Zeitüberschreitung wird es beendet.</summary>
internal static class ExternalProcess
{
    /// <param name="onStderrLine">Bekommt jede stderr-Zeile, sobald sie da ist (z. B. für Fortschritt).</param>
    public static async Task<ProcessResult> RunAsync(
        string fileName, IEnumerable<string> arguments, TimeSpan timeout, string installHint, CancellationToken ct,
        Action<string>? onStderrLine = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new Win32Exception("Process.Start lieferte null");
        }
        catch (Win32Exception e)
        {
            throw new ExternalToolUnavailableException($"{Path.GetFileName(fileName)} nicht gefunden ({fileName}) – {installHint}", e);
        }

        using (process)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutSource.CancelAfter(timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var stderr = ReadLinesAsync(process.StandardError, onStderrLine, timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
                return new ProcessResult(process.ExitCode, await stdout, await stderr);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                if (ct.IsCancellationRequested)
                    throw;
                throw new InvalidDataException($"{Path.GetFileName(fileName)} brauchte länger als {timeout.TotalMinutes:0} min");
            }
        }
    }

    private static async Task<string> ReadLinesAsync(StreamReader reader, Action<string>? onLine, CancellationToken ct)
    {
        if (onLine is null)
            return await reader.ReadToEndAsync(ct);
        var all = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            all.AppendLine(line);
            onLine(line);
        }

        return all.ToString();
    }
}
