# TASKS-002 — Roles y acceso a talleres

| Campo | Valor |
| --- | --- |
| Requerimiento | [RF-002](../requirements/RF-002-roles-and-workshop-access.md) |
| Diseño | [DESIGN-002](../design/DESIGN-002-roles-and-workshop-access.md) |
| Coordinación | [TASKS-001](TASKS-001-authentication.md) |
| Estado | Plan de implementación; todas las tareas pendientes |

Los CA/CP citados pertenecen a RF-002. Una casilla completa requiere resultado
implementado y verificado. Los componentes compartidos con autenticación se
implementan una vez; las dependencias entre planes permiten avanzar por bloques
sin exigir que la funcionalidad completa del otro plan esté terminada.

## 1. Modelo e integración

- [ ] **T002-01 — Implementar invariantes administrativas de asignación.**
  Reutilizar roles, prioridad y perfil de RF-001. Implementar en Domain primera
  asignación, sustitución y retirada permitida, motivo y auditoría administrativa.
  Probar decisiones sin base de datos ni red.
  Trazabilidad: CA/CP-001 a 006, 011, 013, 015, 016, 019.
- [ ] **T002-02 — Verificar la reutilización de pertenencia AD en administración.**
  Reutilizar contratos y adaptador de T001-04/06, incluidos grupos directos y
  anidados, prioridad y fallos. Verificar su uso para retirar taller según el
  rol AD actual del objetivo; no crear otra resolución de roles ni fallback local. Trazabilidad: CA/CP-001 a 003, 008, 009, 011, 012, 019.
- [ ] **T002-03 — Integrar perfiles y acceso de autenticación con administración.**
  Tras T002-01, reutilizar el perfil y los contratos implementados por RF-001.
  Hacer localizable al Asesor pendiente para su primera asignación y verificar
  que las modificaciones administrativas afectan al acceso según el contrato.
  No duplicar aprovisionamiento, login, refresh ni emisión JSON/JWT.
  Trazabilidad: CA/CP-001 a 004, 008 a 014.

## 2. Mantenimiento del taller

Dependencia de integración: [RF-009](../requirements/RF-009-workshop-catalog.md).
Completar su contrato de origen, diseño y tareas antes de implementar el
adaptador/ejecutor. Los casos de uso pueden verificarse con dobles mientras
tanto. T002-03/05 deben validar taller activo en la copia compartida y
coordinarse con su publicación; 404 inexistente, 409 inactivo y 503 sin copia
inicial. Cubrir CA/CP-022: cierre de sesiones de Asesor al desactivar,
conservación de asignación y nuevo login al reactivar, sin recuperar tokens.
La caída de la fuente permite usar la última copia incluso tras 15 minutos.

- [ ] **T002-04 — Implementar consultas y persistencia local.**
  Tras definir los contratos de T002-03, crear mappings/migraciones para
  cambios de asignación, versión y auditoría administrativa sobre los perfiles
  existentes de RF-001, sin recrear su esquema ni su auditoría. Implementar
  proyecciones paginadas y lectura de asignación sin consultar todo AD.
  Verificar unicidad y creación concurrente en SQL Server aislado.
  Trazabilidad: CA/CP-014, 018.
- [ ] **T002-05 — Implementar cambio atómico de asignación.**
  Con T002-01/04 y repositorio de sesión de T001-05, implementar comando,
  validadores, autorización, versión esperada, motivo, auditoría y revocación
  en una unidad de trabajo. Consultar AD fuera de SQL al aceptar null.
  Probar cambios propios, solicitudes sin efecto, rollback y carreras con
  login/refresh. Probar commit indeterminado sin rollback o reintento supuestos.
  Trazabilidad: CA/CP-007, 015 a 017, 019, 020.
- [ ] **T002-06 — Implementar endpoints administrativos.**
  Integrar consultas/escritura de T002-04/05 y Bearer con sesión de T001-07.
  Exponer las tres rutas de RF-002 con DTOs, paginación, ETag/If-Match,
  ProblemDetails y CORS para If-Match/ETag. Rechazar Asesor y cookies sin Bearer
  antes de revelar recursos. Trazabilidad: CA/CP-005 a 008, 015 a 019.

## 3. Verificación y cierre

- [ ] **T002-07 — Verificar integración y matriz completa de permisos.**
  Tras T002-02 a 06 y T001-11, comprobar los 22 CA/CP de RF-002 y las carreras de
  DESIGN-002. Los permisos de imágenes/correcciones/consultas se integran
  con sus respectivos requerimientos sin inventar aquí sus endpoints.
  Usar WebApplicationFactory, SQL Server aislado y pruebas LDAPS sintéticas.
  Verificar retención y limpieza compartida de auditoría/aprovisionamiento
  con las opciones de DESIGN-001 / 7.2, sin eliminar perfiles ni asignaciones.
- [ ] **T002-08 — Actualizar contratos y ejecutar checks.**
  Documentar OpenAPI, opciones y preparación del entorno. Ejecutar restore,
  build Release, format y test desde la raíz según AGENTS.md. Verificar
  arquitectura y reportar evidencia, fallos o prerrequisitos ausentes antes
  de marcar completo. No declarar integración probada solo con mocks.

No implementar asignación manual de roles, administración AD, importación
masiva ni endpoints para alterar auditoría. La limpieza interna aplica únicamente
la retención aprobada de RF-001 / 5.7 y no elimina perfiles locales.
El plan no autoriza commits, despliegues o cambios en producción.
