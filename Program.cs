using Upscale.Connectors.Onvif;
using Quantum.DevKit.Connection;

var client = new Client("https://192.168.x.x", "tenantId");
client.LoginDev("Admin", @"admin", "Onvif", true).Wait();

using var connector = new Connector();
connector.InitAsync(client).Wait();

var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

connector.RunAsync(cts.Token);
