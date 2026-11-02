using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QualityStudio.Testing;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// The Agent Studio hand-off receiver and its sessions (QSI-2): verification of every assertion
/// property, the session cookie, role and repository rights, CSRF and Origin on unsafe requests,
/// revocation, the sign-in redirect for page loads, and the capabilities document.
/// </summary>
[Trait("Category", "ToolBound")]
public sealed class FederationTests : IAsyncLifetime
{
    private const string ServiceToken = "agent-studio-service-credential";
    private const string AliceToken = "alice-test-credential";
    private const string Fingerprint = "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-federation-tests", Guid.NewGuid().ToString("N"));
    private readonly TestEd25519Key key = TestEd25519Key.Create();
    private readonly ManualTimeProvider time = new(DateTimeOffset.UtcNow);
    private string RepositoryRoot => Path.Combine(testRoot, "default");
    private string ForeignRepositoryRoot => Path.Combine(testRoot, "foreign");
    private string HostRoot => Path.Combine(testRoot, "host");
    private string UiRoot => Path.Combine(testRoot, "ui");
    private FederatedApplication? application;

    [Fact]
    public async Task A_valid_handoff_opens_a_session_and_redirects_to_the_relative_target()
    {
        using var client = CreateClient();
        var assertion = Handoffs.Mint(key, Handoffs.Claims(time));

        using var response = await Handoffs.PostAsync(client, assertion);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/?repo=default&card=AGT-3352", response.Headers.Location?.OriginalString);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(FederatedSessionStore.CookieName + "=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("httponly", setCookie, StringComparison.Ordinal);
        Assert.Contains("secure", setCookie, StringComparison.Ordinal);
        Assert.Contains("samesite=lax", setCookie, StringComparison.Ordinal);
        Assert.Contains("path=/", setCookie, StringComparison.Ordinal);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.Ordinal);
        Assert.DoesNotContain(assertion, setCookie, StringComparison.OrdinalIgnoreCase);

        var cookie = Handoffs.SessionCookie(response)!;
        var session = await SessionAsync(client, cookie);
        Assert.Equal("session", session.GetProperty("kind").GetString());
        Assert.Equal("robert", session.GetProperty("subject").GetString());
        Assert.Equal("operator", session.GetProperty("role").GetString());
        Assert.Equal(["default"], session.GetProperty("repositories").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(64, session.GetProperty("csrfToken").GetString()!.Length);
        // The CSRF token also arrives as a cookie this origin's script reads; the session handle never does.
        var csrfCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(FederatedSessionStore.CsrfCookieName + "=", StringComparison.Ordinal));
        Assert.Equal(session.GetProperty("csrfToken").GetString(), csrfCookie.Split(';')[0][(FederatedSessionStore.CsrfCookieName.Length + 1)..]);
        Assert.DoesNotContain("httponly", csrfCookie.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Contains("secure", csrfCookie.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Contains("samesite=lax", csrfCookie.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Equal(Handoffs.StudioOrigin, session.GetProperty("federation").GetProperty("studioPublicUrl").GetString());
    }

    [Theory]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("issued-in-the-future", HttpStatusCode.Unauthorized)]
    [InlineData("longer-than-60-seconds", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-audience", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-issuer", HttpStatusCode.Unauthorized)]
    [InlineData("unknown-version", HttpStatusCode.UnprocessableEntity)]
    [InlineData("absolute-target", HttpStatusCode.BadRequest)]
    [InlineData("protocol-relative-target", HttpStatusCode.BadRequest)]
    [InlineData("backslash-target", HttpStatusCode.BadRequest)]
    [InlineData("unknown-role", HttpStatusCode.Unauthorized)]
    [InlineData("foreign-key", HttpStatusCode.Unauthorized)]
    [InlineData("tampered-claims", HttpStatusCode.Unauthorized)]
    [InlineData("alg-none", HttpStatusCode.Unauthorized)]
    [InlineData("missing", HttpStatusCode.BadRequest)]
    public async Task A_handoff_that_breaks_one_rule_is_refused_without_a_session(string defect, HttpStatusCode expected)
    {
        using var client = CreateClient();
        var claims = Handoffs.Claims(time);
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var signer = key;
        var alg = "EdDSA";
        switch (defect)
        {
            // 31 seconds past exp: beyond the 30 seconds of tolerated clock skew.
            case "expired": claims["iat"] = now - 91; claims["exp"] = now - 31; break;
            case "issued-in-the-future": claims["iat"] = now + 31; claims["exp"] = now + 91; break;
            case "longer-than-60-seconds": claims["exp"] = now + 61; break;
            case "wrong-audience": claims["aud"] = "https://quality-other.example"; break;
            case "wrong-issuer": claims["iss"] = "https://other-studio.example"; break;
            case "unknown-version": claims["ver"] = 2; break;
            case "absolute-target": claims["target"] = "https://evil.example/?repo=default"; break;
            case "protocol-relative-target": claims["target"] = "//evil.example/"; break;
            case "backslash-target": claims["target"] = "/\\evil.example/"; break;
            case "unknown-role": claims["role"] = "administrator"; break;
            case "foreign-key": signer = TestEd25519Key.Create(); break;
            case "alg-none": alg = "none"; break;
        }

        var assertion = defect == "missing" ? null : Handoffs.Mint(signer, claims, alg);
        if (defect == "tampered-claims")
        {
            var parts = assertion!.Split('.');
            claims["role"] = "owner";
            var forged = Handoffs.Mint(signer, claims).Split('.');
            assertion = parts[0] + "." + forged[1] + "." + parts[2];
        }
        using var response = await Handoffs.PostAsync(client, assertion);

        Assert.Equal(expected, response.StatusCode);
        Assert.Null(Handoffs.SessionCookie(response));
        Assert.Null(response.Headers.Location);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (assertion is not null) Assert.DoesNotContain(assertion, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("https://studio.example.evil")]
    [InlineData("https://quality.studio.example")]
    public async Task A_handoff_posted_from_another_origin_is_refused(string? origin)
    {
        using var client = CreateClient();

        using var response = await Handoffs.PostAsync(client, Handoffs.Mint(key, Handoffs.Claims(time)), origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(Handoffs.SessionCookie(response));
    }

    [Fact]
    public async Task A_used_jti_is_refused_again_also_after_a_restart()
    {
        var assertion = Handoffs.Mint(key, Handoffs.Claims(time));
        using (var client = CreateClient())
        {
            using var first = await Handoffs.PostAsync(client, assertion);
            Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);
            using var replay = await Handoffs.PostAsync(client, assertion);
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
            Assert.Null(Handoffs.SessionCookie(replay));
        }

        await using var restarted = new FederatedApplication(this);
        using var restartedClient = CreateClient(restarted);
        using var afterRestart = await Handoffs.PostAsync(restartedClient, assertion);
        Assert.Equal(HttpStatusCode.Unauthorized, afterRestart.StatusCode);
        Assert.True(File.Exists(Path.Combine(QualityDataRootFixture.BaseDirectory, "federation", "used-assertions.json")));
    }

    [Fact]
    public async Task Without_an_issuer_the_receiver_does_not_exist()
    {
        await using var plain = new FederatedApplication(this, federated: false);
        using var client = CreateClient(plain);

        using var response = await Handoffs.PostAsync(client, Handoffs.Mint(key, Handoffs.Claims(time)));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var page = await client.SendAsync(Page("/?repo=default"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    [Fact]
    public void Federation_refuses_to_start_in_local_mode()
    {
        using var local = new FederatedApplication(this, mode: "Local");
        var exception = Assert.Throws<InvalidOperationException>(() => local.CreateClient());
        Assert.Contains("Hosted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_viewer_reads_but_cannot_change_a_finding_state_or_reach_another_repository()
    {
        await WriteFindingAsync();
        using var client = CreateClient();
        var viewer = await SignInAsync(client, role: "viewer");

        using var file = await client.SendAsync(viewer.Get("/api/repos/default/file?path=Sample.cs"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        using var foreign = await client.SendAsync(viewer.Get("/api/repos/foreign/file?path=Foreign.cs"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.DoesNotContain(ForeignRepositoryRoot, await foreign.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        using var list = await client.SendAsync(viewer.Get("/api/repos"), TestContext.Current.CancellationToken);
        var listed = await list.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(["default"], listed.GetProperty("repositories").EnumerateArray().Select(item => item.GetProperty("id").GetString()));

        using var change = await client.SendAsync(viewer.Post("/api/repos/default/findings/state", FindingChange()),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, change.StatusCode);
        Assert.Equal("The viewer role does not permit this action", await TitleAsync(change));
        using var foreignChange = await client.SendAsync(viewer.Post("/api/repos/foreign/findings/state", FindingChange()),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, foreignChange.StatusCode);

        // A side-effect-free POST stays open to viewers.
        using var estimate = await client.SendAsync(viewer.Post("/api/repos/default/review/estimate", new { path = "Sample.cs", kind = "code" }),
            TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.Forbidden, estimate.StatusCode);
    }

    [Fact]
    public async Task An_operator_changes_a_finding_state_only_from_this_origin_with_the_csrf_token()
    {
        await WriteFindingAsync();
        using var client = CreateClient();
        var operatorSession = await SignInAsync(client);

        using var withoutToken = await client.SendAsync(
            operatorSession.Post("/api/repos/default/findings/state", FindingChange(), csrf: false), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, withoutToken.StatusCode);
        Assert.Equal("A valid X-CSRF-Token header is required", await TitleAsync(withoutToken));
        using var fromStudio = await client.SendAsync(
            operatorSession.Post("/api/repos/default/findings/state", FindingChange(), origin: Handoffs.StudioOrigin),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, fromStudio.StatusCode);
        Assert.Equal("Cross-origin request refused", await TitleAsync(fromStudio));
        using var withoutOrigin = await client.SendAsync(
            operatorSession.Post("/api/repos/default/findings/state", FindingChange(), origin: null), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, withoutOrigin.StatusCode);

        // No X-Client-Id: a session caller is identified by its session.
        using var accepted = await client.SendAsync(
            operatorSession.Post("/api/repos/default/findings/state", FindingChange()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var state = await accepted.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("accepted", state.GetProperty("state").GetString());

        using var foreign = await client.SendAsync(
            operatorSession.Post("/api/repos/foreign/findings/state", FindingChange()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Only_an_owner_changes_the_rule_pool_and_registrations_of_its_repositories()
    {
        using var client = CreateClient();
        var operatorSession = await SignInAsync(client, subject: "olga");
        var owner = await SignInAsync(client, subject: "otto", role: "owner");
        var change = new { severity = "low", reason = "Owner decision." };

        using var operatorChange = await client.SendAsync(
            operatorSession.Put("/api/repos/default/rules/overrides/QS-CS-004?scope=project", change), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, operatorChange.StatusCode);
        Assert.Equal("The operator role does not permit this action", await TitleAsync(operatorChange));
        using var ownerChange = await client.SendAsync(
            owner.Put("/api/repos/default/rules/overrides/QS-CS-004?scope=project", change), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ownerChange.StatusCode);
        // The host-wide pool reaches repositories beyond the hand-off, so it stays with registrar clients.
        using var ownerGlobal = await client.SendAsync(
            owner.Put("/api/repos/default/rules/overrides/QS-CS-004?scope=global", change), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, ownerGlobal.StatusCode);
        Assert.Equal("Global rule changes are not permitted", await TitleAsync(ownerGlobal));

        var registration = new { id = "elsewhere", displayName = "Elsewhere", rootPath = ForeignRepositoryRoot };
        using var outside = await client.SendAsync(owner.Post("/api/repos", registration), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);
        using var import = await client.SendAsync(owner.Post("/api/repos/import-from-agent-studio", new { }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, import.StatusCode);
        using var archiveForeign = await client.SendAsync(owner.Delete("/api/repos/foreign"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, archiveForeign.StatusCode);
        using var operatorRegistration = await client.SendAsync(operatorSession.Post("/api/repos", registration),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, operatorRegistration.StatusCode);
    }

    [Fact]
    public async Task The_spend_rate_limit_counts_per_user()
    {
        await using var limited = new FederatedApplication(this, spendRequestsPerMinute: 1);
        using var client = CreateClient(limited);
        var robert = await SignInAsync(client, subject: "robert");
        var ada = await SignInAsync(client, subject: "ada", sessionId: "studio-session-ada");
        var viewer = await SignInAsync(client, subject: "vera", role: "viewer", sessionId: "studio-session-vera");
        var review = new { path = "Sample.cs", kind = "code", cliType = "adapter-that-does-not-exist" };

        using var first = await client.SendAsync(robert.Post("/api/repos/default/review", review), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var second = robert.Post("/api/repos/default/review", review);
        // A header of the caller's choosing must not open another partition.
        second.Headers.Add(ApiSecurity.ClientIdHeader, "someone-else");
        using var secondResponse = await client.SendAsync(second, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, secondResponse.StatusCode);

        using var other = await client.SendAsync(ada.Post("/api/repos/default/review", review), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
        using var refused = await client.SendAsync(viewer.Post("/api/repos/default/review", review), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("The viewer role does not permit this action", await TitleAsync(refused));
    }

    [Fact]
    public async Task Revoke_ends_a_session_within_one_request_and_is_reserved_for_the_service_client()
    {
        using var client = CreateClient();
        var robert = await SignInAsync(client, sessionId: "studio-session-robert");
        var ada = await SignInAsync(client, subject: "ada", sessionId: "studio-session-ada");
        var pending = Handoffs.Mint(key, Handoffs.Claims(time, sessionId: "studio-session-robert"));

        using var alice = BearerClient(AliceToken, "alice");
        using var notService = await alice.PostAsJsonAsync("/api/v1/federation/revoke", new { sid = "studio-session-robert" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, notService.StatusCode);
        using var fromSession = await client.SendAsync(robert.Post("/api/v1/federation/revoke", new { sid = "studio-session-ada" }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, fromSession.StatusCode);

        using var service = BearerClient(ServiceToken, "agent-studio");
        using var revoke = await service.PostAsJsonAsync("/api/v1/federation/revoke", new { sid = "studio-session-robert" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(1, (await revoke.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("revoked").GetInt32());

        using var ended = await client.SendAsync(robert.Get("/api/v1/session"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
        Assert.Equal("agent-studio", Assert.Single(ended.Headers.GetValues("X-QS-Sign-In")));
        using var unaffected = await client.SendAsync(ada.Get("/api/v1/session"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, unaffected.StatusCode);
        // An assertion minted before the logout cannot open a new session afterwards.
        using var late = await Handoffs.PostAsync(client, pending);
        Assert.Equal(HttpStatusCode.Unauthorized, late.StatusCode);

        using var bySubject = await service.PostAsJsonAsync("/api/v1/federation/revoke", new { sub = "ada" },
            TestContext.Current.CancellationToken);
        Assert.Equal(1, (await bySubject.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("revoked").GetInt32());
        using var adaEnded = await client.SendAsync(ada.Get("/api/v1/session"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, adaEnded.StatusCode);
    }

    [Fact]
    public async Task Sessions_end_after_60_idle_minutes_and_after_12_hours()
    {
        using var client = CreateClient();
        var idle = await SignInAsync(client, subject: "idle", sessionId: "studio-session-idle");
        var busy = await SignInAsync(client, subject: "busy", sessionId: "studio-session-busy");

        time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(idle.Get("/api/v1/session"), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(busy.Get("/api/v1/session"), TestContext.Current.CancellationToken)).StatusCode);
        time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(idle.Get("/api/v1/session"), TestContext.Current.CancellationToken)).StatusCode);

        // busy was last seen 61 minutes ago too; keep a fresh one active until the absolute limit.
        var active = await SignInAsync(client, subject: "active", sessionId: "studio-session-active");
        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromHours(12) - TimeSpan.FromMinutes(50); elapsed += TimeSpan.FromMinutes(50))
        {
            time.Advance(TimeSpan.FromMinutes(50));
            Assert.Equal(HttpStatusCode.OK,
                (await client.SendAsync(active.Get("/api/v1/session"), TestContext.Current.CancellationToken)).StatusCode);
        }
        time.Advance(TimeSpan.FromMinutes(51));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(active.Get("/api/v1/session"), TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task A_page_load_without_a_session_goes_to_the_studio_sign_in_and_keeps_its_place()
    {
        using var client = CreateClient();

        using var page = await client.SendAsync(Page("/?repo=default&path=Sample.cs&view=dashboard"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        var location = page.Headers.Location!.OriginalString;
        Assert.Equal(Handoffs.StudioOrigin + AgentStudioFederation.StudioOpenPath +
                     "?project=default&return=" + Uri.EscapeDataString("/?repo=default&path=Sample.cs&view=dashboard"), location);
        using var deep = await client.SendAsync(Page("/some/client/route"), TestContext.Current.CancellationToken);
        Assert.Equal(Handoffs.StudioOrigin + AgentStudioFederation.StudioOpenPath + "?return=" + Uri.EscapeDataString("/some/client/route"),
            deep.Headers.Location!.OriginalString);

        // Assets are public: only the shell is gated.
        using var asset = await client.GetAsync("/main-ABCDEFGH.js", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);

        var session = await SignInAsync(client);
        var withSession = Page("/?repo=default");
        withSession.Headers.Add("Cookie", $"{FederatedSessionStore.CookieName}={session.Cookie}");
        using var signedIn = await client.SendAsync(withSession, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Contains("<title>Quality Studio</title>", await signedIn.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bearer_clients_keep_working_on_a_federated_host_and_the_v1_aliases_apply_the_same_policy()
    {
        using var alice = BearerClient(AliceToken, "alice");

        using var repositories = await alice.GetAsync("/api/v1/repos", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, repositories.StatusCode);
        using var file = await alice.GetAsync("/api/v1/repos/default/file?path=Sample.cs", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        using var foreign = await alice.GetAsync("/api/v1/repos/foreign/file?path=Foreign.cs", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var register = await alice.PostAsJsonAsync("/api/v1/repos",
            new { id = "elsewhere", displayName = "Elsewhere", rootPath = ForeignRepositoryRoot }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, register.StatusCode);

        using var anonymous = CreateClient();
        using var refused = await anonymous.GetAsync("/api/v1/repos", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        // A bearer token is judged alone: a session cookie next to a wrong token does not rescue it.
        var session = await SignInAsync(anonymous);
        using var mixed = new HttpRequestMessage(HttpMethod.Get, "/api/v1/session");
        mixed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        mixed.Headers.Add("Cookie", $"{FederatedSessionStore.CookieName}={session.Cookie}");
        using var mixedResponse = await anonymous.SendAsync(mixed, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, mixedResponse.StatusCode);
        Assert.False(mixedResponse.Headers.Contains("X-QS-Sign-In"));
    }

    [Fact]
    public async Task Suppression_on_read_only_federated_checkout_creates_one_patch_card_without_writing()
    {
        if (OperatingSystem.IsWindows()) return;
        await WriteFindingAsync();
        var handler = new StaticResponseHandler("{\"id\":\"AGT-123\"}");
        await using var live = new FederatedApplication(this, liveHandover: true, handoverHandler: handler);
        using var client = CreateClient(live);
        var originalMode = File.GetUnixFileMode(RepositoryRoot);
        File.SetUnixFileMode(RepositoryRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/repos/default/findings/suppressions")
            {
                Content = JsonContent.Create(new
                {
                    path = "Sample.cs",
                    kind = "code",
                    fingerprint = Fingerprint,
                    author = "Robert",
                    reason = "Covered by migration policy",
                    expectedRevision = 0,
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AliceToken);
            request.Headers.Add(ApiSecurity.ClientIdHeader, "alice");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal("AGT-123", result.GetProperty("taskId").GetString());
            Assert.Equal("https://studio.example/#/tasks/AGT-123", result.GetProperty("url").GetString());
            Assert.Single(handler.Requests);
            Assert.Equal(new Uri("http://agent-studio.test/api/tasks"), handler.Requests[0]);
            var body = Assert.Single(handler.Bodies);
            using var card = JsonDocument.Parse(body);
            var prompt = card.RootElement.GetProperty("promptMarkdown").GetString()!;
            Assert.Contains("Acting user: alice", prompt, StringComparison.Ordinal);
            Assert.Contains("Reason: Covered by migration policy", prompt, StringComparison.Ordinal);
            Assert.Contains("diff --git a/.quality/findings/suppressions.json b/.quality/findings/suppressions.json", prompt, StringComparison.Ordinal);
            Assert.Contains(Fingerprint, prompt, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(RepositoryRoot, FindingSuppressionStore.RelativePath)));
        }
        finally
        {
            File.SetUnixFileMode(RepositoryRoot, originalMode);
        }
    }

    [Fact]
    public async Task Federated_suppression_defaults_to_a_dry_run_record()
    {
        await WriteFindingAsync();
        using var client = BearerClient(AliceToken, "alice");
        using var response = await client.PostAsJsonAsync("/api/repos/default/findings/suppressions", new
        {
            path = "Sample.cs",
            kind = "code",
            fingerprint = Fingerprint,
            author = "Robert",
            reason = "Covered by migration policy",
            expectedRevision = 0,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(receipt.GetProperty("dryRun").GetBoolean());
        Assert.Equal(JsonValueKind.Null, receipt.GetProperty("taskId").ValueKind);
        Assert.Contains(Fingerprint, receipt.GetProperty("card").GetProperty("promptMarkdown").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(RepositoryRoot, FindingSuppressionStore.RelativePath)));
    }

    [Fact]
    public async Task Hosted_without_federation_still_writes_suppression_directly()
    {
        await WriteFindingAsync();
        await using var plain = new FederatedApplication(this, federated: false);
        using var client = CreateClient(plain);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/repos/default/findings/suppressions")
        {
            Content = JsonContent.Create(new
            {
                path = "Sample.cs",
                kind = "code",
                fingerprint = Fingerprint,
                author = "Robert",
                reason = "Covered by migration policy",
                expectedRevision = 0,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AliceToken);
        request.Headers.Add(ApiSecurity.ClientIdHeader, "alice");
        var path = Path.Combine(RepositoryRoot, FindingSuppressionStore.RelativePath);
        try
        {
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Federated_rule_scope_and_guideline_edits_each_create_a_patch_card()
    {
        var handler = new StaticResponseHandler("{\"id\":\"AGT-123\"}");
        await using var live = new FederatedApplication(this, liveHandover: true, handoverHandler: handler);
        using var client = CreateClient(live);

        async Task<HttpResponseMessage> ChangeAsync(HttpMethod method, string path, object body)
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AliceToken);
            request.Headers.Add(ApiSecurity.ClientIdHeader, "alice");
            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        using var rule = await ChangeAsync(HttpMethod.Put, "/api/repos/default/rules/overrides/QS-CS-004",
            new { enabled = false, reason = "Sample policy" });
        using var scope = await ChangeAsync(HttpMethod.Post, "/api/repos/default/scope/rules",
            new { action = "exclude", pattern = "Sample.cs", reason = "Generated sample" });
        using var guideline = await ChangeAsync(HttpMethod.Post, "/api/repos/default/guidelines",
            new
            {
                id = "sample-guideline",
                enabled = true,
                priority = 50,
                kinds = new[] { "code" },
                levels = new[] { "file" },
                content = "Review this sample.",
            });

        Assert.Equal(HttpStatusCode.OK, rule.StatusCode);
        Assert.Equal(HttpStatusCode.OK, scope.StatusCode);
        Assert.Equal(HttpStatusCode.OK, guideline.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains(".quality/rules/overrides.json", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains(".quality/scope.json", handler.Bodies[1], StringComparison.Ordinal);
        Assert.Contains(".quality/inputs/sample-guideline.md", handler.Bodies[2], StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(RepositoryRoot, ".quality/rules/overrides.json")));
        Assert.False(File.Exists(Path.Combine(RepositoryRoot, ".quality/scope.json")));
        Assert.False(File.Exists(Path.Combine(RepositoryRoot, ".quality/inputs/sample-guideline.md")));
    }

    [Fact]
    public async Task A_bearer_browser_learns_its_client_id_and_can_change_a_finding_state()
    {
        // Regression: the frontend never sent X-Client-Id, so hosted-mode browsers could not mutate.
        await WriteFindingAsync();
        await using var plain = new FederatedApplication(this, federated: false);
        using var browser = CreateClient(plain);
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AliceToken);

        var session = await browser.GetFromJsonAsync<JsonElement>("/api/v1/session", TestContext.Current.CancellationToken);
        Assert.Equal("client", session.GetProperty("kind").GetString());
        var clientId = session.GetProperty("clientId").GetString()!;
        Assert.Equal("alice", clientId);

        using var withoutHeader = await browser.PostAsJsonAsync("/api/repos/default/findings/state", FindingChange(),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutHeader.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/repos/default/findings/state")
        {
            Content = JsonContent.Create(FindingChange()),
        };
        request.Headers.Add(ApiSecurity.ClientIdHeader, clientId);
        using var accepted = await browser.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task Capabilities_validate_against_the_schema_on_federated_and_plain_hosts()
    {
        var schema = JsonSchema.FromText(await File.ReadAllTextAsync(
            Path.Combine(FindRepositoryRoot(), "schemas", "integration-capabilities.v1.schema.json"),
            TestContext.Current.CancellationToken));
        using var service = BearerClient(ServiceToken, "agent-studio");
        var federated = await service.GetFromJsonAsync<JsonElement>("/api/v1/capabilities", TestContext.Current.CancellationToken);

        await using var plain = new FederatedApplication(this, federated: false);
        using var plainClient = CreateClient(plain);
        plainClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ServiceToken);
        var standalone = await plainClient.GetFromJsonAsync<JsonElement>("/api/v1/capabilities", TestContext.Current.CancellationToken);

        foreach (var document in new[] { federated, standalone })
        {
            var result = schema.Evaluate(document, new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(result.IsValid, JsonSerializer.Serialize(result));
            Assert.Equal(["agent-studio-integration/1"], document.GetProperty("contracts").EnumerateArray().Select(item => item.GetString()));
            Assert.Contains("deep-link/1", document.GetProperty("features").EnumerateArray().Select(item => item.GetString()));
        }
        Assert.Contains("federation", federated.GetProperty("features").EnumerateArray().Select(item => item.GetString()));
        Assert.DoesNotContain("federation", standalone.GetProperty("features").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(Handoffs.Audience, federated.GetProperty("federation").GetProperty("audience").GetString());

        using var anonymous = CreateClient();
        using var refused = await anonymous.GetAsync("/api/v1/capabilities", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task Keys_from_the_issuer_jwks_verify_a_handoff()
    {
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new object[]
            {
                new { kty = "RSA", kid = "ignored", n = "AQAB", e = "AQAB" },
                new { kty = "OKP", crv = "Ed25519", kid = Handoffs.KeyId, x = key.PublicKeyBase64Url, use = "sig", alg = "EdDSA" },
            },
        });
        var handler = new StaticResponseHandler(jwks);
        await using var fetched = new FederatedApplication(this, jwksHandler: handler);
        using var client = CreateClient(fetched);

        using var response = await Handoffs.PostAsync(client, Handoffs.Mint(key, Handoffs.Claims(time)));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("https://studio.example/.well-known/agent-studio/jwks.json", Assert.Single(handler.Requests).ToString());
    }

    [Fact]
    public async Task The_handoff_never_reaches_the_log()
    {
        var logs = new CapturingLoggerProvider();
        await using var logged = new FederatedApplication(this, logs: logs);
        using var client = CreateClient(logged);
        var accepted = Handoffs.Mint(key, Handoffs.Claims(time));
        var expired = Handoffs.Claims(time);
        expired["exp"] = time.GetUtcNow().ToUnixTimeSeconds() - 120;
        expired["iat"] = time.GetUtcNow().ToUnixTimeSeconds() - 150;
        var refused = Handoffs.Mint(key, expired);

        using (await Handoffs.PostAsync(client, accepted)) { }
        using (await Handoffs.PostAsync(client, accepted)) { }
        using (await Handoffs.PostAsync(client, refused)) { }

        Assert.Contains(logs.Lines, line => line.Contains("robert", StringComparison.Ordinal));
        foreach (var assertion in new[] { accepted, refused })
        {
            var signature = assertion.Split('.')[2];
            Assert.DoesNotContain(logs.Lines, line => line.Contains(signature, StringComparison.Ordinal));
        }
    }

    // ---------------------------------------------------------------- helpers

    private async Task<SessionCaller> SignInAsync(HttpClient client, string subject = "robert", string role = "operator",
        string? sessionId = null, string[]? repositories = null)
    {
        var claims = Handoffs.Claims(time, subject, role, repositories, sessionId ?? "studio-session-" + subject);
        using var response = await Handoffs.PostAsync(client, Handoffs.Mint(key, claims));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var cookie = Handoffs.SessionCookie(response)!;
        var session = await SessionAsync(client, cookie);
        return new SessionCaller(cookie, session.GetProperty("csrfToken").GetString()!);
    }

    private static async Task<JsonElement> SessionAsync(HttpClient client, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/session");
        request.Headers.Add("Cookie", $"{FederatedSessionStore.CookieName}={cookie}");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private sealed record SessionCaller(string Cookie, string Csrf)
    {
        public const string ThisOrigin = "https://localhost";

        public HttpRequestMessage Get(string path) => Request(HttpMethod.Get, path, null, false, null);

        public HttpRequestMessage Post(string path, object body, bool csrf = true, string? origin = ThisOrigin) =>
            Request(HttpMethod.Post, path, body, csrf, origin);

        public HttpRequestMessage Put(string path, object body) => Request(HttpMethod.Put, path, body, true, ThisOrigin);

        public HttpRequestMessage Delete(string path) => Request(HttpMethod.Delete, path, null, true, ThisOrigin);

        private HttpRequestMessage Request(HttpMethod method, string path, object? body, bool csrf, string? origin)
        {
            var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            request.Headers.Add("Cookie", $"{FederatedSessionStore.CookieName}={Cookie}");
            if (csrf) request.Headers.Add(FederatedSessionStore.CsrfHeader, Csrf);
            if (origin is not null) request.Headers.Add("Origin", origin);
            return request;
        }
    }

    private static async Task<string?> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
        .GetProperty("title").GetString();

    private static HttpRequestMessage Page(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("text/html");
        return request;
    }

    private static object FindingChange() => new
    {
        path = "Sample.cs",
        kind = "code",
        fingerprint = Fingerprint,
        state = "accepted",
        author = "Robert",
        reason = "Risk is understood and tracked.",
    };

    private async Task WriteFindingAsync()
    {
        var findingId = "finding-" + new string('d', 64);
        var metadataPath = ReviewMetaPath.ForFile(RepositoryRoot, "Sample.cs", "code");
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        var grade = new ReviewGrade(60, GradeBand.D, "One finding.");
        var metadata = new ReviewMetaDocument
        {
            Unit = new ReviewUnit("qs-v1/generic/file/" + new string('a', 64), ReviewAdapter.Generic,
                ReviewLevel.File, "Sample.cs", "Sample.cs"),
            ReviewedAt = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero),
            Kind = ReviewKind.Code,
            Reviewer = new ReviewerIdentity("test", "test"),
            ReviewedHash = ManifestHash.Subject(new string('b', 64)),
            SubjectInputs = [new SubjectInputHash("Sample.cs", "file", "sha256:" + new string('c', 64))],
            ReviewInputs = new ReviewInputs(
                ManifestHash.ReviewInput(new string('e', 64)), true, [], [],
                new PromptReference("file-code-review", "1.0.0", "sha256:" + new string('f', 64))),
            Grade = grade,
            Summary = "One finding.",
            Aspects = [new ReviewAspect("correctness", "Correctness", grade)],
            Findings = [new ReviewFinding(findingId, "correctness", FindingSeverity.High, "Test finding",
                "A finding used by the federation test.", "Review it.", [new FindingLocation("Sample.cs")],
                Fingerprint, "correctness.test")],
        };
        await File.WriteAllTextAsync(metadataPath, ReviewMetaJson.Serialize(metadata), TestContext.Current.CancellationToken);
    }

    private HttpClient CreateClient(WebApplicationFactory<Program>? target = null) =>
        (target ?? application!).CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

    private HttpClient BearerClient(string token, string clientId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add(ApiSecurity.ClientIdHeader, clientId);
        return client;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QualityStudio.slnx"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("The repository root was not found above the test output.");
    }

    public async ValueTask InitializeAsync()
    {
        foreach (var directory in new[] { RepositoryRoot, ForeignRepositoryRoot, HostRoot, UiRoot })
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(RepositoryRoot, "Sample.cs"), "namespace Sample; public sealed class Subject;");
        await File.WriteAllTextAsync(Path.Combine(ForeignRepositoryRoot, "Foreign.cs"), "namespace Foreign; public sealed class Secret;");
        await File.WriteAllTextAsync(Path.Combine(UiRoot, "index.html"), "<!doctype html><title>Quality Studio</title>");
        await File.WriteAllTextAsync(Path.Combine(UiRoot, "main-ABCDEFGH.js"), "console.log('bundle');");
        await GitTestRepository.InitializeAsync(RepositoryRoot);
        await GitTestRepository.InitializeAsync(ForeignRepositoryRoot);
        var registry = Path.Combine(HostRoot, ".quality-studio", "repositories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        await File.WriteAllTextAsync(registry, JsonSerializer.Serialize(new[]
        {
            new RepositoryRegistration("default", "Default", RepositoryRoot, null, 12000, ["code", "security", "performance"]),
            new RepositoryRegistration("foreign", "Foreign", ForeignRepositoryRoot, null, 12000, ["code", "security", "performance"]),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        application = new FederatedApplication(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        TemporaryDirectory.Delete(testRoot);
    }

    private sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FederatedApplication(
        FederationTests owner,
        bool federated = true,
        string mode = "Hosted",
        int spendRequestsPerMinute = 100,
        HttpMessageHandler? jwksHandler = null,
        bool liveHandover = false,
        HttpMessageHandler? handoverHandler = null,
        CapturingLoggerProvider? logs = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(owner.HostRoot);
            var configuration = new Dictionary<string, string?>
            {
                ["QualityStudio:RepositoryRoot"] = owner.RepositoryRoot,
                ["QualityStudio:AllowedRoots:0"] = owner.RepositoryRoot,
                ["QualityStudio:AllowedRoots:1"] = owner.ForeignRepositoryRoot,
                ["QualityStudio:Ui:RootPath"] = owner.UiRoot,
                ["QualityStudio:Security:Mode"] = mode,
                ["QualityStudio:Security:RequireHttps"] = "true",
                ["QualityStudio:Security:SpendRequestsPerMinute"] = spendRequestsPerMinute.ToString(),
                ["QualityStudio:Security:Clients:0:Id"] = "agent-studio",
                ["QualityStudio:Security:Clients:0:CredentialSha256"] = Hash(ServiceToken),
                ["QualityStudio:Security:Clients:0:Repositories:0"] = "*",
                ["QualityStudio:Security:Clients:0:CanRegisterRepositories"] = "true",
                ["QualityStudio:Security:Clients:1:Id"] = "alice",
                ["QualityStudio:Security:Clients:1:CredentialSha256"] = Hash(AliceToken),
                ["QualityStudio:Security:Clients:1:Repositories:0"] = "default",
                ["AgentStudio:BaseUrl"] = "http://agent-studio.test",
                ["AgentStudio:ClientId"] = "quality-studio",
                ["AgentStudio:Project"] = "QS",
                ["AgentStudio:DryRun"] = liveHandover ? "false" : "true",
            };
            if (federated)
            {
                configuration["AgentStudio:Federation:Issuer"] = Handoffs.Issuer;
                configuration["AgentStudio:Federation:Audience"] = Handoffs.Audience;
                configuration["AgentStudio:Federation:StudioPublicUrl"] = Handoffs.StudioOrigin + "/";
                if (jwksHandler is null)
                {
                    configuration["AgentStudio:Federation:Keys:0:Kid"] = Handoffs.KeyId;
                    configuration["AgentStudio:Federation:Keys:0:X"] = owner.key.PublicKeyBase64Url;
                }
                else
                {
                    configuration["AgentStudio:Federation:JwksUrl"] = "https://studio.example/.well-known/agent-studio/jwks.json";
                }
            }
            builder.ConfigureAppConfiguration((_, configurationBuilder) => configurationBuilder.AddInMemoryCollection(configuration));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(owner.time);
                if (jwksHandler is not null) services.AddSingleton(new HttpClient(jwksHandler));
                if (handoverHandler is not null) services.AddSingleton(new HttpClient(handoverHandler));
            });
            if (logs is not null) builder.ConfigureLogging(logging => logging.AddProvider(logs));
        }

        private static string Hash(string credential) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
    }
}
