namespace TelemoqNet.Api.Emulation;

public sealed class VirtualFileSystem
{
    private const int MaxEntries = 1024;
    private const int MaxFileLength = 65536;
    private const int MaxTotalLength = 1048576;
    private int _totalContentLength;
    private readonly Dictionary<string, VirtualFile> _files =
        new(StringComparer.Ordinal);

    public bool Add(VirtualFile file)
    {
        _files.TryGetValue(Normalize(file.Path), out var existing);
        var previousLength = existing?.Contents.Length ?? 0;
        if (file.Contents.Length > MaxFileLength ||
            file.Contents.Length - previousLength > MaxTotalLength - _totalContentLength ||
            _files.Count >= MaxEntries && !Contains(file.Path))
        {
            return false;
        }

        _totalContentLength += file.Contents.Length - previousLength;
        _files[Normalize(file.Path)] =
            file with
            {
                Path = Normalize(file.Path)
            };
        return true;
    }

    public bool Contains(string path) =>
        _files.ContainsKey(Normalize(path));

    public bool IsDirectory(string path) =>
        _files.TryGetValue(Normalize(path), out var file) &&
        file.IsDirectory;

    public bool AddDirectory(string path)
    {
        var normalized = Normalize(path);

        if (_files.ContainsKey(normalized))
        {
            return false;
        }

        return Add(new VirtualFile(normalized, string.Empty, IsDirectory: true));
    }

    public bool Touch(string path)
    {
        var normalized = Normalize(path);

        if (_files.TryGetValue(normalized, out var file))
        {
            if (file.IsDirectory)
            {
                return false;
            }

            return true;
        }

        return Add(new VirtualFile(normalized, string.Empty));
    }

    public bool Append(string path, string contents) =>
        Write(path, contents, append: true, out _);

    public bool Write(string path, string contents, bool append, out string? error)
    {
        var normalized = Normalize(path);
        error = null;
        if (IsDirectory(normalized))
        {
            error = "Is a directory";
            return false;
        }

        var parent = normalized[..normalized.LastIndexOf('/')];
        if (!IsDirectory(parent))
        {
            error = "No such file or directory";
            return false;
        }

        _files.TryGetValue(normalized, out var file);
        var existing = append ? file?.Contents ?? string.Empty : string.Empty;
        if (contents.Length > MaxFileLength - existing.Length ||
            file is null && _files.Count >= MaxEntries)
        {
            error = "No space left on device";
            return false;
        }

        var added = Add(file is null
            ? new VirtualFile(normalized, contents)
            : file with { Contents = existing + contents });
        if (!added)
        {
            error = "No space left on device";
        }

        return added;
    }

    public IEnumerable<VirtualFile> ListEntries(string path)
    {
        var directory = Normalize(path);
        var prefix = directory == "/" ? "/" : directory + "/";

        return _files.Values
            .Where(file => file.Path.StartsWith(prefix, StringComparison.Ordinal))
            .Where(file => file.Path[prefix.Length..].IndexOf('/') < 0)
            .OrderBy(file => file.Path, StringComparer.Ordinal);
    }

    public bool TryRead(string path, out string contents)
    {
        if (_files.TryGetValue(Normalize(path), out var file) &&
            !file.IsDirectory)
        {
            contents = file.Contents;
            return true;
        }

        contents = string.Empty;
        return false;
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalized = path.StartsWith('/') ? path : "/" + path;
        normalized = normalized.TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }

    public static string ResolvePath(string currentDirectory, string path)
    {
        var combined = path.StartsWith('/')
            ? path
            : $"{currentDirectory.TrimEnd('/')}/{path}";
        var parts = new Stack<string>();

        foreach (var part in combined.Split('/'))
        {
            if (string.IsNullOrEmpty(part) || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.Pop();
                }

                continue;
            }

            parts.Push(part);
        }

        return "/" + string.Join('/', parts.Reverse());
    }
}
