# RF-002 — Administración de asignaciones

Las rutas `/api/users` requieren Bearer con rol `administrator` y sesión
vigente. Las cookies no autorizan estas operaciones. Los roles siguen
procediendo exclusivamente de AD mediante RF-001.

## Consultar y modificar

1. Consultar `GET /api/users?pageNumber=1&pageSize=20` para localizar perfiles,
   incluidos Asesores pendientes. Devuelve datos locales: `id`, `displayName`,
   `username`, `defaultWorkshopId`; no consulta ni importa AD.
2. Consultar `GET /api/workshops?isActive=true` para seleccionar un destino.
3. Leer `GET /api/users/{userId}/default-workshop` y conservar su cabecera `ETag`.
4. Enviar PUT a esa misma ruta con `If-Match` igual al ETag recibido y JSON:

   ```json
   {
     "workshopId": "identificador-interno-del-taller",
     "reason": "Asignación al equipo de recepción"
   }
   ```

Un cambio efectivo devuelve 204 y otro ETag, registra auditoría y cierra la
sesión del objetivo en una transacción. Si el Administrador se modifica a sí
mismo, deberá iniciar sesión de nuevo. Un PUT sin cambio efectivo conserva
ETag y sesión y no genera auditoría; exige igualmente motivo y destino válidos.

`workshopId: null` solo se admite cuando AD confirma que el objetivo es
Administrador, incluso si ya carece de taller. La consulta AD ocurre fuera de
la transacción SQL. No hay edición de roles ni de auditoría.

## Validación y errores

- Identificadores: 1–128 valores escalares Unicode, sin espacios de borde ni
  controles. Los identificadores locales existentes son GUID como string.
- Motivo: 1–500 valores escalares, algún carácter no blanco y sin controles;
  se conserva el texto recibido. No registrarlo en logs generales.
- JSON UTF-8 estricto con ambos campos exactos; rechaza duplicados, desconocidos,
  tipos incorrectos, Unicode inválido y codificaciones alternativas.
- 400: entrada inválida; 401: sesión ausente/finalizada; 403: sin permiso;
  404: usuario/taller inexistente; 409: destino inactivo o retirada incompatible;
  412: versión obsoleta; 413: cuerpo excesivo; 415: formato no admitido;
  428: falta If-Match; 503: indisponibilidad de AD, SQL o copia inicial requerida.
- Ante `operation_outcome_unknown`, no asumir rollback ni repetir el PUT:
  releer asignación/ETag y decidir con el estado confirmado.

## Configuración y persistencia

`UserAdministration` se valida al arrancar: `DefaultPageSize: 20`,
`MaxPageSize: 100` y `MaxBodyBytes: 16384`. El máximo configurable de página es
100; el cuerpo debe estar entre 1 y 1048576 bytes. Orden por identificador local
ascendente. Una página posterior al último resultado queda vacía sin desbordar.

La migración `WorkshopAssignments` sucede a `WorkshopCatalog` según el
procedimiento existente. Añade versiones individuales preservando perfiles y
asignaciones y crea `WorkshopAssignmentAudits`. No aplicar automáticamente en
producción. Down elimina auditoría administrativa: preservar esos datos antes
de una reversión.

La copia publicada de RF-009 determina existencia y actividad. Una copia antigua
sigue siendo utilizable durante caída de la fuente. Sin copia inicial, los
destinos no nulos reciben 503. Login/refresh, asignación y publicación comparten
el orden de bloqueo catálogo/usuario en Infrastructure.

La auditoría guarda actor y objetivo locales, talleres anterior/nuevo, motivo,
fecha UTC y correlación. Usa `Authentication:AuditRetentionDays` y la limpieza
compartida por lotes; no elimina perfiles, talleres ni asignaciones. CORS admite
PUT/If-Match y expone ETag a los orígenes autorizados.

## Verificación

`WorkshopAssignmentTests` y `UserAdministrationTests` cubren reglas, validación,
permisos, ETag, concurrencia, auditoría, rollback, commit indeterminado y retención. `WorkshopAssignmentMigrationTests` verifica
la actualización de perfiles existentes y `UserAssignmentConcurrencyTests`
la carrera con login.
Persistencia/HTTP usan SQL Server aislado con la preparación de
`tests/GarajeMetria.IntegrationTests/AuthenticationFixture.cs`; nunca EF InMemory
ni SQLite. El directorio de estas pruebas es sintético: no acredita pertenencia
transitiva ni permisos contra un AD real aislado.

Imágenes, vehículos y correcciones se implementarán en sus respectivos
requerimientos; RF-002 no introduce sus endpoints.
