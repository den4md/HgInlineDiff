using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HgInlineDiff;

internal sealed class MercurialBaseline
{
    public string Text { get; }
    public string Revision { get; }

    public MercurialBaseline(string text, string revision)
    {
        Text = text;
        Revision = revision;
    }
}

internal static class MercurialRepository
{
    internal const int MaximumFileBytes = 4 * 1024 * 1024;

    public static string? FindRoot(string filePath)
    {
        try
        {
            for (var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
                 directory != null; directory = Path.GetDirectoryName(directory))
            {
                if (Directory.Exists(Path.Combine(directory, ".hg")))
                    return directory;
            }
        }
        catch (Exception error) when (error is ArgumentException || error is IOException
            || error is UnauthorizedAccessException || error is System.Security.SecurityException
            || error is NotSupportedException)
        {
            return null;
        }
        return null;
    }

    public static async Task<MercurialBaseline?> LoadAsync(
        string filePath, Encoding encoding, CancellationToken cancellationToken)
    {
        var root = FindRoot(filePath);
        if (root == null)
            return null;

        var relativePath = Path.GetFullPath(filePath).Substring(root.TrimEnd('\\').Length + 1).Replace('\\', '/');
        var statusBytes = await RunAsync(root, "status --all --copies --print0 -- " + Quote("path:" + relativePath), cancellationToken).ConfigureAwait(false);
        string status;
        try
        {
            status = new UTF8Encoding(false, true).GetString(statusBytes);
        }
        catch (DecoderFallbackException)
        {
            // Older Windows Mercurial clients emit raw ANSI filenames even with HGENCODING=utf-8.
            status = Encoding.Default.GetString(statusBytes);
        }
        var records = status.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        if (records.Length == 0 || records[0][0] == '?' || records[0][0] == 'I')
            return null;

        var originalPath = relativePath;
        if (records[0][0] == 'A')
        {
            var copySource = records.Skip(1).FirstOrDefault(record => record.StartsWith("  ", StringComparison.Ordinal));
            if (copySource == null)
                return new MercurialBaseline(string.Empty, "new file");
            originalPath = copySource.Substring(2);
        }

        // Resolve '.' once, so a concurrent update cannot change the revision between status and cat.
        var revisionBytes = await RunAsync(root, "log --rev . --template {node}", cancellationToken).ConfigureAwait(false);
        var revision = Encoding.UTF8.GetString(revisionBytes).Trim();
        var bytes = await RunAsync(root, "cat --rev " + Quote(revision) + " -- " + Quote("path:" + originalPath), cancellationToken).ConfigureAwait(false);
        using (var stream = new MemoryStream(bytes))
        using (var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true))
        {
            var text = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (text.IndexOf('\0') >= 0)
                return null;
            return new MercurialBaseline(text, revision.Substring(0, Math.Min(12, revision.Length)));
        }
    }

    private static string Quote(string value)
    {
        // These arguments are Windows filenames or Mercurial hashes; neither may contain a quote.
        if (value.IndexOf('"') >= 0)
            throw new ArgumentException("Unexpected quote in a Mercurial argument.", nameof(value));
        return "\"" + value + "\"";
    }

    internal static async Task<MercurialStatus> LoadStatusAsync(string root, CancellationToken cancellationToken)
    {
        var bytes = await RunAsync(root,
            "status --modified --added --removed --deleted --unknown --clean --print0",
            cancellationToken, 64 * 1024 * 1024).ConfigureAwait(false);
        return MercurialStatus.Parse(root, bytes);
    }

    private static async Task<byte[]> RunAsync(string root, string arguments, CancellationToken cancellationToken,
        int maximumBytes = MaximumFileBytes)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        using (var process = new Process())
        using (var output = new MemoryStream())
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var token = timeout.Token;
            process.StartInfo = new ProcessStartInfo("hg.exe", "--noninteractive " + arguments)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            process.StartInfo.EnvironmentVariables["HGPLAIN"] = "1";
            process.StartInfo.EnvironmentVariables["HGENCODING"] = "utf-8";

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                process.Start();
            }
            catch (Win32Exception error)
            {
                throw new InvalidOperationException("Cannot start hg.exe. Add Mercurial or TortoiseHg to PATH.", error);
            }

            using (token.Register(() => StopProcess(process)))
            {
                try
                {
                    var errorTask = process.StandardError.ReadToEndAsync();
                    var buffer = new byte[8192];
                    int count;
                    while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
                    {
                        if (output.Length + count > maximumBytes)
                            throw new InvalidOperationException(maximumBytes == MaximumFileBytes
                                ? "Mercurial margin supports files up to 4 MiB."
                                : "Mercurial status output exceeds 64 MiB.");
                        await output.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                    }
                    await Task.Run(() => process.WaitForExit(), token).ConfigureAwait(false);
                    var errorText = await errorTask.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException("hg " + arguments.Split(' ')[0] + " failed: " + errorText.Trim());
                    return output.ToArray();
                }
                catch (Exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Mercurial did not respond within 15 seconds.");
                }
                finally
                {
                    StopProcess(process);
                }
            }
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
