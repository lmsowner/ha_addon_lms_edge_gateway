using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

const string ProductName = "LMS Tesla Fleet Helper";
const string TeslaPublicKeyPath = "/.well-known/appspecific/com.tesla.3p.public-key.pem";
const string TeslaPublicKeyContentType = "application/x-pem-file";
const string HomeAssistantRedirectUri = "https://my.home-assistant.io/redirect/oauth";

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    ?? "http://0.0.0.0:5055");
builder.Services.AddSingleton<TeslaFleetStore>();
builder.Services.AddHttpClient<EdgeGatewayPublicAssetClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LMS-Tesla-Fleet-Helper");
});

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", product = ProductName }));

app.MapGet("/", async (TeslaFleetStore store) =>
{
    var state = await store.LoadAsync();
    return Results.Content(RenderPage(state), "text/html; charset=utf-8");
});

app.MapPost("/actions/save-settings", async (
    HttpContext context,
    TeslaFleetStore store) =>
{
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    var state = await store.LoadAsync();
    try
    {
        state = state with
        {
            EdgeGatewayUrl = NormalizeHttpUrl(form["edge_gateway_url"].ToString()),
            OriginDomain = NormalizeDomain(form["origin_domain"].ToString(), required: false),
            LastStatus = "Settings saved",
            LastMessage = "Tesla Fleet Helper settings were saved.",
            LastChecks = []
        };
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Settings error",
            LastMessage = exception.Message,
            LastChecks = []
        };
    }

    await store.SaveAsync(state, context.RequestAborted);
    return Results.Redirect("/");
});

app.MapPost("/actions/generate-key", async (
    HttpContext context,
    TeslaFleetStore store) =>
{
    var state = await store.LoadAsync();
    try
    {
        state = await GenerateKeyAsync(store, state, context.RequestAborted);
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Key generation failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
        await store.SaveAsync(state, context.RequestAborted);
    }

    return Results.Redirect("/");
});

app.MapPost("/actions/publish", async (
    HttpContext context,
    TeslaFleetStore store,
    EdgeGatewayPublicAssetClient edgeGatewayClient) =>
{
    var state = await store.LoadAsync();
    try
    {
        if (string.IsNullOrWhiteSpace(state.PublicKeyPem) ||
            string.IsNullOrWhiteSpace(state.PrivateKeyPath) ||
            !File.Exists(state.PrivateKeyPath))
        {
            state = await GenerateKeyAsync(store, state, context.RequestAborted);
        }

        var originDomain = NormalizeDomain(state.OriginDomain, required: true);
        var result = await edgeGatewayClient.PublishAsync(
            state.EdgeGatewayUrl,
            originDomain,
            state.PublicKeyPem,
            context.RequestAborted);
        state = state with
        {
            OriginDomain = originDomain,
            PublicAssetId = result.Asset?.Id ?? state.PublicAssetId,
            PublicKeyUrl = result.Asset?.PublicUrl ?? BuildPublicKeyUrl(originDomain),
            LastPublishedUtc = result.Succeeded ? DateTimeOffset.UtcNow : state.LastPublishedUtc,
            LastStatus = result.Succeeded ? "Published" : "Publish failed",
            LastMessage = result.Summary,
            LastChecks = result.Warnings
        };
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Publish failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
    }

    await store.SaveAsync(state, context.RequestAborted);
    return Results.Redirect("/");
});

app.MapPost("/actions/verify", async (
    HttpContext context,
    TeslaFleetStore store,
    EdgeGatewayPublicAssetClient edgeGatewayClient) =>
{
    var state = await store.LoadAsync();
    try
    {
        var result = state.PublicAssetId.HasValue
            ? await edgeGatewayClient.VerifyAsync(state.EdgeGatewayUrl, state.PublicAssetId.Value, context.RequestAborted)
            : await edgeGatewayClient.VerifyPublicUrlAsync(state.PublicKeyUrl, state.PublicKeyPem, context.RequestAborted);
        state = state with
        {
            LastVerifiedUtc = DateTimeOffset.UtcNow,
            LastStatus = result.Succeeded ? "Verified" : "Verification failed",
            LastMessage = result.Message,
            LastChecks = result.Checks
        };
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Verification failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
    }

    await store.SaveAsync(state, context.RequestAborted);
    return Results.Redirect("/");
});

