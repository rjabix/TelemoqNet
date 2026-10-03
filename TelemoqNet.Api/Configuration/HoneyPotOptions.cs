namespace TelemoqNet.Api.Configuration;

public sealed class HoneypotOptions
{
    public int TelnetPort { get; set; } = 2323;

    public int MaxConnections { get; set; } = 100;

    public int SessionTimeoutSeconds { get; set; } = 300;

    public int MaxLineLength { get; set; } = 2048;

    public int MaxPacketSize { get; set; } = 8192;

    public int MaxResponseLength { get; set; } = 16384;

    public int CommandLatencyMilliseconds { get; set; } = 25;

    public int AuthenticationRetryDelayMilliseconds { get; set; } = 250;

    public int MaxAuthenticationAttempts { get; set; } = 3;

    public bool CloseOnAuthenticationFailure { get; set; } = true;

    public bool CapturePasswords { get; set; }

    public string Device { get; set; } = "GenericLinux";

    public string Banner { get; set; } =
        "BusyBox v1.19.4 (2014-06-12 08:23:17 UTC)";

    public string Hostname { get; set; } = "router";

    public string[] Credentials { get; set; } =
        ["admin:admin", "root:root", "root:123456", "admin:1234"];
}

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