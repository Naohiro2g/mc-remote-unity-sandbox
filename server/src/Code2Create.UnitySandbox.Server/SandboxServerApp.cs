using System.Net;
using Code2Create.UnitySandbox.Server.Sync;
using Code2Create.UnitySandbox.Server.Tcp;
using Code2Create.UnitySandbox.World;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Code2Create.UnitySandbox.Server;

/// <summary>
/// Server-side connection, pairing, and frame-size resource limits aligned with McRemote (2026-10-07-02 / 2026-11-20-04).
/// </summary>
public sealed class SandboxConnectionLimits
{
    public int MaxTotalTcpConnections { get; init; } = 64;
    public int MaxPreHelloTcpConnections { get; init; } = 32;
    public int MaxPerIpTcpConnections { get; init; } = 32;
    public TimeSpan PreHelloIdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan PreHelloDeadline { get; init; } = TimeSpan.FromSeconds(180);
    public bool EnableTcpKeepAlive { get; init; } = true;
    public int TcpKeepAliveTimeSeconds { get; init; } = 60;
    public int TcpKeepAliveIntervalSeconds { get; init; } = 30;
    public int TcpKeepAliveRetryCount { get; init; } = 8;
    public int MaxPairBeginPerIpPerMinute { get; init; } = 20;
    public int MaxPairBeginGlobalPerSecond { get; init; } = 20;
    public int MaxPairBeginGlobalPerMinute { get; init; } = 120;
    public int MaxPendingPairCodes { get; init; } = 128;
    public TimeSpan ExpiredPairRetentionGrace { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxTotalPairEntries { get; init; } = 256;
    public int MaxTcpLineBytes { get; init; } = 65_536;
    public int MaxWebSocketMessageBytes { get; init; } = 65_536;
}

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
    public SandboxConnectionLimits Limits { get; init; } = new SandboxConnectionLimits();
}

public static class SandboxServerApp
{
    public static WebApplication Build(SandboxServerOptions? options = null)
    {
        options ??= new SandboxServerOptions();
        SandboxConnectionLimits limits = options.Limits ?? new SandboxConnectionLimits();
        if (!IPAddress.TryParse(options.TcpListenHost, out var tcpAddress))
        {
            throw new ArgumentException($"Invalid TcpListenHost: '{options.TcpListenHost}'.", nameof(options));
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(options.ListenUrl);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        builder.Services.AddSingleton(limits);
        builder.Services.AddSingleton<SandboxWorld>(_ => new SandboxWorld());
        builder.Services.AddSingleton<IViewerStreamBindingStore, InMemoryViewerStreamBindingStore>();
        builder.Services.AddSingleton<ISandboxTokenStore, InMemorySandboxTokenStore>();
        builder.Services.AddSingleton<IPairingApprovalStore>(_ => new InMemoryPairingApprovalStore(
            limits.MaxPendingPairCodes,
            limits.ExpiredPairRetentionGrace,
            limits.MaxTotalPairEntries));
        builder.Services.AddSingleton(_ => new PairingRateLimiter());
        builder.Services.AddSingleton(_ => new PairBeginRateLimiter(
            limits.MaxPairBeginPerIpPerMinute,
            limits.MaxPairBeginGlobalPerSecond,
            limits.MaxPairBeginGlobalPerMinute));
        builder.Services.AddSingleton(sp => new DisplaySyncHub(
            sp.GetRequiredService<SandboxWorld>(),
            sp.GetRequiredService<IViewerStreamBindingStore>(),
            sp.GetRequiredService<IPairingApprovalStore>(),
            sp.GetRequiredService<PairingRateLimiter>(),
            options.Clock,
            sp.GetRequiredService<ISandboxTokenStore>(),
            limits.MaxWebSocketMessageBytes));
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
            serverVersion: options.ServerVersion,
            limits: limits,
            pairBeginRateLimiter: sp.GetRequiredService<PairBeginRateLimiter>()));

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
