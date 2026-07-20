using System.Net;
using System.Security.Cryptography;
using System.Text;
using HeadsUp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HeadsUp.Tests;

public class PkcePairTests
{
    [Fact]
    public void Challenge_is_the_S256_hash_of_the_verifier()
    {
        var pkce = PkcePair.Create();

        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(pkce.Verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, pkce.Challenge);
    }

    [Fact]
    public void Verifier_is_within_the_length_RFC_7636_allows()
    {
        var pkce = PkcePair.Create();

        Assert.InRange(pkce.Verifier.Length, 43, 128);
    }

    [Fact]
    public void Values_are_base64url_with_no_padding()
    {
        var pkce = PkcePair.Create();

        Assert.DoesNotContain('+', pkce.Verifier + pkce.Challenge);
        Assert.DoesNotContain('/', pkce.Verifier + pkce.Challenge);
        Assert.DoesNotContain('=', pkce.Verifier + pkce.Challenge);
    }

    [Fact]
    public void Each_pair_is_unique()
    {
        var verifiers = Enumerable.Range(0, 50).Select(_ => PkcePair.Create().Verifier).ToList();

        Assert.Equal(verifiers.Count, verifiers.Distinct().Count());
    }
}

public class SlackOAuthClientTests
{
    private const string ClientId = "123.456";

