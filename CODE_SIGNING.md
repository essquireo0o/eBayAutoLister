# Windows release signing

The public MSI must be Authenticode-signed and RFC 3161 timestamped. `publish-update.ps1` now
refuses to upload an unsigned, invalid, or untimestamped installer.

## Recommended: Microsoft Artifact Signing

Microsoft setup documentation:
<https://learn.microsoft.com/azure/artifact-signing/how-to-signing-integrations>

1. Create an Artifact Signing account, complete identity validation, create a Public Trust
   certificate profile, and grant the signing identity the Certificate Profile Signer role.
2. Install Microsoft's client tools:

   ```powershell
   winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
   ```

3. Create the metadata JSON described in Microsoft's Artifact Signing setup instructions.
4. Set these environment variables before publishing:

   ```powershell
   $env:ING_ARTIFACT_SIGNING_DLIB = 'C:\path\x64\Azure.CodeSigning.Dlib.dll'
   $env:ING_ARTIFACT_SIGNING_METADATA = 'C:\secure\artifact-signing-metadata.json'
   pwsh -File .\publish-update.ps1
   ```

The build signs `AutoListerB1.exe` and `AutoListerB1.dll` before WiX packages them, signs the MSI
after WiX finishes, and verifies every signature using Windows' Authenticode policy. The default
timestamp service is Microsoft's `http://timestamp.acs.microsoft.com/`.

## Alternative: certificate already in the Windows certificate store

The certificate must chain to a publicly trusted root, include the Code Signing EKU, and have its
private key in `Cert:\CurrentUser\My` or `Cert:\LocalMachine\My`.

```powershell
$env:ING_CODESIGN_THUMBPRINT = 'CERTIFICATE_SHA1_THUMBPRINT'
$env:ING_CODESIGN_TIMESTAMP_URL = 'https://your-ca.example/timestamp'
pwsh -File .\publish-update.ps1
```

Use the timestamp URL supplied by the certificate authority. Never use a self-signed certificate
for a public release; Windows gives it the same SmartScreen treatment as an unsigned file.

For a local WiX packaging diagnostic only, `build-installer.ps1 -AllowUnsigned` remains available.
That switch is intentionally absent from `publish-update.ps1`.

## False-positive submissions

A valid signature identifies the publisher but does not guarantee that a new download immediately
has SmartScreen reputation. If Microsoft Defender names a malware or PUA detection (rather than
showing only “Windows protected your PC”), submit the exact signed MSI as a software developer at
Microsoft's Security Intelligence file-submission portal and retain the submission ID.

Submission portal: <https://www.microsoft.com/wdsi/filesubmission>
