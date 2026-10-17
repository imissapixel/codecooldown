using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodeUsageMonitor;

internal interface IUsageTransport
{
    Task<bool> IsRunningAsync(Profile p, CancellationToken token);
    Task<JsonElement> RequestAsync(Profile p, string operation, CancellationToken token);
}

internal sealed class WslTransport : IUsageTransport
{
    private readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(120);
    internal static ProcessStartInfo BuildStartInfo(Profile profile, bool listRunning = false)
    {
        Store.Validate(profile);
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wsl.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = listRunning ? Encoding.Unicode : Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (listRunning)
        {
            foreach (var arg in new[] { "--list", "--running", "--quiet" }) info.ArgumentList.Add(arg);
        }
        else
        {
            info.ArgumentList.Add("--distribution"); info.ArgumentList.Add(profile.Distro);
            if (profile.User != "") { info.ArgumentList.Add("--user"); info.ArgumentList.Add(profile.User); }
            foreach (var arg in new[] { "--exec", "python3", profile.HelperPath }) info.ArgumentList.Add(arg);
        }
        return info;
    }

    public async Task<bool> IsRunningAsync(Profile p, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(8));
        var output = await RunAsync(BuildStartInfo(p, true), "", limit.Token);
        return output.Split(['\r', '\n', '\0'], StringSplitOptions.RemoveEmptyEntries).Any(s => s.Trim() == p.Distro);
    }

    public async Task<JsonElement> RequestAsync(Profile p, string operation, CancellationToken token)
    {
        var request = JsonSerializer.Serialize(new { version = 1, operation, profile = p }, Json.Options);
        if (Encoding.UTF8.GetByteCount(request) > 65536) throw new InvalidDataException();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(_requestTimeout);
        var raw = await RunAsync(BuildStartInfo(p), request, limit.Token);
        using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
        var value = document.RootElement;
        if (!value.TryGetProperty("version", out var version) || version.GetInt32() != 1) throw new InvalidDataException();
        return value.Clone();
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int max, Process process, CancellationToken token)
    {
        var buffer = new char[4096];
        var output = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (output.Length + count > max)
            {
                try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
                throw new InvalidDataException("Helper output exceeded its limit.");
            }
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }

    private static async Task<string> RunAsync(ProcessStartInfo info, string input, CancellationToken token)
    {
        using var process = new Process { StartInfo = info };
        process.Start();
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var stdout = ReadBoundedAsync(process.StandardOutput, 2 * 1024 * 1024, process, token);
        var stderr = ReadBoundedAsync(process.StandardError, 32768, process, token);
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), token);
            process.StandardInput.Close();
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(token));
            if (process.ExitCode != 0) throw new IOException("WSL/helper unavailable. Check distribution, Python, and helper installation.");
            return await stdout;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        }
    }
}
