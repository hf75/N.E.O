using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Neo.McpServer.LiveMcp;

/// <summary>
/// One MCP resource fronting a single <c>[McpObservable(Watchable = true)]</c> property on a
/// running preview app. URI follows VISION.md §8: <c>app://&lt;windowId&gt;/&lt;propertyName&gt;</c>.
///
/// <para>The resource itself is stateless about value caching — it asks the registry for the
/// last-known value at <see cref="ReadAsync"/> time. That keeps the cache + coalesce logic in
/// one place (the registry) so we don't end up with split state.</para>
/// </summary>
internal sealed class LiveMcpDynamicResource : McpServerResource
{
    private readonly string _uri;
    private readonly Resource _protocolResource;
    private readonly ResourceTemplate _protocolTemplate;
    private readonly System.Func<string, string> _readCached;

    public LiveMcpDynamicResource(string uri, string name, string description, System.Func<string, string> readCached)
    {
        _uri = uri;
        _readCached = readCached;

        _protocolResource = new Resource
        {
            Uri = uri,
            Name = name,
            Description = description,
            MimeType = "application/json"
        };

        _protocolTemplate = new ResourceTemplate
        {
            UriTemplate = uri,
            Name = name,
            Description = description,
            MimeType = "application/json"
        };
    }

    public override Resource ProtocolResource => _protocolResource;
    public override ResourceTemplate ProtocolResourceTemplate => _protocolTemplate;
    public override IReadOnlyList<object> Metadata { get; } = System.Array.Empty<object>();

    public override bool IsMatch(string uri) => string.Equals(uri, _uri, System.StringComparison.Ordinal);

    public override ValueTask<ReadResourceResult> ReadAsync(
        RequestContext<ReadResourceRequestParams> request,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var json = _readCached(_uri);
        return ValueTask.FromResult(new ReadResourceResult
        {
            Contents = new List<ResourceContents>
            {
                new TextResourceContents
                {
                    Uri = _uri,
                    MimeType = "application/json",
                    Text = json
                }
            }
        });
    }
}
