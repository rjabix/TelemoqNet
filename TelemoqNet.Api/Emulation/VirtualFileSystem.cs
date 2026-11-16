namespace TelemoqNet.Api.Emulation;

public sealed class VirtualFileSystem
{
    private const int MaxEntries = 1024;
    private const int MaxFileLength = 65536;
    private const int MaxTotalLength = 1048576;
    private int _totalContentLength;
    private readonly Dictionary<string, VirtualFile> _files =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, GeneratedContents> _generated = new(StringComparer.Ordinal);
    private readonly HashSet<string> _protectedDirectories = new(StringComparer.Ordinal);

    private sealed record GeneratedContents(Func<string> Read, int MaxLength);

    // Reserve each provider's maximum size up front, so generated reads cannot exhaust
    // the total content budget or consume capacity already promised to ordinary files.
    public bool AddGenerated(string path, Func<string> read, int maxLength)
    {
        var normalized = Normalize(path);
        if (maxLength < 0 || maxLength > MaxFileLength || Contains(normalized) ||
            maxLength > MaxTotalLength - _totalContentLength ||
            !Add(new VirtualFile(normalized, string.Empty, IsReadOnly: true)))
        {
            return false;
        }

        _generated.Add(normalized, new GeneratedContents(read, maxLength));
        _totalContentLength += maxLength;
        return true;
    }

    public void ProtectDirectory(string path) => _protectedDirectories.Add(Normalize(path));

    public bool IsReadOnly(string path)
    {
        var normalized = Normalize(path);
        return _protectedDirectories.Any(directory => normalized == directory ||
                   normalized.StartsWith(directory == "/" ? "/" : directory + "/", StringComparison.Ordinal)) ||
               _files.TryGetValue(normalized, out var file) && file.IsReadOnly;
    }

    public bool Add(VirtualFile file)
    {
        if (IsReadOnly(file.Path))
        {
            return false;
        }

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

    public bool AddDirectory(string path) => AddDirectory(path, out _);

    public bool AddDirectory(string path, out string? error)
    {
        var normalized = Normalize(path);
        error = null;
        if (IsReadOnly(normalized))
        {
            error = "Read-only file system";
            return false;
        }

        if (_files.ContainsKey(normalized))
        {
            error = "File exists";
            return false;
        }

        var added = Add(new VirtualFile(normalized, string.Empty, IsDirectory: true));
        error = added ? null : "No space left on device";
        return added;
    }

    public bool Touch(string path) => Touch(path, out _);

    public bool Touch(string path, out string? error)
    {
        var normalized = Normalize(path);
        error = null;
        if (IsReadOnly(normalized))
        {
            error = "Read-only file system";
            return false;
        }

        if (_files.TryGetValue(normalized, out var file))
        {
            if (file.IsDirectory)
            {
                error = "Is a directory";
                return false;
            }

            return true;
        }

        var added = Add(new VirtualFile(normalized, string.Empty));
        error = added ? null : "No space left on device";
        return added;
    }

    public bool Append(string path, string contents) =>
        Write(path, contents, append: true, out _);

    public bool Write(string path, string contents, bool append, out string? error)
    {
        var normalized = Normalize(path);
        error = null;
        if (IsReadOnly(normalized))
        {
            error = "Read-only file system";
            return false;
        }

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
            .Where(file => file.Path != directory)
            .Where(file => file.Path.StartsWith(prefix, StringComparison.Ordinal))
            .Where(file => file.Path[prefix.Length..].IndexOf('/') < 0)
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => file with
            {
                Contents = TryRead(file.Path, out var contents) ? contents : string.Empty,
                IsReadOnly = IsReadOnly(file.Path)
            })
            .ToArray();
    }

    public bool TryGetFile(string path, out VirtualFile? file)
    {
        if (_files.TryGetValue(Normalize(path), out var entry))
        {
            file = entry with
            {
                Contents = TryRead(entry.Path, out var contents) ? contents : string.Empty,
                IsReadOnly = IsReadOnly(entry.Path)
            };
            return true;
        }

        file = null;
        return false;
    }

    public bool TryRead(string path, out string contents)
    {
        if (_files.TryGetValue(Normalize(path), out var file) &&
            !file.IsDirectory)
        {
            if (_generated.TryGetValue(file.Path, out var generated))
            {
                var snapshot = generated.Read();
                contents = snapshot.Length <= generated.MaxLength ? snapshot : string.Empty;
                return snapshot.Length <= generated.MaxLength;
            }

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

        return ResolvePath("/", path);
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
