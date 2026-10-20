# TASKS-001 — Autenticación

| Campo | Valor |
| --- | --- |
| Requerimiento | [RF-001](../requirements/RF-001-authentication.md) |
| Diseño | [DESIGN-001](../design/DESIGN-001-authentication.md) |
| Integraciones posteriores | [TASKS-002](TASKS-002-roles-and-workshop-access.md) y RF-009; sin dependencia de ejecución |
| Estado | Código de RF-001 incorporado; verificaciones de AD real y restauración física aislada pendientes |

Las casillas representan trabajo implementado y verificado, no la redacción de
documentación. No marcar una tarea completa por disponer de su diseño. Los
CA y CP citados pertenecen a RF-001; su correspondencia se define en DESIGN-001 / 10.1. Mantener este plan cuando cambie el alcance.

RF-001 es un tramo independiente: todas sus tareas pueden completarse sin
implementar T002 ni la sincronización de RF-009. Incluye resolución AD de roles,
perfil y asignación local leída, auditoría de aprovisionamiento y persistencia
propias del login. Los tests preparan datos sintéticos de acceso en SQL aislado;
no se introduce una carga ficticia ni un bypass en producción.
La auditoría administrativa de asignaciones y sus productores se integran después.

## 1. Preparación

- [x] **T001-01 — Revisar el alcance y los contratos definidos de autenticación.**
  Usar las 19 reglas funcionales de RF-001 y los contratos técnicos de
  DESIGN-001 como base. Mantener la trazabilidad RN → CA → CP de DESIGN-001 /
  10.1 y 13 al implementar y verificar este tramo. Las decisiones
  funcionales de RF-001 están cerradas, incluidos JWT, errores de infraestructura,
  limitación compartida, cookie de refresh y retención de la base operativa.
  Identificar los contratos compartidos con RF-002/RF-009 y distinguir las
  pruebas propias de autenticación de las pruebas de integración entre tramos.
  La configuración del entorno corresponde a T001-02 y no condiciona el estado
  de definición del requisito. Resultado: alcance de implementación y pruebas
  trazable a los contratos acordados.
- [ ] **T001-02 — Preparar los entornos de verificación.**
  Documentar SQL Server aislado, cuentas/grupos sintéticos AD, certificados LDAPS
  y configuración privada externa. Comprobar permisos de lectura con el
  responsable de AD, sin producción ni secretos versionados. Puede avanzar
  en paralelo con la definición y las pruebas unitarias.

## 2. Implementación

- [x] **T001-03 — Implementar invariantes de sesión y refresh.**
  Domain: expiraciones UTC, límite absoluto, rotación, uso único y cierres.
  Pruebas deterministas con reloj controlado para estados y límites temporales.
  Reuso de refresh consumido vigente revoca; expirado devuelve 401 sin revocar,
  incluso con sucesor activo. Cubrir esperas que crucen exp al decidir el cierre.
  Trazabilidad: CA-005, CA-006, CA-007, CA-008, CA-009, CA-010, CA-014, CA-020; CP-005,
  007, 016 a 034.
- [x] **T001-04 — Implementar contratos y casos de uso de Application.**
  Tras T001-03, incorporar MediatR y FluentValidation, comportamiento asíncrono,
  validadores y handlers de login/refresh/logout con dobles de dependencias.
  Implementar aquí resolución de rol, creación local y sus contratos compartidos,
  conservando el perfil del Asesor pendiente pese al 403. Propagar cancelación y distinguir resultados.
  Trazabilidad: CA-001, CA-002, CA-003, CA-004, CA-005, CA-006, CA-007, CA-008, CA-009,
  CA-010, CA-014, CA-016, CA-017, CA-020; CP-001 a 038, 068, 069.
- [x] **T001-05 — Implementar persistencia de sesiones y consumo atómico.**
  Tras definir contratos en T001-04, crear mappings, migraciones y repositorios
  EF Core/SQL Server. Probar sesión única, huellas, transacciones, sustitución
  de login, reutilización y carreras. Incluir perfil único, consulta local de
  asignación/actividad y auditoría de aprovisionamiento necesarios para RF-001;
  RF-002 reutilizará estas piezas sin duplicar modelos o migraciones.
  Verificar por separado rollback conocido y pérdida de confirmación de commit,
  sin reintento automático. Trazabilidad: CA-006, CA-008, CA-009, CA-010, CA-011, CA-014, CA-020; CP-018, 021 a 023,
  025, 026, 042, 053, 054, 073 a 076.
