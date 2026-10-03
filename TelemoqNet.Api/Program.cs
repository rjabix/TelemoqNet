using TelemoqNet.Api;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Emulation;
using TelemoqNet.Api.Logging;
using TelemoqNet.Api.Telnet;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<HoneypotOptions>()
    .Bind(builder.Configuration.GetSection("Honeypot"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<HoneypotOptions>, HoneypotOptionsValidator>();

builder.Services.AddSingleton<IDeviceProfileFactory, DeviceProfileFactory>();

builder.Services.AddLoggingServices(builder.Environment);

builder.Services.AddSingleton<TelnetServer>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();

await host.RunAsync();