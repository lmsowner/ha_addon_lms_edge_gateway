using System.Net;
using System.Text;

namespace LMS.EdgeGateway.Core;

public sealed record EdgeGatewayAccessDiagnosticCheck(
    string Label,
    string Status,
    string Detail,
    bool IsOk = false);

public sealed record EdgeGatewayAccessDiagnostics(
    string Title,
    string Summary,
    string RouteName,
    string PublicHostname,
    string AccessPolicy,
    string SourceIp,
    string SourceIpHeader,
    string CloudflareConnectingIp,
    string CountryCode,
    string UserAgent,
    IReadOnlyList<EdgeGatewayAccessDiagnosticCheck> Checks,
    IReadOnlyList<string> NextSteps);

public static class EdgeGatewayAccessDiagnosticsPage
{
    public static string Render(EdgeGatewayAccessDiagnostics diagnostics)
    {
        var checks = new StringBuilder();
        foreach (var check in diagnostics.Checks)
        {
            var tone = check.IsOk ? "ok" : "warn";
            checks.Append($$"""
                <div class="check {{tone}}">
                  <div class="check-label">{{Html(check.Label)}}</div>
                  <div class="check-status">{{Html(check.Status)}}</div>
                  <div class="check-detail">{{Html(check.Detail)}}</div>
                </div>
                """);
        }

        var steps = new StringBuilder();
        foreach (var step in diagnostics.NextSteps)
        {
            steps.Append($"<li>{Html(step)}</li>");
        }

        var country = string.IsNullOrWhiteSpace(diagnostics.CountryCode) ? "Unknown" : diagnostics.CountryCode;
        var cloudflareIp = string.IsNullOrWhiteSpace(diagnostics.CloudflareConnectingIp)
            ? "Not present on this request"
            : diagnostics.CloudflareConnectingIp;
        var userAgent = string.IsNullOrWhiteSpace(diagnostics.UserAgent) ? "Unknown" : diagnostics.UserAgent;
        var sourceHeader = string.IsNullOrWhiteSpace(diagnostics.SourceIpHeader)
            ? "Unknown"
            : diagnostics.SourceIpHeader;

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{Html(diagnostics.Title)}} | LMS Edge Gateway</title>
              <style>
                :root { color-scheme: light dark; }
                body { align-items: center; background: #eef3f8; color: #142033; display: flex; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; justify-content: center; margin: 0; min-height: 100vh; padding: 20px; }
                main { background: #fff; border: 1px solid #dce7f3; border-radius: 18px; box-shadow: 0 18px 60px rgba(17, 34, 58, .14); max-width: 720px; padding: 28px; width: 100%; }
                h1 { color: #b45309; font-size: 24px; margin: 0 0 10px; }
                p { color: #607089; font-size: 15px; line-height: 1.5; margin: 0 0 16px; }
                .eyebrow { color: #0f7b57; font-size: 11px; font-weight: 900; letter-spacing: .08em; margin: 0 0 8px; text-transform: uppercase; }
                .facts, .checks { display: grid; gap: 10px; margin: 0 0 18px; }
                .fact, .check { background: #f7f9fc; border: 1px solid #e3edf7; border-radius: 12px; padding: 12px 14px; }
                .fact.emphasis { background: #eef8f3; border-color: #b7e2d1; }
                .fact span, .check-label { color: #607089; display: block; font-size: 11px; font-weight: 900; letter-spacing: .08em; text-transform: uppercase; }
                .fact strong, .check-status { display: block; font-size: 15px; margin-top: 4px; overflow-wrap: anywhere; }
                .check-detail { color: #607089; font-size: 13px; line-height: 1.4; margin-top: 6px; overflow-wrap: anywhere; }
                .check.ok { border-color: #b7e2d1; }
                .check.ok .check-status { color: #0f7b57; }
                .check.warn .check-status { color: #b45309; }
                h2 { font-size: 14px; margin: 0 0 10px; }
                ol { color: #607089; font-size: 14px; line-height: 1.5; margin: 0; padding-left: 1.2rem; }
                .note { color: #607089; font-size: 12px; line-height: 1.4; margin: 18px 0 0; }
                @media (prefers-color-scheme: dark) {
                  body { background: #0b1118; color: #edf4ff; }
                  main { background: #121b26; border-color: #26384d; box-shadow: none; }
                  p, .fact span, .check-label, .check-detail, ol, .note { color: #9fb0c5; }
                  .fact, .check { background: #182433; border-color: #2a3d52; }
                  .fact.emphasis { background: #143028; border-color: #1f5f48; }
                  .check.ok { border-color: #1f5f48; }
                }
              </style>
            </head>
            <body>
              <main>
                <p class="eyebrow">Edge Gateway access check</p>
                <h1>{{Html(diagnostics.Title)}}</h1>
                <p>{{Html(diagnostics.Summary)}}</p>
                <div class="facts">
                  <div class="fact"><span>App</span><strong>{{Html(diagnostics.RouteName)}}</strong></div>
                  <div class="fact"><span>Host</span><strong>{{Html(diagnostics.PublicHostname)}}</strong></div>
                  <div class="fact"><span>Access policy</span><strong>{{Html(diagnostics.AccessPolicy)}}</strong></div>
                  <div class="fact emphasis"><span>CF-Connecting-IP (Cloudflare client IP)</span><strong>{{Html(cloudflareIp)}}</strong></div>
                  <div class="fact emphasis"><span>IP used for Known source IPs / email approve</span><strong>{{Html(diagnostics.SourceIp)}}</strong></div>
                  <div class="fact"><span>IP taken from</span><strong>{{Html(sourceHeader)}}</strong></div>
                  <div class="fact"><span>Country (CF-IPCountry)</span><strong>{{Html(country)}}</strong></div>
                  <div class="fact"><span>User-Agent</span><strong>{{Html(userAgent)}}</strong></div>
                </div>
                <h2>Auth checks</h2>
                <div class="checks">
                  {{checks}}
                </div>
                <h2>What to try next</h2>
                <ol>
                  {{steps}}
                </ol>
                <p class="note">Return 404 on the route is optional. While it is off, every unapproved client sees this access check page — including on production.</p>
              </main>
            </body>
            </html>
            """;
    }

    private static string Html(string? value) =>
        WebUtility.HtmlEncode(value ?? string.Empty);
}
