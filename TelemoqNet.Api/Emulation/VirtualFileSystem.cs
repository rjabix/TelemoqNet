namespace TelemoqNet.Api.Emulation;

public sealed class VirtualFileSystem
{
    private readonly Dictionary<string, VirtualFile> _files =
        new(StringComparer.Ordinal);

    public void Add(VirtualFile file)
    {
        _files[Normalize(file.Path)] =
            file with { Path = Normalize(file.Path) };
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
            return false;

        Add(new VirtualFile(normalized, string.Empty, IsDirectory: true));
        return true;
    }

    public bool Touch(string path)
    {
        var normalized = Normalize(path);

        if (_files.TryGetValue(normalized, out var file))
        {
            if (file.IsDirectory)
                return false;

            return true;
        }

        Add(new VirtualFile(normalized, string.Empty));
        return true;
    }

    public bool Append(string path, string contents)
    {
        var normalized = Normalize(path);

        if (_files.TryGetValue(normalized, out var file))
        {
            if (file.IsDirectory)
                return false;

            _files[normalized] = file with
            {
                Contents = file.Contents + contents
            };
            return true;
        }

        Add(new VirtualFile(normalized, contents));
        return true;
    }

    public IEnumerable<string> List(string path)
    {
        var directory = Normalize(path);
        var prefix = directory == "/" ? "/" : directory + "/";

        return _files.Keys
            .Where(filePath => filePath.StartsWith(prefix, StringComparison.Ordinal))
            .Select(filePath => filePath[prefix.Length..].Split('/')[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
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
            return "/";

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
                continue;
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.Pop();
                continue;
            }

            parts.Push(part);
        }

        return "/" + string.Join('/', parts.Reverse());
    }
}