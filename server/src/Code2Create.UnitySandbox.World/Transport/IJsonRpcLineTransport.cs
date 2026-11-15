using System.Threading;
using System.Threading.Tasks;

namespace Code2Create.UnitySandbox.World.Transport
{
    /// <summary>
    /// Line-delimited JSON-RPC transport abstraction (keeps TCP / WebSocket / in-memory pipes decoupled from world logic).
    /// </summary>
    public interface IJsonRpcLineTransport
    {
        Task<string?> ReadLineAsync(CancellationToken cancellationToken);
        Task WriteLineAsync(string line, CancellationToken cancellationToken);
    }
}
