using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TrialHater.Core.Reporting;
using TrialHater.ReportRelay;

// The report relay (docs/REPORTING.md). Takes reports from the plugin over HTTPS and files them as
// issues in the private reports repo; runs as the TrialHaterRelay service on the compute box.
//   ReportRelay make-cert <folder>   writes <folder>/relay.pfx and prints the pin the plugin needs
//   ReportRelay pin <relay.pfx>      prints the pin of an existing certificate
//   ReportRelay --data <folder> [--port 8443] [--listen 0.0.0.0] [--repo owner/name]
// The data folder holds relay.pfx, github.token, allowlist.json, relay.log and failed/.
switch (args.FirstOrDefault())
{
    case "make-cert" when args.Length == 2:
        return MakeCertificate(args[1]);
    case "pin" when args.Length == 2:
        using (var cert = X509CertificateLoader.LoadPkcs12FromFile(args[1], null))
            Console.WriteLine(ReportClient.SpkiPin(cert));
        return 0;
}

var data = Option("--data");
if (data is null)
{
    Console.Error.WriteLine("usage: ReportRelay --data <folder> [--port 8443] [--listen 0.0.0.0] [--repo owner/name] | make-cert <folder> | pin <relay.pfx>");
    return 2;
}

var port = int.Parse(Option("--port") ?? "8443");
var listen = IPAddress.Parse(Option("--listen") ?? "0.0.0.0");
var repo = Option("--repo") ?? "Tihlyn/TrialHater-reports";
var log = new RelayLog(Path.Combine(data, "relay.log"));
var tokenPath = Path.Combine(data, "github.token");
if (!File.Exists(tokenPath))
{
    log.Write($"no GitHub token at {tokenPath}; stopping");
    return 1;
}

// The machine key store: a user store needs the account's DPAPI keys, which a key-based SSH logon
// (and maybe the service account) does not have. The key is removed again when the relay stops.
using var certificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(data, "relay.pfx"), null, X509KeyStorageFlags.MachineKeySet);
using var github = new GitHubReports(repo, File.ReadAllText(tokenPath).Trim());
var relay = new Relay(Path.Combine(data, "allowlist.json"), Path.Combine(data, "failed"), github, log);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(o => o.ServiceName = "TrialHaterRelay");
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = ReportProtocol.MaxBodyBytes;
    kestrel.Limits.MaxConcurrentConnections = 64;
    kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    kestrel.Listen(listen, port, options => options.UseHttps(certificate));
});

var app = builder.Build();
app.MapGet("/v1/health", () => Results.Text("ok"));
Func<HttpContext, Task<IResult>> report = relay.HandleAsync;
app.MapPost(ReportProtocol.Path, report);
log.Write($"listening on {listen}:{port}, filing into {repo}, certificate pin {ReportClient.SpkiPin(certificate)}");
app.Run();
return 0;

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// Self-signed: the plugin pins the public key, so no CA or host name is involved. A new
// certificate means a new pin and a plugin release.
static int MakeCertificate(string folder)
{
    Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, "relay.pfx");
    if (File.Exists(path))
    {
        Console.Error.WriteLine($"{path} exists; the plugin's pin changes with a new one, so delete it by hand first");
        return 1;
    }

    using var rsa = RSA.Create(3072);
    var request = new CertificateRequest("CN=TrialHater report relay", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
    using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
    File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
    Console.WriteLine(ReportClient.SpkiPin(cert));
    return 0;
}
