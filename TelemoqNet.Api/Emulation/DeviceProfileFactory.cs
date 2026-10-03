using Microsoft.Extensions.DependencyInjection;

namespace TelemoqNet.Api.Emulation;

public sealed class DeviceProfileFactory(IServiceProvider services) : IDeviceProfileFactory
{
    public IDeviceEmulator Create(string profileName) =>
        profileName.Equals("GenericLinux", StringComparison.OrdinalIgnoreCase)
            ? ActivatorUtilities.CreateInstance<GenericLinuxDevice>(services)
            : throw new InvalidOperationException(
                $"Unknown Honeypot:Device profile '{profileName}'. Supported profiles: GenericLinux.");
}
