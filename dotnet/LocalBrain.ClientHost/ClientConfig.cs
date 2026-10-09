using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LocalBrain.ClientHost;

internal sealed record ClientConfig(
    string GatewayBaseUrl,
    int BridgePort,
    string ClientCertificateThumbprint,
    string ProjectsFile,
    string AuditLog,
    int HeartbeatSeconds = 60,
    bool PersonalValidation = false)
{
    public static ClientConfig Load()
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Environment.GetEnvironmentVariable("LOCALBRAIN_CLIENT_CONFIG") ?? Path.Combine(baseDir, "clientsettings.json");
        var config = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("clientsettings.json is invalid");
        if (!Uri.TryCreate(config.GatewayBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new InvalidOperationException("GatewayBaseUrl must be an HTTPS URL");
        return config with {
            ProjectsFile = Expand(config.ProjectsFile, baseDir),
            AuditLog = Expand(config.AuditLog, baseDir)
        };
    }

    public X509Certificate2 LoadCertificate()
    {
        var thumbprint = ClientCertificateThumbprint.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var cert = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: true)
            .OfType<X509Certificate2>().SingleOrDefault(c => c.HasPrivateKey);
        return cert ?? throw new InvalidOperationException("A valid LocalBrain client certificate with private key was not found");
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string Expand(string value, string baseDir) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(value), baseDir);
}
