using StackScope.Coordinator;
using StackScope.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure the project storage location. Precedence: --project-root
// arg > STACKSCOPE_PROJECT_ROOT env > default local-app-data path.
string projectRoot = args
    .Zip(args.Skip(1), (a, b) => (a, b))
    .Where(p => p.a == "--project-root")
    .Select(p => p.b)
    .FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("STACKSCOPE_PROJECT_ROOT")
    ?? Path.Combine(Environment.GetFolderPath(
           Environment.SpecialFolder.LocalApplicationData),
           "StackScope", "default");

var project = new ProjectService(projectRoot);
var query   = new QueryService(project);
builder.Services.AddSingleton(project);
builder.Services.AddSingleton(query);
builder.Services.AddSingleton<StackScope.Coordinator.WorkerLauncher>();

builder.Services.AddGrpc(o =>
{
    o.MaxSendMessageSize    = 128 * 1024 * 1024;
    o.MaxReceiveMessageSize = 128 * 1024 * 1024;
});

builder.WebHost.ConfigureKestrel(o =>
{
    var endpoint = Environment.GetEnvironmentVariable("STACKSCOPE_COORDINATOR_ENDPOINT")
                   ?? "127.0.0.1:50500";
    var parts = endpoint.Split(':');
    o.ListenAnyIP(int.Parse(parts[1]),
        listen => listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});

var app = builder.Build();
app.MapGrpcService<CoordinatorService>();
app.MapGet("/", () => $"StackScope Coordinator up. Project root: {projectRoot}");

app.Logger.LogInformation("StackScope Coordinator started. Project root: {Root}", projectRoot);
app.Run();
