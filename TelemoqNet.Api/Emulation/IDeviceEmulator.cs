namespace TelemoqNet.Api.Emulation;

public interface IDeviceEmulator
{
    string ProfileName { get; }

    string Banner { get; }

    bool Authenticate(string username, string password);

    string Prompt { get; }

    string CurrentDirectory { get; }

    Task<string?> ExecuteCommandAsync(string command,
        CancellationToken cancellationToken,
        Action<PayloadDownloadAttempt>? recordDownloadAttempt = null);
}

public interface IDeviceProfileFactory
{
    IDeviceEmulator Create(string profileName);
}