app.MapGet("/tesla_fleet.key", async (
    TeslaFleetStore store,
    CancellationToken cancellationToken) =>
{
    var state = await store.LoadAsync(cancellationToken);
    if (string.IsNullOrWhiteSpace(state.PrivateKeyPath) || !File.Exists(state.PrivateKeyPath))
    {
        return Results.NotFound("Generate a Tesla Fleet key before exporting tesla_fleet.key.");
    }

    return Results.File(
        state.PrivateKeyPath,
        TeslaPublicKeyContentType,
        "tesla_fleet.key");
});

app.MapGet(TeslaPublicKeyPath, async (
    TeslaFleetStore store,
    CancellationToken cancellationToken) =>
{
    var state = await store.LoadAsync(cancellationToken);
    return string.IsNullOrWhiteSpace(state.PublicKeyPem)
        ? Results.NotFound("Generate a Tesla Fleet key first.")
        : Results.Bytes(Encoding.UTF8.GetBytes(state.PublicKeyPem), TeslaPublicKeyContentType);
});

app.Run();

static async Task<TeslaFleetState> GenerateKeyAsync(
    TeslaFleetStore store,
    TeslaFleetState state,
    CancellationToken cancellationToken)
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var privateKeyPem = key.ExportECPrivateKeyPem();
    var publicKeyPem = key.ExportSubjectPublicKeyInfoPem();
    var privateKeyPath = store.PrivateKeyPath;
    Directory.CreateDirectory(Path.GetDirectoryName(privateKeyPath)!);
    await File.WriteAllTextAsync(privateKeyPath, privateKeyPem, cancellationToken);
    TrySetOwnerOnly(privateKeyPath);

    var updated = state with
    {
        PrivateKeyPath = privateKeyPath,
        PublicKeyPem = publicKeyPem,
        KeyGeneratedUtc = DateTimeOffset.UtcNow,
        LastStatus = "Key ready",
        LastMessage = "Generated a new EC P-256 key pair. Publish the public key through Edge Gateway before using it with Tesla.",
        LastChecks = []
    };
    await store.SaveAsync(updated, cancellationToken);
    return updated;
}

static void TrySetOwnerOnly(string path)
{
    try
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
    catch
    {
        // Permission tightening is best effort inside Home Assistant add-on containers.
    }
}

