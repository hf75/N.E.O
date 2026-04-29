using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Neo.App.Mcp.Internal;

/// <summary>
/// Frozen-Mode equivalent of <c>LiveMcpDynamicResource</c>: one MCP resource per
/// <c>[McpObservable(Watchable = true)]</c> property. URI is <c>app://&lt;name&gt;</c>;
/// <see cref="ReadAsync"/> returns the cached JSON value held by
/// <see cref="ObservableSubscriptions"/>.
/// </summary>
internal sealed class DynamicResource : McpServerResource
{
    private readonly string _uri;
    private readonly Resource _protocolResource;
    private readonly ResourceTemplate _protocolTemplate;
    private readonly ObservableSubscriptions _subs;
    private readonly string _observableName;

    public DynamicResource(ObservableEntry entry, ObservableSubscriptions subs)
    {
        _observableName = entry.Name;
        _uri = Naming.BuildResourceUri(entry.Name);
        _subs = subs;

        _protocolResource = new Resource
        {
            Uri = _uri,
            Name = entry.Name,
            Description = entry.Description,
            MimeType = "application/json"
        };
        _protocolTemplate = new ResourceTemplate
        {
            UriTemplate = _uri,
            Name = entry.Name,
            Description = entry.Description,
            MimeType = "application/json"
        };
    }

    public override Resource ProtocolResource => _protocolResource;
    public override ResourceTemplate ProtocolResourceTemplate => _protocolTemplate;
    public override IReadOnlyList<object> Metadata { get; } = Array.Empty<object>();

    public override bool IsMatch(string uri) => string.Equals(uri, _uri, StringComparison.Ordinal);

    public override ValueTask<ReadResourceResult> ReadAsync(
        RequestContext<ReadResourceRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var json = _subs.GetCachedJson(_observableName);
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
