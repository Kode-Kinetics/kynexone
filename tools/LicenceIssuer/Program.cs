using System.Reflection;
using System.Security.Cryptography;
using Zayra.Api.Infrastructure.Licensing;
using Zayra.Api.Infrastructure.Modules;

// KynexOne licence issuer — vendor side only.
//
//   keygen --kid <kid> --out-dir <dir>
//       A new ECDSA P-256 key pair: <dir>/kynexone-<kid>.key (PRIVATE — vendor vault only) and <dir>/<kid>.pub.pem
//       (copy into backend-dotnet/Zayra.Api/Infrastructure/Licensing/Keys/ and release).
//
//   issue --key <private.pem> --kid <kid> --in <request.json> --out <client.licence> [--all-modules yes]
//       Signs a licence. request.json holds the payload fields (camelCase, see LicencePayload). issuedAtUtc and
//       notBeforeUtc default to now; formatVersion to the current one.
//
//   verify --pub <kid.pub.pem> --kid <kid> --in <client.licence>
//       Checks a licence's signature and prints what it grants.
//
// Exit codes: 0 done; 1 refused (the reason is printed); 2 usage.

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0) return Usage();
    var options = ParseOptions(args.Skip(1).ToArray());
    try
    {
        return args[0] switch
        {
            "keygen" => KeyGen(options),
            "issue" => Issue(options),
            "verify" => Verify(options),
            _ => Usage(),
        };
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
    {
        Console.Error.WriteLine($"Refused: {ex.Message}");
        return 1;
    }
}

static int KeyGen(IReadOnlyDictionary<string, string> o)
{
    if (!o.TryGetValue("kid", out var kid) || !o.TryGetValue("out-dir", out var dir)) return Usage();
    Directory.CreateDirectory(dir);
    var privatePath = Path.Combine(dir, $"kynexone-{kid}.key");
    var publicPath = Path.Combine(dir, $"{kid}.pub.pem");
    if (File.Exists(privatePath) || File.Exists(publicPath))
    {
        Console.Error.WriteLine($"Refused: a key for kid '{kid}' already exists in {dir}. Keys are never overwritten.");
        return 1;
    }
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem());
    File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem());
    Console.WriteLine($"Private key: {privatePath}  <- store in the vendor vault; never commit, never ship.");
    Console.WriteLine($"Public key:  {publicPath}  <- copy to backend-dotnet/Zayra.Api/Infrastructure/Licensing/Keys/");
    return 0;
}

static int Issue(IReadOnlyDictionary<string, string> o)
{
    if (!o.TryGetValue("key", out var keyPath) || !o.TryGetValue("kid", out var kid)
        || !o.TryGetValue("in", out var requestPath) || !o.TryGetValue("out", out var outPath)) return Usage();

    var request = LicencePayload.Parse(File.ReadAllBytes(requestPath))
                  ?? throw new ArgumentException($"{requestPath} is not a readable licence request.");
    var now = DateTime.UtcNow;
    var payload = request with
    {
        IssuedAtUtc = request.IssuedAtUtc == default ? now : request.IssuedAtUtc,
        NotBeforeUtc = request.NotBeforeUtc == default ? now : request.NotBeforeUtc,
        FormatVersion = LicencePayload.CurrentFormatVersion,
    };
    // --all-modules yes: every module and opt-in feature this build of the product knows (CI and test licences).
    if (o.TryGetValue("all-modules", out var all) && all is "yes" or "true")
        payload = payload with
        {
            Modules = KnownModules().Order(StringComparer.Ordinal).ToList(),
            OptInFeatures = OptInFeatures.Keys.Order(StringComparer.Ordinal).ToList(),
        };

    var problems = payload.Problems(KnownModules(), OptInFeatures.Keys.ToHashSet(StringComparer.Ordinal), now.AddMinutes(5));
    if (problems.Count > 0)
    {
        Console.Error.WriteLine("Refused — the licence would be rejected by the product:");
        foreach (var p in problems) Console.Error.WriteLine($"  - {p}");
        return 1;
    }

    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(keyPath));
    var bytes = payload.ToUtf8Bytes();
    var signature = key.SignData(bytes, HashAlgorithmName.SHA256);
    var file = new LicenceFile(Base64Url.Encode(bytes), Base64Url.Encode(signature), kid);
    File.WriteAllText(outPath, file.ToJson());
    Console.WriteLine($"Issued {payload.LicenceId} for {payload.Licensee} ({payload.Edition}) -> {outPath}");
    return 0;
}

static int Verify(IReadOnlyDictionary<string, string> o)
{
    if (!o.TryGetValue("pub", out var pubPath) || !o.TryGetValue("kid", out var kid) || !o.TryGetValue("in", out var path))
        return Usage();
    var file = LicenceFile.Parse(File.ReadAllText(path)) ?? throw new ArgumentException($"{path} is not a licence file.");
    if (file.Kid != kid) { Console.Error.WriteLine($"Refused: the licence was signed with kid '{file.Kid}', not '{kid}'."); return 1; }
    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(pubPath));
    var bytes = Base64Url.Decode(file.Payload) ?? throw new ArgumentException("The payload is not base64url.");
    var signature = Base64Url.Decode(file.Signature) ?? throw new ArgumentException("The signature is not base64url.");
    if (!key.VerifyData(bytes, signature, HashAlgorithmName.SHA256)) { Console.Error.WriteLine("Refused: the signature is not valid."); return 1; }
    Console.WriteLine("Signature valid. Payload:");
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(bytes));
    return 0;
}

/// <summary>Every module key the product knows: the constants of ModuleKeys.</summary>
static IReadOnlySet<string> KnownModules() => typeof(ModuleKeys)
    .GetFields(BindingFlags.Public | BindingFlags.Static)
    .Where(f => f.IsLiteral && f.FieldType == typeof(string))
    .Select(f => (string)f.GetRawConstantValue()!)
    .ToHashSet(StringComparer.Ordinal);

static Dictionary<string, string> ParseOptions(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i + 1 < args.Length; i += 2)
        if (args[i].StartsWith("--", StringComparison.Ordinal)) options[args[i][2..]] = args[i + 1];
    return options;
}

static int Usage()
{
    Console.Error.WriteLine("""
        kynexone-licence-issuer keygen --kid <kid> --out-dir <dir>
        kynexone-licence-issuer issue  --key <private.pem> --kid <kid> --in <request.json> --out <client.licence> [--all-modules yes]
        kynexone-licence-issuer verify --pub <kid.pub.pem> --kid <kid> --in <client.licence>
        """);
    return 2;
}
