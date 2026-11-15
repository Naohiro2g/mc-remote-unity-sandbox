using System.Net;
using Code2Create.UnitySandbox.Server.Sync;
using Code2Create.UnitySandbox.Server.Tcp;
using Code2Create.UnitySandbox.World;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Code2Create.UnitySandbox.Server;

public sealed class SandboxServerOptions
{
    public string ListenUrl { get; init; } = "http://127.0.0.1:5080";
    public string? WebRootPath { get; init; }
    public Func<DateTimeOffset>? Clock { get; init; }
    public string TcpListenHost { get; init; } = "127.0.0.1";
    public int TcpListenPort { get; init; } = 0;
    public bool AuthEnforcement { get; init; } = true;
    public string ServerName { get; init; } = WorldConstants.DefaultServerName;
    public string ServerVersion { get; init; } = WorldConstants.ResolveServerVersion();
}

public static class SandboxServerApp
{
    public static WebApplication Build(SandboxServerOptions? options = null)
    {
        options ??= new SandboxServerOptions();
        if (!IPAddress.TryParse(options.TcpListenHost, out var tcpAddress))
        {
            throw new ArgumentException($"Invalid TcpListenHost: '{options.TcpListenHost}'.", nameof(options));
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(options.ListenUrl);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        builder.Services.AddSingleton<SandboxWorld>(_ => new SandboxWorld());
        builder.Services.AddSingleton<IViewerStreamBindingStore, InMemoryViewerStreamBindingStore>();
        builder.Services.AddSingleton<ISandboxTokenStore, InMemorySandboxTokenStore>();
        builder.Services.AddSingleton<IPairingApprovalStore, InMemoryPairingApprovalStore>();
        builder.Services.AddSingleton(_ => new PairingRateLimiter());
        builder.Services.AddSingleton(sp => new DisplaySyncHub(
            sp.GetRequiredService<SandboxWorld>(),
            sp.GetRequiredService<IViewerStreamBindingStore>(),
            sp.GetRequiredService<IPairingApprovalStore>(),
            sp.GetRequiredService<PairingRateLimiter>(),
            options.Clock,
            sp.GetRequiredService<ISandboxTokenStore>()));
        builder.Services.AddSingleton(sp => new TcpJsonRpcServer(
            sp.GetRequiredService<SandboxWorld>(),
            sp.GetRequiredService<IViewerStreamBindingStore>(),
            sp.GetRequiredService<IPairingApprovalStore>(),
            sp.GetRequiredService<ISandboxTokenStore>(),
            sp.GetRequiredService<DisplaySyncHub>(),
            tcpAddress,
            options.TcpListenPort,
            options.Clock,
            options.AuthEnforcement,
            options.ServerName,
            serverVersion: options.ServerVersion));

        var app = builder.Build();
        var tcpServer = app.Services.GetRequiredService<TcpJsonRpcServer>();
        app.Lifetime.ApplicationStarted.Register(() => tcpServer.Start());
        app.Lifetime.ApplicationStopping.Register(() => tcpServer.StopAsync().GetAwaiter().GetResult());

        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });

        app.MapGet("/healthz", (SandboxWorld world, DisplaySyncHub hub, TcpJsonRpcServer tcp) => Results.Json(new
        {
            status = "ok",
            version = options.ServerVersion,
            protocol = WorldConstants.ProtocolVersion,
            worldEpoch = world.WorldEpoch,
            seq = world.Seq,
            connections = hub.ActiveConnectionCount,
            tcpPort = tcp.BoundPort
        }));

        app.Map("/ws/sync", async (HttpContext context, DisplaySyncHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            string? viewerId = context.Request.Query["viewerId"].FirstOrDefault();
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await hub.HandleWebSocketAsync(socket, context.RequestAborted, viewerId);
        });

        string? webRoot = ResolveWebRoot(options.WebRootPath);
        if (webRoot != null)
        {
            var provider = new PhysicalFileProvider(webRoot);
            var contentTypes = CreateWebGlContentTypeProvider();

            app.UseDefaultFiles(new DefaultFilesOptions
            {
                FileProvider = provider
            });

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = provider,
                ContentTypeProvider = contentTypes,
                ServeUnknownFileTypes = true,
                OnPrepareResponse = ctx =>
                {
                    string name = ctx.File.Name;
                    if (name.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Context.Response.Headers["Content-Encoding"] = "br";
                    }
                    else if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Context.Response.Headers["Content-Encoding"] = "gzip";
                    }
                }
            });
        }

        return app;
    }

    private static string? ResolveWebRoot(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string full = Path.GetFullPath(explicitPath);
            return Directory.Exists(full) ? full : null;
        }

        string defaultWwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        return Directory.Exists(defaultWwwroot) ? defaultWwwroot : null;
    }

    private static FileExtensionContentTypeProvider CreateWebGlContentTypeProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".wasm"] = "application/wasm";
        provider.Mappings[".data"] = "application/octet-stream";
        provider.Mappings[".unityweb"] = "application/octet-stream";
        provider.Mappings[".wasm.br"] = "application/wasm";
        provider.Mappings[".wasm.gz"] = "application/wasm";
        provider.Mappings[".js.br"] = "application/javascript";
        provider.Mappings[".js.gz"] = "application/javascript";
        provider.Mappings[".data.br"] = "application/octet-stream";
        provider.Mappings[".data.gz"] = "application/octet-stream";
        return provider;
    }
}
