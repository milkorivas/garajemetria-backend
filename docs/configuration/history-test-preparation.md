# RF-005 — Preparación de pruebas

Usar el SDK de `global.json`, SQL Server aislado y datos sintéticos.
`SqlFixture` crea una base `GarajeMetria_AuthTests_<UUID>` y verifica su nombre
antes de retirarla. En Windows usa conexión local por memoria compartida con
autenticación integrada; `GARAJEMETRIA_TEST_SQL` permite indicar otra instancia
aislada. No imprimir esa variable ni apuntarla a producción.

Un error SSPI del proceso aislado no equivale a fallo funcional de RF-005:
registrar el intento y ejecutar solo sus pruebas afectadas con acceso Windows
local autorizado. No reemplazar SQL Server por InMemory/SQLite ni omitir
silenciosamente integración. Los hosts HTTP usan `AuthenticationFactory`,
certificados/usuarios/talleres sintéticos y `TestDirectory`; no necesitan AD real.

## Comandos focalizados

Desde la raíz, tras restore/build cuando correspondan:

```powershell
dotnet test tests/GarajeMetria.Application.Tests/GarajeMetria.Application.Tests.csproj --no-restore --configuration Release --filter FullyQualifiedName~History --logger trx --results-directory artifacts/agent-checks/rf005-application
dotnet test tests/GarajeMetria.IntegrationTests/GarajeMetria.IntegrationTests.csproj --no-restore --configuration Release --filter 'FullyQualifiedName~LprHttpTests.History|FullyQualifiedName~HistoryConcurrencyTests' --blame-hang-timeout 120s --blame-hang-dump-type none --logger trx --results-directory artifacts/agent-checks/rf005-http-focused
dotnet test tests/GarajeMetria.ArchitectureTests/GarajeMetria.ArchitectureTests.csproj --no-restore --configuration Release --logger trx --results-directory artifacts/agent-checks/rf005-architecture
```

No lanzar otra compilación sobre un ensamblado que siga abierto en testhost.
Esperar a terminar o diagnosticar el bloqueo de esa ejecución concreta. Guardar
TRX y logs bajo `artifacts/agent-checks/`, ignorados por Git. Los filtros son
ejemplos vinculados a clases reales; verificar que descubren pruebas.

La suite completa sigue la frecuencia diaria de AGENTS.md: comprobar historial
y `artifacts/agent-checks/full-suite.json` antes de lanzarla. Un intento fallido
también cuenta para el día; las correcciones se verifican con filtros.

## Escenarios y tiempo

Preparar al menos dos talleres, cámaras con versiones de ubicación e inactivos,
usuarios Admin/Asesor y eventos válidos/invalidos/repetidos/sin asociación.
La confianza actual es obligatoria en RF-003; probar cero como valor real y
`missing` como resultado vacío, sin alterar el esquema de ingesta.
Las pruebas de calendario son deterministas y las de cierre avanzan `ILprClock`;
no esperan a una medianoche física. Dos contextos/hosts independientes son
necesarios para comprobar locks y leases compartidas.

La carga Excel sintética de 10.000 filas registra tiempo/tamaño en
`history-load-evidence/10000-rows.json` bajo el directorio de salida de pruebas.
Copiar la evidencia a los artefactos del tramo al consolidar, sin atribuirle
rendimiento de producción o un SLA. El límite de 10.001 se verifica por rechazo,
no por truncamiento del archivo.
