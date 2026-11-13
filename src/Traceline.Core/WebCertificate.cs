using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Traceline.Core.Notifications;

namespace Traceline.Core;

/// <summary>
/// The certificate the web UI uses for HTTPS: a file the administrator points at, or one Traceline makes for itself
/// and keeps in its data folder. A self-made certificate encrypts the connection, but browsers warn about it
/// because nobody they trust has vouched for it.
/// </summary>
public static class WebCertificate
{
    public const string OwnFileName = "https-certificate.pfx";

    /// <summary>Loads a .pfx/.p12 file, or a .pem/.crt file that holds the key too or has it next to it as a .key file.</summary>
    public static X509Certificate2 Load(string path, string password)
    {
        if (!File.Exists(path)) throw new ArgumentException($"The certificate file {path} does not exist.");
        try
        {
            X509Certificate2 certificate;
            if (Path.GetExtension(path).ToLowerInvariant() is ".pfx" or ".p12")
            {
                certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);
            }
            else
            {
                var keyFile = Path.ChangeExtension(path, ".key");
                using var pem = password.Length > 0
                    ? X509Certificate2.CreateFromEncryptedPemFile(path, password, File.Exists(keyFile) ? keyFile : null)
                    : X509Certificate2.CreateFromPemFile(path, File.Exists(keyFile) ? keyFile : null);
                // Windows cannot serve HTTPS with a key that only exists in memory; going through PKCS#12 gives it one it can use.
                certificate = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
            }
            if (!certificate.HasPrivateKey) throw new ArgumentException("The certificate file has no private key in it, so it cannot be used for HTTPS.");
            return certificate;
        }
        catch (CryptographicException ex)
        {
            throw new ArgumentException($"The certificate file could not be read: {ex.Message}");
        }
    }

    /// <summary>The self-made certificate from the data folder; made on first use and again shortly before it runs out.</summary>
    public static X509Certificate2 LoadOrCreateOwn(string dataFolder, AppSettings settings, Action save)
    {
        var path = Path.Combine(dataFolder, OwnFileName);
        if (File.Exists(path) && settings.OwnCertificatePassword.Length > 0)
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, SecretProtector.Unprotect(settings.OwnCertificatePassword));
                if (existing.NotAfter > DateTime.Now.AddDays(30)) return existing;
            }
            catch (CryptographicException) { /* unreadable: make a new one */ }
        }
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        File.WriteAllBytes(path, CreateSelfSigned(password));
        settings.OwnCertificatePassword = SecretProtector.Protect(password);
        save();
        return X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }

    /// <summary>A five-year certificate for this computer's names and addresses, as a password-protected PKCS#12 file.</summary>
    public static byte[] CreateSelfSigned(string password)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var machine = Environment.MachineName;
        var request = new CertificateRequest($"CN={machine}, O=Traceline", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddDnsName(machine);
        try { var full = Dns.GetHostEntry("").HostName; if (!string.Equals(full, machine, StringComparison.OrdinalIgnoreCase)) names.AddDnsName(full); }
        catch (SocketException) { }
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (var address in LocalAddresses()) names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));   // server authentication
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        return certificate.Export(X509ContentType.Pkcs12, password);
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
                .Where(a => !a.IsIPv6LinkLocal).Distinct().ToList();
        }
        catch (NetworkInformationException) { return []; }
    }
}
