using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;

namespace ActivityTracker.Core;

public enum SyncErrorKind
{
    /// <summary>URL non <c>https://</c> (o <c>http://</c> fuori da localhost / senza <c>AT_ALLOW_INSECURE_LOCALHOST=1</c>).</summary>
    InsecureUrl,
    InvalidUrl,
    /// <summary>401 dalla rotta: token mancante, sbagliato o revocato, o persona disattivata.</summary>
    Unauthorized,
    /// <summary>401 dal gate di perimetro: cookie <c>et_gate</c> assente o diverso da <c>ACCESS_SECRET</c>.</summary>
    Gate,
    Forbidden,
    /// <summary>400 / 404 / 413: richiesta rifiutata dal server, con il messaggio del server.</summary>
    BadRequest,
    /// <summary>422: JSON valido ma invio non accettabile, con l'indice del primo tratto sbagliato.</summary>
    Unprocessable,
    RateLimited,
    /// <summary>5xx: da ritentare.</summary>
    Server,
    /// <summary>Errore di rete / timeout.</summary>
    Network,
    /// <summary>Risposta 2xx con JSON non conforme.</summary>
    Decoding,
}

/// <summary>Errore del client verso equipe-track, confrontabile per valore.</summary>
public sealed record SyncFault(SyncErrorKind Kind, string? Detail = null, int? Index = null, int? Status = null)
{
    /// <summary>True per gli errori transitori (rete, 5xx, 429): si ritenta al giro successivo.</summary>
    public bool IsRetryable => Kind is SyncErrorKind.Network or SyncErrorKind.Server or SyncErrorKind.RateLimited;
}

public sealed class SyncException(SyncFault fault) : Exception(fault.Detail ?? fault.Kind.ToString())
{
    public SyncFault Fault { get; } = fault;
}

/// <summary>Trasporto HTTP iniettabile (nelle verifiche: un finto in memoria o il server finto).</summary>
public interface IHttpTransport
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
}

/// <summary>Trasporto reale (timeout 20 s per richiesta, nessun cookie salvato: il cookie del gate viaggia solo come
/// intestazione esplicita).</summary>
public sealed class HttpClientTransport : IHttpTransport
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _client;

    public HttpClientTransport()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = RequestTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _client = new HttpClient(handler) { Timeout = RequestTimeout };
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) =>
        _client.SendAsync(request, cancellationToken);
}

/// <summary>
/// Client delle tre rotte dell'agente su equipe-track:
/// <code>
///   GET  /api/agente/sessione   c'è una sessione aperta? si traccia?
///   POST /api/agente/segmenti   i tratti di una giornata (sostituisce la giornata)
///   POST /api/agente/stop       chiude la sessione aperta
/// </code>
/// Ogni richiesta porta <c>Authorization: Bearer &lt;token&gt;</c> e il cookie <c>et_gate=&lt;ACCESS_SECRET&gt;</c>.
/// Token e segreto non vengono mai scritti nei log.
/// </summary>
public sealed class SyncClient
{
    public const string GateCookieName = "et_gate";
    public const string VersionHeader = "X-Agente-Versione";
    public const string InsecureLocalhostFlag = "AT_ALLOW_INSECURE_LOCALHOST";

    public Uri BaseUrl { get; }
    public string Token { get; }
    /// <summary>Valore del cookie del gate; null o vuoto = nessun cookie.</summary>
    public string? GateSecret { get; }
    public string AgentVersion { get; }
    public IHttpTransport Transport { get; }

    public SyncClient(Uri baseUrl, string token, string? gateSecret, string agentVersion = AppIdentity.AgentVersion,
                      IHttpTransport? transport = null)
    {
        BaseUrl = baseUrl;
        Token = token;
        GateSecret = gateSecret;
        AgentVersion = agentVersion;
        Transport = transport ?? new HttpClientTransport();
    }

    // Validazione

    private static readonly HashSet<string> LocalhostHosts = ["127.0.0.1", "localhost", "::1", "[::1]"];

