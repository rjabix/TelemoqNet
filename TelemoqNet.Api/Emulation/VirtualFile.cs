namespace TelemoqNet.Api.Emulation;

public sealed record VirtualFile(
    string Path,
    string Contents,
    bool IsExecutable = false,
    bool IsDirectory = false);