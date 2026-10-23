using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace TrialHater.Core.Reporting;

public sealed record Reporter(string Name, string World, byte[] Key);

// The relay's answer: the issue filed, or why not.
public sealed record ReportReply(int Number, string? Url, string? Error);

public sealed record ReportResult(bool Filed, int Number, string Message);

// Sends reports to the relay over HTTPS. The relay's certificate is self-signed: the client
// trusts exactly the public key it was built with and nothing else, CA-signed or not.
public sealed class ReportClient : IDisposable
{
    private readonly HttpClient http;
    private readonly Uri endpoint;

    public ReportClient(Uri endpoint, string spkiPin, TimeSpan timeout)
    {
        this.endpoint = endpoint;
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && SpkiPin(certificate) == spkiPin,
            },
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        this.http = new HttpClient(handler) { Timeout = timeout };
    }

    // Base64 SHA-256 of the certificate's SubjectPublicKeyInfo.
    public static string SpkiPin(X509Certificate certificate)
    {
        if (certificate is X509Certificate2 cert)
            return Pin(cert);
        using var copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return Pin(copy);
    }

    public async Task<ReportResult> SendAsync(IssueReport report, Reporter reporter, CancellationToken cancel)
    {
        var body = Gzip(JsonSerializer.SerializeToUtf8Bytes(report, ReportProtocol.Json));
        if (body.Length > ReportProtocol.MaxBodyBytes)
            return new ReportResult(false, 0, $"the report is too large ({body.Length / 1024} KiB compressed)");
        using var request = new HttpRequestMessage(HttpMethod.Post, this.endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content.Headers.ContentEncoding.Add("gzip");
        request.Headers.Add(ReportProtocol.NameHeader, Uri.EscapeDataString(reporter.Name));
        request.Headers.Add(ReportProtocol.WorldHeader, Uri.EscapeDataString(reporter.World));
        request.Headers.Add(ReportProtocol.KeyHeader, Convert.ToBase64String(reporter.Key));
        try
        {
            using var response = await this.http.SendAsync(request, cancel);
            var text = await response.Content.ReadAsStringAsync(cancel);
            var reply = ParseReply(text);
            if (response.IsSuccessStatusCode && reply is { Number: > 0 } filed)
                return new ReportResult(true, filed.Number, filed.Url ?? string.Empty);
            var why = reply?.Error ?? (text.Length > 200 ? text[..200] : text);
            return new ReportResult(false, 0, $"the relay refused it ({(int)response.StatusCode}): {why}");
        }
        catch (HttpRequestException ex)
        {
            return new ReportResult(false, 0, $"could not reach the relay: {Innermost(ex).Message}");
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            return new ReportResult(false, 0, $"the relay did not answer within {this.http.Timeout.TotalSeconds:0} s");
        }
    }

    public void Dispose() => this.http.Dispose();

    private static string Pin(X509Certificate2 cert) => Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            gzip.Write(data);
        return output.ToArray();
    }

    private static ReportReply? ParseReply(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<ReportReply>(text, ReportProtocol.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is { } inner)
            ex = inner;
        return ex;
    }
}
