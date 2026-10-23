# GarajeMetría — Generación de secretos

## Configuración Variable de entorno que indica la ruta del archivo de configuración privado

### Máquina
```powershell
[Environment]::SetEnvironmentVariable(
    "GARAJEMETRIA_PRIVATE_CONFIG",
    "C:\GarajeMetria\config\private.json",
    "Machine"
)
```
### Usuario
```powershell
[Environment]::SetEnvironmentVariable(
    "GARAJEMETRIA_PRIVATE_CONFIG",
    "C:\GarajeMetria\config\private.json",
    "User"
)
```



## Configuración Http

Para estos dos parámetros (más la contraseña) que se enceuntran en la sección Http:

```list
   1.DataProtectionKeyPath
   2.DataProtectionCertificatePath
```

Ejecutar en PowerShell:


```powershell
$protectionRoot = Join-Path $env:LOCALAPPDATA 'GarajeMetria\DataProtection'
$keysPath = Join-Path $protectionRoot 'keys'
$pfxPath = Join-Path $protectionRoot 'data-protection-development.pfx'

New-Item -ItemType Directory -Path $keysPath -Force | Out-Null

$pfxPassword = Read-Host 'Elige una contraseña para el PFX' -AsSecureString

$certificateParameters = @{
    Type              = 'Custom'
    Subject           = 'CN=GarajeMetria-DataProtection-Development'
    KeyAlgorithm      = 'RSA'
    KeyLength         = 3072
    HashAlgorithm     = 'SHA256'
    KeyExportPolicy   = 'Exportable'
    KeyUsage          = @('KeyEncipherment', 'DataEncipherment')
    CertStoreLocation = 'Cert:\CurrentUser\My'
    NotAfter          = (Get-Date).AddYears(1)
}

$certificate = New-SelfSignedCertificate @certificateParameters

Export-PfxCertificate `
    -Cert $certificate `
    -FilePath $pfxPath `
    -Password $pfxPassword `
    -NoClobber | Out-Null
```

Y luego obtener los valores con:

```powershell
[ordered]@{
    DataProtectionKeyPath             = $keysPath
    DataProtectionCertificatePath     = $pfxPath
    DataProtectionCertificatePassword = '<la contraseña que acabas de elegir>'
} | ConvertTo-Json
```

## Configuración Jwt

Para preparar un certificado de firma, ejecuta en PowerShell:

```powershell
$jwtDirectory = Join-Path $env:LOCALAPPDATA 'GarajeMetria\Jwt'
$pfxPath = Join-Path $jwtDirectory 'jwt-signing-development.pfx'

New-Item -ItemType Directory -Path $jwtDirectory -Force | Out-Null

$pfxPassword = Read-Host 'Elige una contraseña para el PFX de JWT' -AsSecureString

$certificateParameters = @{
    Type              = 'Custom'
    Subject           = 'CN=GarajeMetria-Jwt-Development'
    KeyAlgorithm      = 'RSA'
    KeyLength         = 3072
    HashAlgorithm     = 'SHA256'
    KeyExportPolicy   = 'Exportable'
    KeyUsage          = 'DigitalSignature'
    CertStoreLocation = 'Cert:\CurrentUser\My'
    NotAfter          = (Get-Date).AddYears(1)
}

$certificate = New-SelfSignedCertificate @certificateParameters

Export-PfxCertificate `
    -Cert $certificate `
    -FilePath $pfxPath `
    -Password $pfxPassword `
    -NoClobber | Out-Null
```

Estos comandos crean un certificado autofirmado para pruebas y lo exportan a PFX.
Para obtener el bloque con tu ruta real:

```powershell
@{
    Jwt = [ordered]@{
        Issuer                     = 'GarajeMetria.Development'
        AccessAudience             = 'GarajeMetria.Api.Access'
        RefreshAudience            = 'GarajeMetria.Api.Refresh'
        ClockSkewSeconds           = 0
        SigningCertificatePath     = $pfxPath
        SigningCertificatePassword = '<la contraseña que acabas de elegir>'
        SigningKeyId               = 'garajemetria-dev-001'
        VerificationKeys           = @()
    }
} | ConvertTo-Json -Depth 4
```