static string RenderPage(TeslaFleetState state)
{
    var hasKey = !string.IsNullOrWhiteSpace(state.PublicKeyPem) &&
                 !string.IsNullOrWhiteSpace(state.PrivateKeyPath) &&
                 File.Exists(state.PrivateKeyPath);
    var originDomain = string.IsNullOrWhiteSpace(state.OriginDomain) ? "tesla.example.com" : state.OriginDomain;
    var publicKeyUrl = string.IsNullOrWhiteSpace(state.PublicKeyUrl)
        ? BuildPublicKeyUrl(originDomain)
        : state.PublicKeyUrl;
    var virtualKeyUrl = string.IsNullOrWhiteSpace(state.OriginDomain)
        ? "https://tesla.com/_ak/tesla.example.com"
        : $"https://tesla.com/_ak/{state.OriginDomain}";
    var lastChecks = state.LastChecks ?? [];
    var checks = lastChecks.Count == 0
        ? "<li>No detailed checks yet.</li>"
        : string.Concat(lastChecks.Select(check => $"<li>{H(check)}</li>"));

    return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>{{ProductName}}</title>
  <style>
    :root {
      color-scheme: dark light;
      --bg: #0f141b;
      --surface: #161d27;
      --surface-2: #1d2734;
      --border: #334155;
      --text: #eef2f7;
      --muted: #a8b3c2;
      --accent: #4fd1c5;
      --accent-2: #8bd5ff;
      --danger: #ff8a8a;
      --ready: #9be7b4;
      --warning: #ffd37d;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      background: var(--bg);
      color: var(--text);
      font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }
    main { width: min(1180px, calc(100vw - 32px)); margin: 0 auto; padding: 28px 0 48px; }
    header { display: flex; justify-content: space-between; gap: 24px; align-items: flex-start; margin-bottom: 18px; }
    h1, h2, h3, p { margin-top: 0; }
    h1 { font-size: 28px; margin-bottom: 8px; }
    h2 { font-size: 18px; margin-bottom: 10px; }
    h3 { font-size: 15px; margin-bottom: 8px; }
    .meta { color: var(--muted); line-height: 1.5; max-width: 820px; }
    .grid { display: grid; grid-template-columns: 1.1fr .9fr; gap: 16px; align-items: start; }
    .cards { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; margin: 16px 0; }
    .card {
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 16px;
      box-shadow: 0 16px 36px rgba(0, 0, 0, .2);
    }
    .status {
      display: inline-flex;
      align-items: center;
      min-height: 28px;
      padding: 4px 10px;
      border: 1px solid var(--border);
      border-radius: 999px;
      color: var(--muted);
      font-size: 13px;
      white-space: nowrap;
    }
    .status.ready { color: var(--ready); border-color: rgba(155, 231, 180, .5); }
    .status.warn { color: var(--warning); border-color: rgba(255, 211, 125, .5); }
    .status.fail { color: var(--danger); border-color: rgba(255, 138, 138, .5); }
    label { display: grid; gap: 7px; margin-bottom: 12px; color: var(--muted); font-size: 13px; }
    input, textarea {
      width: 100%;
      min-height: 42px;
      border: 1px solid var(--border);
      border-radius: 7px;
      background: #0c1118;
      color: var(--text);
      padding: 10px 12px;
      font: inherit;
    }
    textarea { min-height: 220px; resize: vertical; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
    code, .value {
      display: block;
      overflow-wrap: anywhere;
      border: 1px solid var(--border);
      border-radius: 7px;
      background: #0c1118;
      padding: 10px 12px;
      color: var(--accent-2);
      font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
      font-size: 13px;
    }
    .actions { display: flex; flex-wrap: wrap; gap: 10px; align-items: center; }
    button, .button {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-height: 40px;
      border: 1px solid var(--border);
      border-radius: 7px;
      padding: 9px 13px;
      background: var(--surface-2);
      color: var(--text);
      text-decoration: none;
      cursor: pointer;
      font: inherit;
      font-weight: 650;
    }
    button.primary, .button.primary { background: var(--accent); border-color: var(--accent); color: #061316; }
    button:disabled, .button.disabled { opacity: .55; cursor: not-allowed; }
    .fact-grid { display: grid; gap: 10px; }
    .fact-grid div span { display: block; color: var(--muted); font-size: 12px; margin-bottom: 5px; }
    .callout {
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 12px 14px;
      background: var(--surface-2);
      color: var(--muted);
      line-height: 1.45;
    }
    .callout strong { color: var(--text); }
    ul { margin: 8px 0 0 18px; padding: 0; color: var(--muted); line-height: 1.55; }
    .split-actions { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
    @media (max-width: 900px) {
      main { width: min(100vw - 20px, 760px); padding-top: 18px; }
      header, .grid, .cards, .split-actions { grid-template-columns: 1fr; display: grid; }
      header { gap: 10px; }
    }
  </style>
</head>
<body>
  <main>
    <header>
      <div>
        <h1>{{ProductName}}</h1>
        <p class="meta">Generate Tesla Fleet keys, publish the public key through LMS Edge Gateway, export the private key for Home Assistant, and keep the Tesla-specific setup details in this companion add-on.</p>
      </div>
      <span class="status {{BuildStatusClass(state.LastStatus)}}">{{H(string.IsNullOrWhiteSpace(state.LastStatus) ? "Not configured" : state.LastStatus)}}</span>
    </header>

    <section class="cards">
      <div class="card"><h3>Key</h3><span class="status {{(hasKey ? "ready" : "warn")}}">{{(hasKey ? "EC P-256 ready" : "Generate required")}}</span></div>
      <div class="card"><h3>Publish</h3><span class="status {{(!string.IsNullOrWhiteSpace(state.PublicAssetId?.ToString()) ? "ready" : "warn")}}">{{(!string.IsNullOrWhiteSpace(state.PublicAssetId?.ToString()) ? "Edge Gateway asset linked" : "Not published")}}</span></div>
      <div class="card"><h3>Verify</h3><span class="status {{BuildStatusClass(state.LastStatus)}}">{{H(string.IsNullOrWhiteSpace(state.LastStatus) ? "Waiting" : state.LastStatus)}}</span></div>
    </section>

    <section class="grid">
      <div class="card">
        <h2>Setup</h2>
        <form method="post" action="actions/save-settings">
          <label>
            Edge Gateway API URL
            <input name="edge_gateway_url" value="{{H(state.EdgeGatewayUrl)}}" autocomplete="off" />
          </label>
          <label>
            Tesla origin domain
            <input name="origin_domain" value="{{H(state.OriginDomain)}}" placeholder="tesla.example.com" autocomplete="off" />
          </label>
          <div class="actions">
            <button type="submit">Save settings</button>
          </div>
        </form>
        <hr style="border:0;border-top:1px solid var(--border);margin:16px 0" />
        <div class="split-actions">
          <form method="post" action="actions/generate-key"><button type="submit">Generate / rotate key</button></form>
          <form method="post" action="actions/publish"><button class="primary" type="submit">Publish public key</button></form>
          <form method="post" action="actions/verify"><button type="submit">Verify public URL</button></form>
          <a class="button {{(hasKey ? "" : "disabled")}}" href="tesla_fleet.key">Export tesla_fleet.key</a>
        </div>
      </div>

      <div class="card">
        <h2>Tesla Developer Values</h2>
        <div class="fact-grid">
          <div><span>Origin domain</span><code>{{H(originDomain)}}</code></div>
          <div><span>Public key URL</span><code>{{H(publicKeyUrl)}}</code></div>
          <div><span>Home Assistant OAuth redirect URI</span><code>{{HomeAssistantRedirectUri}}</code></div>
          <div><span>Virtual key install URL</span><code>{{H(virtualKeyUrl)}}</code></div>
        </div>
      </div>
    </section>

    <section class="grid" style="margin-top:16px">
      <div class="card">
        <h2>Diagnostics</h2>
        <div class="callout">
          <strong>{{H(string.IsNullOrWhiteSpace(state.LastStatus) ? "No action yet" : state.LastStatus)}}</strong>
          <p style="margin:8px 0 0">{{H(string.IsNullOrWhiteSpace(state.LastMessage) ? "Save settings, generate a key, then publish through Edge Gateway." : state.LastMessage)}}</p>
          <ul>{{checks}}</ul>
        </div>
        <div class="fact-grid" style="margin-top:12px">
          <div><span>Key generated</span><code>{{FormatDate(state.KeyGeneratedUtc)}}</code></div>
          <div><span>Last published</span><code>{{FormatDate(state.LastPublishedUtc)}}</code></div>
          <div><span>Last verified</span><code>{{FormatDate(state.LastVerifiedUtc)}}</code></div>
          <div><span>Edge Gateway asset id</span><code>{{H(state.PublicAssetId?.ToString("D") ?? "None")}}</code></div>
          <div><span>Private key path</span><code>{{H(string.IsNullOrWhiteSpace(state.PrivateKeyPath) ? "Generate a key first." : state.PrivateKeyPath)}}</code></div>
        </div>
      </div>

      <div class="card">
        <h2>Public Key Preview</h2>
        <p class="meta">Only the public key should be published. The private key is exported separately as <code style="display:inline;padding:2px 5px">tesla_fleet.key</code> for Home Assistant.</p>
        <textarea readonly>{{H(string.IsNullOrWhiteSpace(state.PublicKeyPem) ? "Generate a key to see the public key." : state.PublicKeyPem)}}</textarea>
      </div>
    </section>
  </main>
</body>
</html>
""";
}

static string H(string? value) => HtmlEncoder.Default.Encode(value ?? string.Empty);

static string BuildStatusClass(string? status)
{
    var value = (status ?? string.Empty).ToLowerInvariant();
    if (value.Contains("verified", StringComparison.Ordinal) ||
        value.Contains("published", StringComparison.Ordinal) ||
        value.Contains("ready", StringComparison.Ordinal) ||
        value.Contains("saved", StringComparison.Ordinal))
    {
        return "ready";
    }

    return value.Contains("failed", StringComparison.Ordinal) ||
           value.Contains("error", StringComparison.Ordinal)
        ? "fail"
        : "warn";
}

static string FormatDate(DateTimeOffset? value) =>
    value.HasValue ? H(value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")) : "Never";

static string BuildPublicKeyUrl(string domain) =>
    string.IsNullOrWhiteSpace(domain)
        ? string.Empty
        : $"https://{domain.Trim().TrimEnd('.')}{TeslaPublicKeyPath}";

static string NormalizeHttpUrl(string value)
{
    var trimmed = (value ?? string.Empty).Trim().TrimEnd('/');
    if (string.IsNullOrWhiteSpace(trimmed))
    {
        return "http://127.0.0.1:5000";
    }

    if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
        throw new InvalidOperationException("Enter a valid Edge Gateway API URL.");
    }

    return uri.ToString().TrimEnd('/');
}

static string NormalizeDomain(string value, bool required)
{
    var trimmed = (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
    if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Enter a valid domain.");
        }

        trimmed = uri.Host.Trim().TrimEnd('.').ToLowerInvariant();
    }

    if (string.IsNullOrWhiteSpace(trimmed))
    {
        return required ? throw new InvalidOperationException("Enter the Tesla origin domain.") : string.Empty;
    }

    if (!trimmed.Contains('.', StringComparison.Ordinal) ||
        trimmed.Contains('*', StringComparison.Ordinal) ||
        trimmed.Contains('/', StringComparison.Ordinal) ||
        trimmed.Contains('\\', StringComparison.Ordinal) ||
        trimmed.Contains(' ', StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Enter a valid DNS hostname, for example tesla.example.com.");
    }

    return trimmed;
}

sealed class TeslaFleetStore(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string dataRoot = ResolvePath(
        configuration["TeslaFleetHelper:DataRoot"] ?? "/data/lms-tesla-fleet-helper");
    private readonly string optionsJsonPath = ResolvePath(
        configuration["TeslaFleetHelper:OptionsJsonPath"] ?? "/data/options.json");

    public string PrivateKeyPath => Path.Combine(dataRoot, "secrets", "tesla_fleet.key");

    private string StatePath => Path.Combine(dataRoot, "state.json");

    public async Task<TeslaFleetState> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(dataRoot);
        if (!File.Exists(StatePath))
        {
            return new TeslaFleetState(EdgeGatewayUrl: ReadDefaultEdgeGatewayUrl());
        }

        await using var stream = File.OpenRead(StatePath);
        var state = await JsonSerializer.DeserializeAsync<TeslaFleetState>(stream, JsonOptions, cancellationToken) ??
                    new TeslaFleetState();
        return state with
        {
            EdgeGatewayUrl = string.IsNullOrWhiteSpace(state.EdgeGatewayUrl)
                ? ReadDefaultEdgeGatewayUrl()
                : state.EdgeGatewayUrl,
            PublicKeyUrl = string.IsNullOrWhiteSpace(state.PublicKeyUrl) && !string.IsNullOrWhiteSpace(state.OriginDomain)
                ? TeslaFleetDefaults.BuildPublicKeyUrl(state.OriginDomain)
                : state.PublicKeyUrl,
            LastChecks = state.LastChecks ?? []
        };
    }

    public async Task SaveAsync(TeslaFleetState state, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(dataRoot);
        var temporaryPath = $"{StatePath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, StatePath, overwrite: true);
    }

    private string ReadDefaultEdgeGatewayUrl()
    {
        try
        {
            if (File.Exists(optionsJsonPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(optionsJsonPath));
                if (document.RootElement.TryGetProperty("edge_gateway_url", out var url) &&
                    !string.IsNullOrWhiteSpace(url.GetString()))
                {
                    return TeslaFleetDefaults.NormalizeHttpUrl(url.GetString()!);
                }
            }
        }
        catch
        {
            // Fall back to the standard host-network Edge Gateway control-plane URL.
        }

        return "http://127.0.0.1:5000";
    }

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
}

sealed class EdgeGatewayPublicAssetClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<PublicAssetPublishResponse> PublishAsync(
        string edgeGatewayUrl,
        string originDomain,
        string publicKeyPem,
        CancellationToken cancellationToken)
    {
        var payload = new PublicAssetPublishRequest(
            originDomain,
            TeslaFleetDefaults.PublicKeyPath,
            TeslaFleetDefaults.PublicKeyContentType,
            publicKeyPem,
            "Tesla Fleet public key",
            "no-store");
        using var response = await httpClient.PostAsJsonAsync(
            $"{edgeGatewayUrl.TrimEnd('/')}/api/public-assets/publish",
            payload,
            JsonOptions,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = Deserialize<PublicAssetPublishResponse>(body);
        if (result is not null)
        {
            return result with
            {
                Summary = string.IsNullOrWhiteSpace(result.Summary)
                    ? $"Edge Gateway returned HTTP {(int)response.StatusCode}."
                    : result.Summary
            };
        }

        return new PublicAssetPublishResponse(
            response.IsSuccessStatusCode,
            $"Edge Gateway returned HTTP {(int)response.StatusCode}. {body}",
            [],
            null);
    }

    public async Task<PublicAssetVerifyResponse> VerifyAsync(
        string edgeGatewayUrl,
        Guid publicAssetId,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(
            $"{edgeGatewayUrl.TrimEnd('/')}/api/public-assets/{publicAssetId:D}/verify",
            null,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = Deserialize<PublicAssetVerifyResponse>(body);
        if (result is not null)
        {
            return result;
        }

        return new PublicAssetVerifyResponse(
            response.IsSuccessStatusCode,
            response.IsSuccessStatusCode ? "Verified" : "Failed",
            $"Edge Gateway returned HTTP {(int)response.StatusCode}. {body}",
            []);
    }

    public async Task<PublicAssetVerifyResponse> VerifyPublicUrlAsync(
        string publicUrl,
        string expectedPublicKeyPem,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(publicUrl))
        {
            return new PublicAssetVerifyResponse(false, "Missing", "Publish the public key before verifying.", []);
        }

        using var response = await httpClient.GetAsync(publicUrl, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var checks = new List<string>
        {
            $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.",
            $"Content-Type: {response.Content.Headers.ContentType}."
        };
        var success = response.IsSuccessStatusCode &&
                      body.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) &&
                      body.Trim().Equals(expectedPublicKeyPem.Trim(), StringComparison.Ordinal);
        return new PublicAssetVerifyResponse(
            success,
            success ? "Verified" : "Failed",
            success ? "Public URL returned the Tesla Fleet public key." : "Public URL did not return the expected public key.",
            checks);
    }

    private static T? Deserialize<T>(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch
        {
            return default;
        }
    }
}

sealed record TeslaFleetState(
    string EdgeGatewayUrl = "http://127.0.0.1:5000",
    string OriginDomain = "",
    string PublicKeyPem = "",
    string PrivateKeyPath = "",
    DateTimeOffset? KeyGeneratedUtc = null,
    Guid? PublicAssetId = null,
    string PublicKeyUrl = "",
    DateTimeOffset? LastPublishedUtc = null,
    DateTimeOffset? LastVerifiedUtc = null,
    string LastStatus = "",
    string LastMessage = "",
    List<string>? LastChecks = null);

sealed record PublicAssetPublishRequest(
    string Hostname,
    string Path,
    string ContentType,
    string Content,
    string Description,
    string CacheControl);

sealed record PublicAssetPublishResponse(
    bool Succeeded,
    string Summary,
    List<string> Warnings,
    PublicAssetItem? Asset);

sealed record PublicAssetVerifyResponse(
    bool Succeeded,
    string Status,
    string Message,
    List<string> Checks);

sealed record PublicAssetItem(
    Guid Id,
    string Hostname,
    string Path,
    string ContentType,
    string PublicUrl,
    bool Enabled,
    bool RequiresAuth,
    string CacheControl);

static class TeslaFleetDefaults
{
    public const string PublicKeyPath = "/.well-known/appspecific/com.tesla.3p.public-key.pem";
    public const string PublicKeyContentType = "application/x-pem-file";

    public static string BuildPublicKeyUrl(string domain) =>
        string.IsNullOrWhiteSpace(domain)
            ? string.Empty
            : $"https://{domain.Trim().TrimEnd('.')}{PublicKeyPath}";

    public static string NormalizeHttpUrl(string value)
    {
        var trimmed = (value ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "http://127.0.0.1:5000";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Enter a valid Edge Gateway API URL.");
        }

        return uri.ToString().TrimEnd('/');
    }
}
