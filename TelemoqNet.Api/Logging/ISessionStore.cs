namespace TelemoqNet.Api.Logging;

public interface ISessionStore
{
    Task StoreAsync(
        HoneypotSession session,
        CancellationToken cancellationToken);
}