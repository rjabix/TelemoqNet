using TelemoqNet.Api;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Emulation;
using TelemoqNet.Api.Logging;
using TelemoqNet.Api.Telnet;
using TelemoqNet.Api.Mqtt;
using TelemoqNet.Api.Mqtt.Broker;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<HoneypotOptions>()
    .Bind(builder.Configuration.GetSection("Honeypot"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<HoneypotOptions>, HoneypotOptionsValidator>();
builder.Services.AddOptions<MqttOptions>()
    .Bind(builder.Configuration.GetSection("Honeypot:Mqtt"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<MqttOptions>, MqttOptionsValidator>();

builder.Services.AddSingleton<IDeviceProfileFactory, DeviceProfileFactory>();

builder.Services.AddLoggingServices(builder.Environment);

builder.Services.AddSingleton<TelnetServer>();
builder.Services.AddSingleton<BrokerHub>();
builder.Services.AddSingleton<MqttServer>();
builder.Services.AddSingleton<IProtocolServer>(sp => sp.GetRequiredService<TelnetServer>());
builder.Services.AddSingleton<IProtocolServer>(sp => sp.GetRequiredService<MqttServer>());

builder.Services.AddHostedService<Worker>();

var host = builder.Build();

await host.RunAsync();