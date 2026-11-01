namespace TelemoqNet.Api.Configuration;

public sealed class HoneypotOptionsValidator : Microsoft.Extensions.Options.IValidateOptions<HoneypotOptions>
{
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, HoneypotOptions options)
    {
        var errors = new List<string>();
        if (options.TelnetPort is < 1 or > 65535) errors.Add("Honeypot:TelnetPort must be between 1 and 65535.");
        if (options.MaxConnections < 1) errors.Add("Honeypot:MaxConnections must be positive.");
        if (options.SessionTimeoutSeconds < 1) errors.Add("Honeypot:SessionTimeoutSeconds must be positive.");
        if (options.MaxLineLength is < 1 or > 1_048_576) errors.Add("Honeypot:MaxLineLength must be between 1 and 1048576.");
        if (options.MaxPacketSize < options.MaxLineLength || options.MaxPacketSize > 4 * 1024 * 1024)
            errors.Add("Honeypot:MaxPacketSize must be at least MaxLineLength and no greater than 4194304.");
        if (options.MaxResponseLength < 1 || options.MaxResponseLength > 4 * 1024 * 1024)
            errors.Add("Honeypot:MaxResponseLength must be between 1 and 4194304.");
        if (options.CommandLatencyMilliseconds is < 0 or > 60_000)
            errors.Add("Honeypot:CommandLatencyMilliseconds must be between 0 and 60000.");
        if (options.AuthenticationRetryDelayMilliseconds is < 0 or > 60_000)
            errors.Add("Honeypot:AuthenticationRetryDelayMilliseconds must be between 0 and 60000.");
        if (options.MaxAuthenticationAttempts is < 1 or > 100)
            errors.Add("Honeypot:MaxAuthenticationAttempts must be between 1 and 100.");
        if (string.IsNullOrWhiteSpace(options.Device)) errors.Add("Honeypot:Device is required.");
        if (options.Credentials.Any(c => string.IsNullOrWhiteSpace(c) || !c.Contains(':')))
            errors.Add("Honeypot:Credentials entries must use username:password format.");
        return errors.Count == 0
            ? Microsoft.Extensions.Options.ValidateOptionsResult.Success
            : Microsoft.Extensions.Options.ValidateOptionsResult.Fail(errors);
    }
}
