using Code2Create.UnitySandbox.Server;

string listenUrl = "http://127.0.0.1:5080";
string? webRoot = null;
string tcpHost = "127.0.0.1";
int tcpPort = Code2Create.UnitySandbox.World.WorldConstants.DefaultTcpPort;
string? explicitServerVersion = null;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--url" && i + 1 < args.Length)
    {
        listenUrl = args[++i];
    }
    else if (args[i] == "--web-root" && i + 1 < args.Length)
    {
        webRoot = args[++i];
    }
    else if (args[i] == "--tcp-host" && i + 1 < args.Length)
    {
        tcpHost = args[++i];
    }
    else if (args[i] == "--tcp-port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedPort))
    {
        tcpPort = parsedPort;
        i++;
    }
    else if (args[i] == "--server-version" && i + 1 < args.Length)
    {
        explicitServerVersion = args[++i];
    }
}

var app = SandboxServerApp.Build(new SandboxServerOptions
{
    ListenUrl = listenUrl,
    WebRootPath = webRoot,
    TcpListenHost = tcpHost,
    TcpListenPort = tcpPort,
    ServerVersion = Code2Create.UnitySandbox.World.WorldConstants.ResolveServerVersion(explicitServerVersion)
});

await app.RunAsync();