    private static SlackOAuthClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<SlackOAuthClient>.Instance);

    // ------------------------------------------------------------------
    // Authorize URL
    // ------------------------------------------------------------------

    [Fact]
    public void Authorize_url_carries_the_pkce_challenge_and_no_secret()
    {
        var pkce = new PkcePair("verifier", "challenge");

        var url = SlackOAuthClient.BuildAuthorizeUrl(ClientId, "http://localhost:32741/callback", pkce, "state1");

        Assert.Contains("code_challenge=challenge", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=state1", url);
        Assert.DoesNotContain("client_secret", url);
        // The verifier is the secret half and must never travel in the browser.
        Assert.DoesNotContain("verifier", url);
    }

    [Fact]
    public void Authorize_url_requests_user_scopes_not_bot_scopes()
    {
        var url = SlackOAuthClient.BuildAuthorizeUrl(ClientId, "http://localhost/cb", PkcePair.Create(), "s");

        // Desktop (loopback/custom-scheme) redirects may not request bot scopes.
        Assert.Contains("user_scope=", url);
        Assert.DoesNotContain("&scope=", url);
    }

    // ------------------------------------------------------------------
    // Code exchange
    // ------------------------------------------------------------------

    [Fact]
    public async Task Exchange_sends_the_verifier_and_no_client_secret()
    {
        var handler = new CapturingHandler("""
            {"ok":true,"authed_user":{"access_token":"xoxp-new","refresh_token":"xoxe-1","expires_in":43200}}
            """);

        await Client(handler).ExchangeCodeAsync(ClientId, "code1", "http://localhost/cb", new PkcePair("v1", "c1"));

        Assert.Contains("code_verifier=v1", handler.LastBody);
        Assert.Contains("grant_type=authorization_code", handler.LastBody);
        Assert.DoesNotContain("client_secret", handler.LastBody);
    }

    [Fact]
    public async Task Exchange_reads_the_user_token_from_authed_user()
    {
        var handler = new CapturingHandler("""
            {"ok":true,"access_token":"xoxb-bot","authed_user":{"access_token":"xoxp-user","refresh_token":"xoxe-1","expires_in":43200}}
            """);

        var tokens = await Client(handler).ExchangeCodeAsync(ClientId, "c", "http://localhost/cb", PkcePair.Create());

        // The top-level access_token is the bot token; Heads-Up acts as the user.
        Assert.Equal("xoxp-user", tokens.AccessToken);
        Assert.Equal("xoxe-1", tokens.RefreshToken);
        Assert.NotNull(tokens.ExpiresAt);
    }

    [Fact]
    public async Task Non_expiring_tokens_have_no_expiry()
    {
        var handler = new CapturingHandler("""{"ok":true,"authed_user":{"access_token":"xoxp-user"}}""");

        var tokens = await Client(handler).ExchangeCodeAsync(ClientId, "c", "http://localhost/cb", PkcePair.Create());

        Assert.Null(tokens.ExpiresAt);
        Assert.Null(tokens.RefreshToken);
    }

    [Fact]
    public async Task A_logical_failure_throws_even_though_the_status_is_200()
    {
        var handler = new CapturingHandler("""{"ok":false,"error":"invalid_grant_type"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client(handler).ExchangeCodeAsync(ClientId, "c", "http://localhost/cb", PkcePair.Create()));

        Assert.Contains("invalid_grant_type", ex.Message);
    }

    [Fact]
    public async Task A_response_without_a_user_token_throws()
    {
        var handler = new CapturingHandler("""{"ok":true,"access_token":"xoxb-only"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client(handler).ExchangeCodeAsync(ClientId, "c", "http://localhost/cb", PkcePair.Create()));
    }

    // ------------------------------------------------------------------
    // Refresh
    // ------------------------------------------------------------------

    [Fact]
    public async Task Refresh_posts_the_refresh_grant_without_pkce_parameters()
    {
        var handler = new CapturingHandler(RefreshResponse);

        var tokens = await Client(handler).RefreshAsync(ClientId, "xoxe-1");

        Assert.Contains("grant_type=refresh_token", handler.LastBody);
        Assert.Contains("refresh_token=xoxe-1", handler.LastBody);
        Assert.DoesNotContain("code_verifier", handler.LastBody);
        // Slack rotates the refresh token, so the new one must come back for persisting.
        Assert.Equal("xoxe-2", tokens.RefreshToken);
    }

    /// <summary>
    /// The real shape of a user-token refresh, per Slack's token-rotation docs: flat, with
    /// token_type "user" and <b>no</b> authed_user wrapper — unlike the authorization_code
    /// exchange, which nests. Requiring the nested shape made every renewal throw, so every
    /// rotation forced a full browser sign-in.
    /// </summary>
    private const string RefreshResponse = """
        {"ok":true,"id":"U1234","scope":"channels:history","token_type":"user",
         "access_token":"xoxe.xoxp-2","refresh_token":"xoxe-2","expires_in":43200}
        """;

    [Fact]
    public async Task A_flat_refresh_response_yields_the_renewed_user_token()
    {
        var tokens = await Client(new CapturingHandler(RefreshResponse)).RefreshAsync(ClientId, "xoxe-1");

        Assert.Equal("xoxe.xoxp-2", tokens.AccessToken);
        Assert.Equal("xoxe-2", tokens.RefreshToken);
        Assert.NotNull(tokens.ExpiresAt);
    }

    [Fact]
    public async Task A_flat_response_that_is_not_a_user_token_is_refused()
    {
        // Guards the reason the top level was originally rejected outright: a bot token must
        // never be handed back as the user's, or we connect as the wrong identity.
        var handler = new CapturingHandler(
            """{"ok":true,"token_type":"bot","access_token":"xoxb-bot","expires_in":43200}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client(handler).RefreshAsync(ClientId, "xoxe-1"));
    }

    [Fact]
    public async Task A_flat_response_with_no_token_type_is_refused()
    {
        var handler = new CapturingHandler("""{"ok":true,"access_token":"xoxb-ambiguous"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client(handler).RefreshAsync(ClientId, "xoxe-1"));
    }

    /// <summary>Returns one canned JSON body and records the form that was posted.</summary>
    private sealed class CapturingHandler(string json) : HttpMessageHandler
    {
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}

public class SlackTokenSetTests
{
    private static SlackTokenSet WithExpiry(DateTimeOffset? expiresAt) =>
        new("cid", "xapp-1", "xoxp-1", "xoxe-1", expiresAt);

    [Fact]
    public void A_token_without_an_expiry_never_needs_refreshing() =>
        Assert.False(WithExpiry(null).NeedsRefresh(TimeSpan.FromMinutes(5)));

    [Fact]
    public void An_expired_token_needs_refreshing() =>
        Assert.True(WithExpiry(DateTimeOffset.UtcNow.AddMinutes(-1)).NeedsRefresh(TimeSpan.FromMinutes(5)));

    [Fact]
    public void A_token_expiring_inside_the_skew_needs_refreshing() =>
        Assert.True(WithExpiry(DateTimeOffset.UtcNow.AddMinutes(2)).NeedsRefresh(TimeSpan.FromMinutes(5)));

    [Fact]
    public void A_token_expiring_beyond_the_skew_does_not() =>
        Assert.False(WithExpiry(DateTimeOffset.UtcNow.AddHours(2)).NeedsRefresh(TimeSpan.FromMinutes(5)));
}

public class FileSlackTokenStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"headsup-tokens-{Guid.NewGuid():N}.dat");

    private FileSlackTokenStore Store(ITokenProtector? protector = null) =>
        new(protector ?? new ReversingProtector(), NullLogger<FileSlackTokenStore>.Instance, _path);

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public async Task Nothing_stored_yet_reads_as_null() =>
        Assert.Null(await Store().LoadAsync());

    [Fact]
    public async Task Tokens_survive_a_save_and_load_round_trip()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(12);
        await Store().SaveAsync(new SlackTokenSet("cid", "xapp-1", "xoxp-1", "xoxe-1", expiry));

        var loaded = await Store().LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal("cid", loaded.ClientId);
        Assert.Equal("xapp-1", loaded.AppToken);
        Assert.Equal("xoxp-1", loaded.UserToken);
        Assert.Equal("xoxe-1", loaded.RefreshToken);
        Assert.Equal(expiry.ToUnixTimeSeconds(), loaded.ExpiresAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Tokens_are_not_written_in_the_clear()
    {
        await Store().SaveAsync(new SlackTokenSet("cid", "xapp-secret", "xoxp-secret", "xoxe-secret", null));

        var raw = await File.ReadAllTextAsync(_path);

        Assert.DoesNotContain("xoxp-secret", raw);
        Assert.DoesNotContain("xapp-secret", raw);
    }

    [Fact]
    public async Task An_undecryptable_file_reads_as_not_signed_in()
    {
        // DPAPI is user+machine scoped, so a copied file lands here rather than crashing.
        await File.WriteAllTextAsync(_path, "not encrypted by us");

        Assert.Null(await Store(new ThrowingProtector()).LoadAsync());
    }

    [Fact]
    public async Task Clearing_removes_the_stored_tokens()
    {
        var store = Store();
        await store.SaveAsync(new SlackTokenSet("cid", "xapp-1", "xoxp-1", "xoxe-1", null));

        await store.ClearAsync();

        Assert.Null(await store.LoadAsync());
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task Clearing_when_nothing_is_stored_is_harmless() =>
        await Store().ClearAsync(); // must not throw

    /// <summary>Stand-in for DPAPI: reversible, and enough to prove the bytes get transformed.</summary>
    private sealed class ReversingProtector : ITokenProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Reverse().ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.Reverse().ToArray();
    }

    private sealed class ThrowingProtector : ITokenProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] ciphertext) => throw new CryptographicException("wrong user");
    }
}