    /// <summary><c>AT_ALLOW_INSECURE_LOCALHOST=1</c> nell'ambiente dato (default: quello del processo).</summary>
    public static bool EnvironmentAllowsInsecureLocalhost(IDictionary<string, string>? env = null)
    {
        var v = env is null ? Environment.GetEnvironmentVariable(InsecureLocalhostFlag)
                            : env.TryGetValue(InsecureLocalhostFlag, out var x) ? x : null;
        return v is not null && !(v.Length == 0 || v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Accetta solo <c>https://host[:porta][/percorso]</c>. <c>http://</c> è ammesso SOLO per 127.0.0.1 /
    /// localhost e SOLO con <paramref name="allowInsecureLocalhost"/> (prove locali). Niente credenziali, query o
    /// frammento nell'URL. Restituisce l'URL normalizzato o l'errore.</summary>
    public static (Uri? Url, SyncFault? Error) Validate(string urlString, bool allowInsecureLocalhost)
    {
        var trimmed = urlString.Trim();
        if (trimmed.Length == 0 || !trimmed.Contains("://", StringComparison.Ordinal)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return (null, new SyncFault(SyncErrorKind.InvalidUrl));
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 1 || uri.Fragment.Length > 1)
            return (null, new SyncFault(SyncErrorKind.InvalidUrl));
        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        switch (scheme)
        {
            case "https":
                break;
            case "http":
                if (!allowInsecureLocalhost || !LocalhostHosts.Contains(host)) return (null, new SyncFault(SyncErrorKind.InsecureUrl));
                break;
            default:
                return (null, new SyncFault(SyncErrorKind.InsecureUrl));
        }
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        var path = uri.AbsolutePath.TrimEnd('/');
        return Uri.TryCreate($"{scheme}://{uri.Host}{port}{path}", UriKind.Absolute, out var normalized)
            ? (normalized, null)
            : (null, new SyncFault(SyncErrorKind.InvalidUrl));
    }

    /// <summary>Forma testuale dell'URL normalizzato, senza barra finale (<c>Uri.ToString()</c> la aggiunge).</summary>
    public static string Text(Uri url) => url.GetLeftPart(UriPartial.Path).TrimEnd('/');

    /// <summary>Stessa forma che il server accetta (<c>tokenDaIntestazione</c> in <c>domain/agente.ts</c>).</summary>
    public static bool IsWellFormedToken(string token)
    {
        var t = token.Trim();
        return t.Length is >= 20 and <= 200 && t.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
    }

    /// <summary>Il segreto nel cookie, codificato come lo decodifica Next (<c>decodeURIComponent</c>): solo
    /// <c>A–Z a–z 0–9 - . _ ~</c> restano in chiaro, il resto in <c>%XX</c>.</summary>
    public static string GateCookieValue(string secret) => Uri.EscapeDataString(secret);

    // Le tre rotte

    /// <summary><c>GET /api/agente/sessione</c>, con gli istanti di invio e di ricezione (per lo scarto d'orologio).</summary>
    public async Task<(RispostaSessione Risposta, DateTimeOffset SentAt, DateTimeOffset ReceivedAt)> SessioneAsync(CancellationToken ct = default)
    {
        using var request = MakeRequest("/api/agente/sessione", HttpMethod.Get, null);
        var sentAt = DateTimeOffset.UtcNow;
        var r = await PerformAsync<RispostaSessione>(request, ct).ConfigureAwait(false);
        return (r, sentAt, DateTimeOffset.UtcNow);
    }

    /// <summary><c>POST /api/agente/segmenti</c>: i tratti di una giornata, che sostituiscono quelli già inviati.</summary>
    public async Task<RispostaSegmenti> InviaSegmentiAsync(InvioSegmenti invio, CancellationToken ct = default)
    {
        using var request = MakeRequest("/api/agente/segmenti", HttpMethod.Post, SyncJson.Encode(invio));
        return await PerformAsync<RispostaSegmenti>(request, ct).ConfigureAwait(false);
    }

    /// <summary><c>POST /api/agente/stop</c>: chiude la sessione aperta. <paramref name="alle"/> null = adesso.</summary>
    public async Task<RispostaStop> StopAsync(DateTimeOffset? alle, CancellationToken ct = default)
    {
        var body = new StopRichiesta { Alle = alle is { } a ? SyncJson.Instant(a) : null };
        using var request = MakeRequest("/api/agente/stop", HttpMethod.Post, SyncJson.Encode(body));
        return await PerformAsync<RispostaStop>(request, ct).ConfigureAwait(false);
    }

    // Interni

    private HttpRequestMessage MakeRequest(string path, HttpMethod method, byte[]? body)
    {
        var request = new HttpRequestMessage(method, new Uri(Text(BaseUrl) + path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token.Trim());
        if (!string.IsNullOrEmpty(GateSecret))
            request.Headers.TryAddWithoutValidation("Cookie", $"{GateCookieName}={GateCookieValue(GateSecret)}");
        request.Headers.TryAddWithoutValidation(VersionHeader, AgentVersion);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        return request;
    }

    private async Task<T> PerformAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        byte[] data;
        try
        {
            response = await Transport.SendAsync(request, ct).ConfigureAwait(false);
            data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (SyncException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new SyncException(new SyncFault(SyncErrorKind.Network, Describe(e)));
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            switch (status)
            {
                case >= 200 and < 300:
                    try { return SyncJson.Decode<T>(data); }
                    catch (Exception) { throw new SyncException(new SyncFault(SyncErrorKind.Decoding)); }
                case 401:
                    // La rotta risponde JSON con `WWW-Authenticate: Bearer`; il gate risponde testo semplice.
                    var bearer = response.Headers.TryGetValues("WWW-Authenticate", out var values)
                        && values.Any(v => v.TrimStart().StartsWith("bearer", StringComparison.OrdinalIgnoreCase));
                    throw new SyncException(new SyncFault(bearer ? SyncErrorKind.Unauthorized : SyncErrorKind.Gate));
                case 422:
                    var body = TryError(data);
                    throw new SyncException(new SyncFault(SyncErrorKind.Unprocessable, body?.Errore ?? "invio non accettato (422)", body?.Indice));
                case 400 or 404 or 413:
                    throw new SyncException(new SyncFault(SyncErrorKind.BadRequest, ServerMessage(data, $"richiesta rifiutata ({status})")));
                case 403:
                    throw new SyncException(new SyncFault(SyncErrorKind.Forbidden));
                case 429:
                    throw new SyncException(new SyncFault(SyncErrorKind.RateLimited));
                case >= 500 and < 600:
                    throw new SyncException(new SyncFault(SyncErrorKind.Server, Status: status));
                default:
                    throw new SyncException(new SyncFault(SyncErrorKind.BadRequest, ServerMessage(data, $"risposta inattesa ({status})")));
            }
        }
    }

    private static RispostaErrore? TryError(byte[] data)
    {
        try { return SyncJson.Decode<RispostaErrore>(data); }
        catch (Exception) { return null; }
    }

    private static string ServerMessage(byte[] data, string fallback) =>
        TryError(data) is { Errore.Length: > 0 } e ? e.Errore : fallback;

    /// <summary>Descrizione breve in italiano di un errore di rete.</summary>
    public static string Describe(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case TaskCanceledException or TimeoutException: return "tempo scaduto";
                case AuthenticationException: return "certificato TLS non valido";
                case SocketException s:
                    return s.SocketErrorCode switch
                    {
                        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "server non trovato",
                        SocketError.ConnectionRefused => "connessione rifiutata",
                        SocketError.ConnectionReset or SocketError.ConnectionAborted => "connessione interrotta",
                        SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.HostUnreachable => "nessuna connessione",
                        SocketError.TimedOut => "tempo scaduto",
                        _ => s.Message,
                    };
            }
        }
        return error.Message;
    }
}
