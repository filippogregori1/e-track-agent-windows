namespace ActivityTracker.Core;

/// <summary>
/// Deposito di segreti (stringhe per nome). Nell'app è Gestione credenziali di Windows (<c>CredentialManagerStore</c>);
/// nelle verifiche un'implementazione in memoria, così i test automatici non scrivono mai nel deposito reale.
/// </summary>
public interface ITokenStore
{
    string? Token(string account);
    void Save(string token, string account);
    void Delete(string account);
}

public sealed class InMemoryTokenStore : ITokenStore
{
    private readonly Dictionary<string, string> _tokens = [];

    public string? Token(string account) => _tokens.TryGetValue(account, out var t) ? t : null;
    public void Save(string token, string account) => _tokens[account] = token;
    public void Delete(string account) => _tokens.Remove(account);
    public int Count => _tokens.Count;
}

/// <summary>
/// Le tre credenziali dell'agente verso equipe-track, tutte nel deposito sicuro (mai in file di impostazioni, mai nel
/// codice): URL del server, token dell'agente (emesso da <c>npm run agente:token</c>) e segreto del gate
/// (<c>ACCESS_SECRET</c>, mandato come cookie <c>et_gate</c>; facoltativo, vuoto = nessun cookie).
/// </summary>
public sealed record EquipeCredentials(string ServerUrl, string Token, string GateSecret)
{
    public static class Account
    {
        public const string ServerUrl = "server-url";
        public const string Token = "agent-token";
        public const string GateSecret = "gate-secret";
    }

    /// <summary>null se manca l'URL o il token.</summary>
    public static EquipeCredentials? Load(ITokenStore store)
    {
        var url = store.Token(Account.ServerUrl);
        var token = store.Token(Account.Token);
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token)) return null;
        return new EquipeCredentials(url, token, store.Token(Account.GateSecret) ?? "");
    }

    public void Save(ITokenStore store)
    {
        store.Save(ServerUrl, Account.ServerUrl);
        store.Save(Token, Account.Token);
        if (GateSecret.Length == 0) store.Delete(Account.GateSecret);
        else store.Save(GateSecret, Account.GateSecret);
    }

    public static void Delete(ITokenStore store)
    {
        store.Delete(Account.ServerUrl);
        store.Delete(Account.Token);
        store.Delete(Account.GateSecret);
    }

    /// <summary>Mai il token o il segreto in un log o in un messaggio.</summary>
    public override string ToString() => $"EquipeCredentials({ServerUrl}, token ***, gate {(GateSecret.Length > 0 ? "***" : "-")})";
}
