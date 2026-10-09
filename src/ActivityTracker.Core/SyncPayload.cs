using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ActivityTracker.Core;

// Tipi del contratto con equipe-track: `src/lib/domain/agente.ts` (forma dell'invio) e
// `src/app/api/agente/*/route.ts` (risposte). Chiavi JSON in italiano, camelCase, come nel contratto.
// Dal PC esce solo (sessione, nome aggregato, stato, durata, inizio): mai dominio, URL, titolo o eseguibile.

/// <summary>I quattro stati della misura (<c>STATI_ATTIVITA</c> in <c>enums.ts</c>).</summary>
[JsonConverter(typeof(StatoAttivitaConverter))]
public enum StatoAttivita
{
    Attivo,
    Passivo,
    SenzaUtilizzo,
    FuoriSessione,
}

public static class StatiAttivita
{
    public static string Raw(this StatoAttivita s) => s switch
    {
        StatoAttivita.Attivo => "attivo",
        StatoAttivita.Passivo => "passivo",
        StatoAttivita.SenzaUtilizzo => "senza_utilizzo",
        _ => "fuori_sessione",
    };

    public static StatoAttivita? Parse(string? raw) => raw switch
    {
        "attivo" => StatoAttivita.Attivo,
        "passivo" => StatoAttivita.Passivo,
        "senza_utilizzo" => StatoAttivita.SenzaUtilizzo,
        "fuori_sessione" => StatoAttivita.FuoriSessione,
        _ => null,
    };
}

public sealed class StatoAttivitaConverter : JsonConverter<StatoAttivita>
{
    public override StatoAttivita Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        StatiAttivita.Parse(reader.GetString()) ?? throw new JsonException("stato non valido");

    public override void Write(Utf8JsonWriter writer, StatoAttivita value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Raw());
}

/// <summary>Un tratto dell'invio. <c>sessione</c> e <c>nome</c> sono <c>null</c> (espliciti) solo su <c>fuori_sessione</c>.
/// <c>inizio</c> è un istante UTC con «Z», es. <c>2026-10-08T09:00:00.000Z</c>.</summary>
public sealed record SegmentoInvio(
    [property: JsonPropertyName("sessione")] string? Sessione,
    [property: JsonPropertyName("nome")] string? Nome,
    [property: JsonPropertyName("durataSec")] int DurataSec,
    [property: JsonPropertyName("stato")] StatoAttivita Stato,
    [property: JsonPropertyName("inizio")] string Inizio);

/// <summary>Corpo di <c>POST /api/agente/segmenti</c>: la giornata intera, che sostituisce quella già inviata.</summary>
public sealed record InvioSegmenti(
    [property: JsonPropertyName("giorno")] string Giorno,
    [property: JsonPropertyName("segmenti")] IReadOnlyList<SegmentoInvio> Segmenti);

/// <summary>Risposta di <c>GET /api/agente/sessione</c>.</summary>
public sealed class RispostaSessione
{
    public sealed class SessioneInfo
    {
        [JsonPropertyName("id")] public required string Id { get; init; }
        [JsonPropertyName("giorno")] public required string Giorno { get; init; }
        [JsonPropertyName("tipo")] public required string Tipo { get; init; }
        [JsonPropertyName("inizio")] public required string Inizio { get; init; }
        [JsonPropertyName("inPausa")] public required bool InPausa { get; init; }
    }

    [JsonPropertyName("ok")] public required bool Ok { get; init; }
    /// <summary>Si misura solo se true: falso senza sessione e anche in pausa.</summary>
    [JsonPropertyName("traccia")] public required bool Traccia { get; init; }
    [JsonPropertyName("sessione")] public SessioneInfo? Sessione { get; init; }
    /// <summary>Ora del server, per lo scarto d'orologio.</summary>
    [JsonPropertyName("adesso")] public required string Adesso { get; init; }
}

/// <summary>Risposta 200 di <c>POST /api/agente/segmenti</c>.</summary>
public sealed class RispostaSegmenti
{
    [JsonPropertyName("ok")] public required bool Ok { get; init; }
    [JsonPropertyName("giorno")] public required string Giorno { get; init; }
    [JsonPropertyName("scritti")] public required int Scritti { get; init; }
    [JsonPropertyName("rimossi")] public required int Rimossi { get; init; }
}

/// <summary>Corpo di <c>POST /api/agente/stop</c>: senza <c>alle</c> il server usa adesso (corpo <c>{}</c>).</summary>
public sealed class StopRichiesta
{
    [JsonPropertyName("alle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Alle { get; init; }
}

/// <summary>Risposta 200 di <c>POST /api/agente/stop</c>: <c>chiusa</c> falso = non c'era niente di aperto.</summary>
public sealed class RispostaStop
{
    [JsonPropertyName("ok")] public required bool Ok { get; init; }
    [JsonPropertyName("chiusa")] public required bool Chiusa { get; init; }
}

/// <summary>Corpo d'errore delle rotte: <c>{ ok: false, errore, indice? }</c>.</summary>
public sealed class RispostaErrore
{
    [JsonPropertyName("ok")] public bool? Ok { get; init; }
    [JsonPropertyName("errore")] public required string Errore { get; init; }
    [JsonPropertyName("indice")] public int? Indice { get; init; }
}

/// <summary>Codifica JSON e istanti.</summary>
public static class SyncJson
{
    /// <summary>Opzioni per richieste e risposte: <c>null</c> espliciti, caratteri non ASCII leggibili.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Decode<T>(byte[] data) =>
        JsonSerializer.Deserialize<T>(data, Options) ?? throw new JsonException("risposta vuota");

    /// <summary>Istante UTC con millisecondi e «Z» (la forma <c>ISTANTE_UTC</c> del contratto).</summary>
    public static string Instant(DateTimeOffset date) =>
        date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static readonly string[] InstantFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.f'Z'", "yyyy-MM-dd'T'HH:mm:ss.ff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.fffK",
    ];

    /// <summary>Legge un istante ISO 8601 con o senza frazione di secondo.</summary>
    public static DateTimeOffset? ParseInstant(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return DateTimeOffset.TryParseExact(s, InstantFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }
}
