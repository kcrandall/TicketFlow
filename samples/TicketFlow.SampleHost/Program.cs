using TicketFlow.SampleHost;

// dotnet run -- seed   : applies every store migration and seeds the demo workspace, then exits.
// dotnet run           : runs the host (endpoints, SignalR hubs, workers, OpenAPI, /health, /demo).
var seed = args.Length > 0 && string.Equals(args[0], "seed", StringComparison.Ordinal);
var app = SampleHostApp.Build(seed ? args[1..] : args);
await SampleHostApp.InitializeAsync(app, seed);
if (!seed)
    await app.RunAsync();
