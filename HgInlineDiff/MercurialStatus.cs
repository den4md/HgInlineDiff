using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HgInlineDiff;

internal sealed class MercurialFileChange
{
    public string Repository { get; }
    public string Path { get; }
    public string Status { get; }
    public string FullPath => System.IO.Path.Combine(Repository, Path.Replace('/', '\\'));

    public MercurialFileChange(string repository, string path, char status)
    {
        Repository = repository;
        Path = path;
        Status = status.ToString();
    }
}

// One immutable disk snapshot supplies both the changes window and synchronous shell glyph requests.
internal sealed class MercurialStatus
{
    private readonly Dictionary<string, char> _files = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, char> _directories = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
    public string Root { get; }
    public IReadOnlyList<MercurialFileChange> Changes { get; }

    private MercurialStatus(string root, string text)
    {
        Root = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');
        _directories[Root] = 'C';
        foreach (var record in text.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (record.Length < 3 || record[1] != ' ' || "MAR!?C".IndexOf(record[0]) < 0)
                throw new InvalidOperationException("Unexpected Mercurial status record.");
            var relativePath = record.Substring(2).Replace('/', '\\');
            var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relativePath));
            if (!fullPath.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Mercurial returned a path outside the repository.");
            var status = record[0];
            _files[fullPath] = status;
            for (var directory = System.IO.Path.GetDirectoryName(fullPath);
                 directory != null && (directory.Equals(Root, StringComparison.OrdinalIgnoreCase)
                     || directory.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase));
                 directory = System.IO.Path.GetDirectoryName(directory))
            {
                if (!_directories.TryGetValue(directory, out var previous) || previous == 'C')
                    _directories[directory] = status == 'C' ? 'C' : 'M';
            }
        }
        Changes = _files.Where(pair => pair.Value != 'C')
            .Select(pair => new MercurialFileChange(Root, pair.Key.Substring(Root.Length + 1).Replace('\\', '/'), pair.Value))
            .OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public char GetStatus(string path)
    {
        path = path.Replace('/', '\\').TrimEnd('\\');
        return _files.TryGetValue(path, out var status) || _directories.TryGetValue(path, out status) ? status : '\0';
    }

    public static MercurialStatus Parse(string root, byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.Default.GetString(bytes); }
        return new MercurialStatus(root, text);
    }
}
