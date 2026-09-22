using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Neo.Collab;

public enum ConnectionCodeKind : byte { Invite = 1, Reply = 2 }

public enum DtlsSetup : byte { ActPass = 0, Active = 1, Passive = 2 }

public enum CandidateType : byte { Host = 0, ServerReflexive = 1, PeerReflexive = 2, Relay = 3 }

/// <summary>An address the other side can try to reach this one at.</summary>
public sealed record Candidate(CandidateType Type, IPAddress Address, ushort Port);

/// <summary>
/// What one side tells the other so that the two can connect directly: its ICE credentials, the
/// fingerprint of its DTLS certificate (which authenticates it — only the holder of the matching
/// key can complete the handshake) and the addresses to try. Travels as a short text code: the
/// invitation from the host, the reply from the guest. No server is involved in the exchange;
/// people send the codes to each other however they like.
///
/// <para>Format: <c>NEO1</c> + base64url of a compact binary record with a 4-byte checksum, about
/// 170 characters. Whitespace and line breaks in pasted codes are ignored.</para>
/// </summary>
public sealed record ConnectionCode(
    ConnectionCodeKind Kind,
    uint SessionId,
    string IceUser,
    string IcePassword,
    byte[] Fingerprint,
    DtlsSetup Setup,
    IReadOnlyList<Candidate> Candidates)
{
    public const string Prefix = "NEO1";
    private const int FingerprintLength = 32; // SHA-256

    public string Encode()
    {
        if (Fingerprint.Length != FingerprintLength) throw new InvalidOperationException("Only SHA-256 DTLS fingerprints are supported.");
        var buffer = new List<byte> { (byte)Kind };
        Span<byte> id = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(id, SessionId);
        buffer.AddRange(id.ToArray());
        AddString(buffer, IceUser);
        AddString(buffer, IcePassword);
        buffer.AddRange(Fingerprint);
        buffer.Add((byte)Setup);
        buffer.Add((byte)Candidates.Count);
        foreach (var c in Candidates)
        {
            bool v6 = c.Address.AddressFamily == AddressFamily.InterNetworkV6;
            buffer.Add((byte)((byte)c.Type | (v6 ? 0x04 : 0)));
            buffer.AddRange(c.Address.GetAddressBytes());
            buffer.Add((byte)(c.Port >> 8));
            buffer.Add((byte)c.Port);
        }
        var checksum = SHA256.HashData(buffer.ToArray()).AsSpan(0, 4).ToArray();
        buffer.AddRange(checksum);
        return Prefix + Base64Url(buffer.ToArray());
    }

    /// <summary>Reads a code; throws <see cref="FormatException"/> with a message a user understands.</summary>
    public static ConnectionCode Decode(string text)
    {
        var code = Regex.Replace(text ?? "", @"\s+", "");
        if (code.Length == 0) throw new FormatException("The code is empty.");
        if (!code.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException("This is not a N.E.O. session code (it should start with " + Prefix + ").");
        byte[] data;
        try { data = FromBase64Url(code[Prefix.Length..]); }
        catch (FormatException) { throw new FormatException("The code is damaged — copy it again, completely."); }
        if (data.Length < 1 + 4 + 2 + FingerprintLength + 2 + 4)
            throw new FormatException("The code is incomplete — copy it again, completely.");

        var body = data.AsSpan(0, data.Length - 4);
        if (!SHA256.HashData(body).AsSpan(0, 4).SequenceEqual(data.AsSpan(data.Length - 4)))
            throw new FormatException("The code is damaged — copy it again, completely.");

        int i = 0;
        var kind = (ConnectionCodeKind)body[i++];
        if (kind is not (ConnectionCodeKind.Invite or ConnectionCodeKind.Reply))
            throw new FormatException("This code comes from a different N.E.O. version.");
        uint sessionId = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(i, 4)); i += 4;
        string user = ReadString(body, ref i);
        string password = ReadString(body, ref i);
        var fingerprint = body.Slice(i, FingerprintLength).ToArray(); i += FingerprintLength;
        var setup = (DtlsSetup)body[i++];
        int count = body[i++];
        var candidates = new List<Candidate>(count);
        for (int n = 0; n < count; n++)
        {
            byte flags = body[i++];
            bool v6 = (flags & 0x04) != 0;
            int len = v6 ? 16 : 4;
            var address = new IPAddress(body.Slice(i, len)); i += len;
            ushort port = (ushort)((body[i] << 8) | body[i + 1]); i += 2;
            candidates.Add(new Candidate((CandidateType)(flags & 0x03), address, port));
        }
        return new ConnectionCode(kind, sessionId, user, password, fingerprint, setup, candidates);
    }

    /// <summary>A session description the WebRTC stack accepts as the other side's offer or answer.</summary>
    public string ToSdp()
    {
        var setup = Setup switch { DtlsSetup.Active => "active", DtlsSetup.Passive => "passive", _ => "actpass" };
        var sb = new StringBuilder();
        sb.Append("v=0\r\n");
        sb.Append($"o=- {SessionId} 0 IN IP4 127.0.0.1\r\n");
        sb.Append("s=sipsorcery\r\n");
        sb.Append("t=0 0\r\n");
        sb.Append("a=group:BUNDLE 0\r\n");
        sb.Append("m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n");
        sb.Append("c=IN IP4 0.0.0.0\r\n");
        sb.Append($"a=ice-ufrag:{IceUser}\r\n");
        sb.Append($"a=ice-pwd:{IcePassword}\r\n");
        sb.Append($"a=fingerprint:sha-256 {string.Join(":", Fingerprint.Select(b => b.ToString("X2")))}\r\n");
        sb.Append($"a=setup:{setup}\r\n");
        sb.Append("a=mid:0\r\n");
        sb.Append("a=ice-options:ice2,trickle\r\n");
        sb.Append("a=sctp-port:5000\r\n");
        sb.Append("a=max-message-size:262144\r\n");
        return sb.ToString();
    }

    /// <summary>The candidate lines for <c>addIceCandidate</c>, in ICE's text form.</summary>
    public IEnumerable<string> CandidateLines()
    {
        int index = 0;
        foreach (var c in Candidates)
        {
            int typePreference = c.Type switch
            {
                CandidateType.Host => 126,
                CandidateType.PeerReflexive => 110,
                CandidateType.ServerReflexive => 100,
                _ => 0,
            };
            uint priority = (uint)((typePreference << 24) | ((65535 - index) << 8) | 255);
            var type = c.Type switch
            {
                CandidateType.ServerReflexive => "srflx raddr 0.0.0.0 rport 0",
                CandidateType.PeerReflexive => "prflx raddr 0.0.0.0 rport 0",
                CandidateType.Relay => "relay raddr 0.0.0.0 rport 0",
                _ => "host",
            };
            yield return $"candidate:{index + 1} 1 udp {priority} {c.Address} {c.Port} typ {type} generation 0";
            index++;
        }
    }

    /// <summary>Picks the fields a code carries out of a local session description.</summary>
    public static ConnectionCode FromSdp(ConnectionCodeKind kind, uint sessionId, string sdp, IEnumerable<Candidate> candidates)
    {
        string Attribute(string name)
        {
            var m = Regex.Match(sdp, $@"^a={name}:(.+?)\s*$", RegexOptions.Multiline);
            if (!m.Success) throw new InvalidOperationException($"The session description has no '{name}'.");
            return m.Groups[1].Value.Trim();
        }
        var fp = Attribute("fingerprint");
        if (!fp.StartsWith("sha-256 ", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only SHA-256 DTLS fingerprints are supported: " + fp);
        var fingerprint = Convert.FromHexString(fp[8..].Replace(":", ""));
        var setup = Attribute("setup").ToLowerInvariant() switch
        {
            "active" => DtlsSetup.Active,
            "passive" => DtlsSetup.Passive,
            _ => DtlsSetup.ActPass,
        };
        return new ConnectionCode(kind, sessionId, Attribute("ice-ufrag"), Attribute("ice-pwd"), fingerprint, setup, candidates.ToList());
    }

    private static void AddString(List<byte> buffer, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        if (bytes.Length > 255) throw new InvalidOperationException("Value too long for a session code.");
        buffer.Add((byte)bytes.Length);
        buffer.AddRange(bytes);
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int i)
    {
        int len = data[i++];
        var s = Encoding.ASCII.GetString(data.Slice(i, len));
        i += len;
        return s;
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
        return Convert.FromBase64String(b);
    }
}
