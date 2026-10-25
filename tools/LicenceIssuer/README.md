# KynexOne licence issuer (vendor only)

Signs the licences that client installations import. **Never ship this tool, a private key or a signed
licence to a client repository or image.** The product only contains public keys
(`backend-dotnet/Zayra.Api/Infrastructure/Licensing/Keys/<kid>.pub.pem`).

The tool links the product's own `LicencePayload.cs`, `FeatureKeys.cs`, `ModuleKeys.cs` and `OptInFeatures.cs`,
so it refuses exactly the licences the product would refuse.

## Build

```bash
dotnet build tools/LicenceIssuer -c Release
# or run without installing:
dotnet run --project tools/LicenceIssuer -- <command> ...
```

## Keys

Each signing key has a **kid** (key id, e.g. `2026-01`). The product verifies every licence against the public
key of its kid, so several keys can be valid at once and a key can be rotated without invalidating licences
already issued.

Generate a key pair with the tool:

```bash
dotnet run --project tools/LicenceIssuer -- keygen --kid 2027-01 --out-dir /secure/kynexone-keys
```

or with OpenSSL:

```bash
openssl ecparam -name prime256v1 -genkey -noout -out kynexone-2027-01.key
openssl ec -in kynexone-2027-01.key -pubout -out 2027-01.pub.pem
```

Then:

1. Store `kynexone-<kid>.key` in the vendor's secret vault (password manager / HSM / KMS-wrapped secret).
   Never commit it, never email it, never copy it onto a build agent that publishes artefacts.
2. Copy `<kid>.pub.pem` into `backend-dotnet/Zayra.Api/Infrastructure/Licensing/Keys/` and release a product
   version. Licences signed with the new kid are accepted only by versions that contain its public key.
3. To retire a key, stop signing with it; remove its public key only once no client licence uses it.

The first key, `2026-01`, was generated on 2026-10-25. Its private key was written to
`C:\Users\pc\KynexOne-Vendor-Keys\kynexone-2026-01.key` on the developer workstation and must be moved into the
vault.

## Issue a licence

1. Ask the client for the **installation id** (Organisation Admin → Licence, or
   `dotnet Zayra.Api.dll --licence-status`) if the licence should work on that installation only.
2. Write a request (see `example-request.json`). Fields:

   | Field | Meaning |
   |---|---|
   | `licenceId` | Unique id, e.g. `ACME-2026-0001`. Re-importing the same id + content is a no-op. |
   | `licensee` | Client's legal name. |
   | `installationId` | Binds the licence to one installation; `null` = any. |
   | `edition` | e.g. `Standard`, `Enterprise`. |
   | `expiresAtUtc` | `null` = perpetual. |
   | `graceDays` | Days after expiry during which everything still works (with a banner). |
   | `supportExpiresAtUtc` | Upgrades are refused after it; `null` = no limit. |
   | `accountType` | `SingleCompany` or `Group`. |
   | `maxEmployees`, `maxUsers`, `maxCompanies`, `maxAdminUsers` | `0` = unlimited. |
   | `aiMonthlyTokenLimit` | Monthly AI tokens; `0` = AI disabled. |
   | `modules` | Optional module keys (`ModuleKeys`). Core and statutory modules are always on. |
   | `optInFeatures` | Opt-in feature keys (`OptInFeatures`). |
   | `countries` | Allowed country packs (ISO-2), `null` = all. |

3. Sign:

   ```bash
   dotnet run --project tools/LicenceIssuer -- issue \
     --key /secure/kynexone-keys/kynexone-2026-01.key --kid 2026-01 \
     --in acme-request.json --out acme.licence
   ```

4. Optionally check it: `... verify --pub 2026-01.pub.pem --kid 2026-01 --in acme.licence`.
5. Send `acme.licence` to the client. They import it in Organisation Admin → Licence, with
   `dotnet Zayra.Api.dll --import-licence acme.licence`, or by setting `Licence__Path`.