- [ ] **T001-06 — Implementar LDAPS, JWT y configuración validada.**
  Usar los contratos de T001-04 y los parámetros disponibles de T001-02.
  Implementar identidad estable, consulta de estado sin contraseña conservada,
  firma/validación JWT con token_use access/refresh, skew cero y precisión de
  segundos, y carga privada con validación al arrancar. Implementar pertenencia
  directa/anidada y prioridad Admin en este tramo; RF-002 reutiliza el adaptador.
  Trazabilidad: CA-003, CA-005, CA-007, CA-008, CA-011, CA-014, CA-016, CA-017; CP-006,
  028 a 035, 041, 052, 061 a 063, 068, 070 a 072.
- [x] **T001-07 — Implementar HTTP y controles de seguridad.**
  Integrar T001-04 a 06, T001-10 y el primer acceso de RF-001: DTOs, rutas, Bearer con
  consulta de sesión, cookies, bootstrap antiforgery, CORS, límites y
  ProblemDetails. Verificar con WebApplicationFactory el contrato uniforme,
  rechazos sin efectos laterales y el nombre `__Secure-GarajeMetria.RefreshToken`
  al emitir, leer, rotar y eliminar, incluyendo 500/503 y ausencia de challenge 401 por
  fallo SQL y 429 con espera calculada. Trazabilidad: CA-001, CA-002, CA-005, CA-006, CA-009, CA-010, CA-011, CA-012, CA-013,
  CA-014, CA-015, CA-016; CP-039 a 060, 064 a 069, 073 a 080.
- [x] **T001-10 — Implementar limitación compartida y ventana exacta.**
  Con los contratos de T001-04 y el entorno SQL de T001-02, implementar
  IAuthenticationRateLimitStore y admisión atómica por partición en Infrastructure.
  Conservar cuotas por IP/usuario tras reinicios, normalización, política común
  y Retry-After. Verificar fronteras, errores sin reembolso, rechazo sin extensión,
  limpieza de permisos vencidos y dos instancias concurrentes con SQL aislado.
  Trazabilidad: CA-015; CP-043, 051, 077 a 080.
- [ ] **T001-11 — Implementar retención y limpieza diaria compartida.**
  Con la persistencia de sesión y aprovisionamiento de T001-05, implementar elegibilidad,
  borrado por lotes y supervisor con lease SQL común. Aplicar los plazos de
  DESIGN-001 / 7.2 a sesiones, huellas y aprovisionamiento; nunca
  eliminar huellas de familias activas. Verificar fronteras temporales, pérdida
  de lease, recuperación de lotes y ausencia de cascadas. Documentar y probar
  restauración aislada antes de habilitar acceso. RF-002 integrará su auditoría
  administrativa en este mismo supervisor, sin condicionar el cierre de RF-001.
  Trazabilidad: CA-018, CA-019; CP-081 a 084.

## 3. Verificación y cierre

- [ ] **T001-08 — Verificar autenticación y registrar integraciones entre tramos.**
  Tras las tareas de implementación de este tramo, verificar los 20 CA mediante
  los CP aplicables de los 85 definidos en DESIGN-001, con el nivel de prueba
  correspondiente. Incluir acceso desde navegador y cliente HTTP,
  bootstrap/rotación/logout, pérdida de respuesta, cambios de rol/taller,
  y CP-085 de catálogo: actividad en login/refresh, cierre por desactivación,
  nuevo login tras reactivación, continuidad con última copia y 503 sin carga.
  Probar las decisiones de autenticación con dobles de los contratos compartidos.
  Registrar por separado las pruebas reales entre tramos de DESIGN-001 / 10.3:
  su ejecución requiere las funcionalidades de RF-002/RF-009 correspondientes
  y no condiciona completar las tareas ni cerrar el tramo RF-001. Cubrir también
  revocación y fallos AD/SQL. Conservar evidencia de las pruebas ejecutadas.
- [ ] **T001-09 — Completar OpenAPI, documentación y checks.**
  Actualizar contratos y preparación del entorno. Ejecutar desde la raíz
  restore, build Release, format y test según AGENTS.md. Resolver errores y
  advertencias nuevas; declarar cualquier prueba que no pudo ejecutarse.
  Marcar completo solo con criterios cubiertos y arquitectura preservada.

No añadir pruebas ficticias ni sustituir SQL Server con EF InMemory/SQLite.
Una prueba dependiente de AD no ejecutada sigue pendiente aunque pasen los
dobles de Application. Este plan no autoriza commits, despliegues o migraciones
de producción.

## 4. Evidencia y verificaciones externas

Ejecución local del 20 de octubre de 2026:

