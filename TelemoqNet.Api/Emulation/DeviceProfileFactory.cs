using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;

namespace TelemoqNet.Api.Emulation;

public sealed class DeviceProfileFactory(IOptions<HoneypotOptions> options) : IDeviceProfileFactory
{
    public IDeviceEmulator Create(string profileName) =>
        profileName.Equals("GenericLinux", StringComparison.OrdinalIgnoreCase)
            ? new GenericLinuxDevice(options)
            : throw new InvalidOperationException(
                $"Unknown Honeypot:Device profile '{profileName}'. Supported profiles: GenericLinux.");
}
