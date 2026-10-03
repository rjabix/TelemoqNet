namespace TelemoqNet.Api;

public interface IProtocolServer
{
    string Name { get; }
    Task RunAsync(CancellationToken cancellationToken);
}