- `dotnet restore`: correcto; sin avisos de dependencias vulnerables.
- `dotnet build --no-restore --configuration Release`: correcto, sin errores ni advertencias.
- `dotnet format --verify-no-changes --no-restore`: correcto.
- `dotnet test --no-build --configuration Release`: 131 pruebas superadas y
  1 omitida por ausencia de configuración del AD de pruebas. Desglose:
  Domain 21, Application 31, Architecture 14 e Integration 65 superadas + 1 omitida.
- Regresión de `Http:AllowedOrigins`: orígenes HTTPS alternativos aceptados,
  sin dominios de producción implícitos; orígenes ausentes, HTTP, comodines y
  rutas rechazados. La misma configuración rige en todos los entornos.
- SQL Server local real, con bases temporales creadas mediante migraciones y
  eliminadas por los fixtures. No se ha utilizado EF InMemory ni SQLite.
- Enlaces Markdown, plantilla JSON y trazabilidad de los 20 CA y 85 CP comprobados.
  La configuración de CI incorpora SQL aislado; no se ha ejecutado aquí un job remoto de CI.

El código implementa login, refresh, logout y bootstrap; Domain conserva las
invariantes de sesión y acceso, Application orquesta mediante MediatR y
FluentValidation, e Infrastructure aporta JWT, LDAPS asíncrono, persistencia,
cuotas compartidas y supervisor de limpieza. La migración inicial se aplica
solo en bases de tests; la API no migra automáticamente al arrancar.

| Pruebas implementadas | Escenarios relacionados | Límite de la evidencia |
| --- | --- | --- |
| Domain / AuthenticationTests | Roles y taller; CP-005, 022, 024, 031 a 034, 071, 072 | Invariantes sin red ni SQL |
| Application / AuthenticationTests | Login, acceso y perfil; CP-001 a 004, 007 a 017, 020, 022, 023, 026, 028 a 030, 048, 060, 068, 069 | Directorio y persistencia con dobles; no acredita AD real ni productores de RF-002/RF-009 |
| IntegrationTests / HttpAuthenticationTests | CP-018, 039, 040, 044 a 050, 055 a 059, 064 a 069, 079; preflight y OpenAPI | Transporte real y SQL aislado; directorio sintético |
| IntegrationTests / JwtContractTests y ConfigurationTests | CP-035, 052, 070 a 072 | Firma RSA real, claims, precisión, expiración y configuración |
| IntegrationTests / SqlStoreTests | CP-018, 021 a 025, 053, 074, 077 a 083 | SQL real: sesión única, rotación concurrente, rollback, cuotas y limpieza sin cascadas |
| IntegrationTests / InfrastructureFailureTests | CP-065, 073 a 076, 078 a 080 | Dos hosts, claves compartidas, cuotas, conexión SQL fallida y confirmación de commit perdida tras un commit real |
| IntegrationTests / DirectoryIntegrationTests | Escape LDAP y CP-041; parte de CP-061 a 063 y 068 | Escape y TLS negativo locales; prueba de dominio real condicionada a configuración externa |
| ArchitectureTests | Dependencias y acceso Api/Infrastructure | Fronteras efectivas y compilación semántica de Api |

Las filas agrupan evidencia de escenarios; no declaran ejecutadas todas las
variantes de cada CP. En particular, los cambios de nombre/recreación, estados
y pertenencia reales en AD requieren completar su matriz en el entorno aislado.
La selección de contexto de negocio y los productores de cambios de taller se
verifican al implementar sus propios tramos.

Las tareas sin marcar tienen estas condiciones de cierre:

- **T001-02 y T001-06:** SQL aislado, adaptador LDAPS, JWT y configuración están
  implementados; falta preparar AD de pruebas y verificar permisos, estados,
  identidad y pertenencia reales. Sin `GARAJEMETRIA_AD_TEST_CONFIG`, el test se
  informa como omitido.
- **T001-11:** supervisor, coordinación y eliminación elegible están implementados
  y tienen pruebas SQL. Falta ejecutar una restauración física aislada y verificar
  la reconciliación de revocaciones conforme al runbook; los tests de limpieza
  y rollback no equivalen a esta comprobación.
- **T001-08 y T001-09:** completar las verificaciones anteriores y registrar su
  evidencia antes de declarar el cierre integral. OpenAPI/Swagger UI y los
  checks automatizados están incorporados.

Preparación, opciones, comandos y recuperación se documentan en el
[runbook de autenticación](../runbooks/authentication.md). No se ha configurado
producción ni aplicado migraciones a bases ajenas a los tests.
