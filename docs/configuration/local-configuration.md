# GarajeMetría — Generación de secretos

## Logs JSON con Serilog

Añadir `FileLogging` al JSON privado señalado por `GARAJEMETRIA_PRIVATE_CONFIG`:

```json
{
  "FileLogging": {
    "DirectoryPath": "C:\\ProgramData\\GarajeMetria\\logs",
    "FileSizeLimitBytes": 20971520,
    "RetainedFileCountLimit": 30,
    "RetainedDays": 14
  }
}
```

La ruta es obligatoria, absoluta y externa al content root y al directorio
publicado. También puede fijarse con `FileLogging__DirectoryPath` si el archivo
privado no establece ese valor (el archivo privado se carga con prioridad).
Conceder a la identidad del proceso/IIS permisos de creación, escritura,
lectura y eliminación solo en ese directorio; restringir su lectura a personal
autorizado. El arranque comprueba que puede crear, vaciar y eliminar un archivo.
Un destino inaccesible impide el arranque.

Se escribe un evento JSON compacto por línea, en archivos
`garajemetria-<PID>-<fecha>[_secuencia].json`. El PID separa procesos de IIS
solapados. La rotación ocurre cada día y al alcanzar 20 MiB: el último evento
se escribe completo, por lo que el límite no es una cuota exacta de disco.
Se conservan hasta 30 archivos por PID y hasta 14 días, evaluados al abrir o
rotar un archivo. También se eliminan por antigüedad archivos de procesos
anteriores con ese patrón exacto. Mientras todos los procesos estén detenidos
no se ejecuta limpieza. El límite de archivos es por PID, no por servidor.

Los niveles se configuran mediante `Serilog:MinimumLevel` en appsettings o en
el JSON privado; `Default` es `Information`, y `Override:Microsoft` es `Warning`.
La sección anterior `Logging:LogLevel` se sustituye por `Serilog:MinimumLevel`;
actualizar también las variables de entorno que configuraban esos niveles.
Para investigar el inicio/cancelación de casos de uso puede usarse:

```json
{ "Serilog": { "MinimumLevel": { "Override": {
  "GarajeMetria.Application.Diagnostics": "Debug"
} } } }
```

No habilitar sinks adicionales ni captura de datos sensibles. Los filtros de
privacidad se aplican independientemente del nivel. Serilog se crea por host,
sin logger estático compartido; al detener y disponer el host se vacía el sink.

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
