# language: es
Característica: Aislamiento multiempresa
  Escenario: Un aliado no puede leer la orden de otro aliado
    Dado que existe la organización aliada A y la organización aliada B
    Y la orden O pertenece a A
    Cuando un administrador de B solicita la orden O por API
    Entonces la respuesta debe ser 404 o 403 según la política uniforme
    Y no debe filtrar que la orden existe
    Y debe existir un evento de seguridad sin exponer datos de O

Característica: Cotización y tarifa de volumen
  Escenario: Bloquear un tier de volumen en un viaje exclusivo
    Dado un envío con pricing_tier BUSINESS_200_499 sin ruta consolidada
    Cuando el motor intenta confirmar la cotización
    Entonces la confirmación debe ser rechazada
    Y debe solicitar una ruta consolidada o un override financiero

  Escenario: Autorizar tarifa de volumen en ruta
    Dado un envío marcado consolidated_route=true
    Y una regla vigente de 4500 centavos
    Cuando se crea la cotización
    Entonces la cotización debe registrar la regla aplicada
    Y el desglose neto, IVA y total

Característica: Creación idempotente de orden
  Escenario: Repetir la misma petición
    Dado un quote vigente y una Idempotency-Key K
    Cuando se envía dos veces la misma creación de orden con K
    Entonces debe existir una sola orden
    Y ambas respuestas deben referir al mismo id

Característica: Aceptación atómica de oferta externa
  Escenario: Dos repartidores aceptan simultáneamente
    Dado una oferta OPEN válida
    Cuando dos repartidores elegibles intentan aceptarla al mismo tiempo
    Entonces exactamente uno recibe una asignación ACCEPTED
    Y el otro recibe conflicto sin asignación
    Y existe un solo evento OFFER_ACCEPTED

Característica: Elegibilidad del repartidor
  Escenario: Documento vencido
    Dado un repartidor con licencia vencida
    Cuando despacho intenta asignarle una orden
    Entonces la asignación es rechazada
    Y la respuesta contiene el código DRIVER_DOCUMENT_EXPIRED

Característica: Evidencia de entrega
  Escenario: Entrega sin POD obligatorio
    Dado una orden en DELIVERING
    Cuando el repartidor intenta pasar a DELIVERED sin foto o código requerido
    Entonces la transición se rechaza
    Y la orden permanece en DELIVERING

  Escenario: Sincronización después de pérdida de conectividad
    Dado un repartidor sin conexión con un POD en cola local
    Cuando recupera conexión y el cliente reintenta el mismo evento
    Entonces el servidor registra una sola prueba
    Y la orden transiciona una sola vez

Característica: Tracking público privado
  Escenario: Token válido
    Dado un token de tracking vigente
    Cuando el destinatario abre el enlace
    Entonces ve public_id, estado simplificado, ventana y timeline
    Y no ve teléfono, nombre completo ni costo del repartidor

  Escenario: Token expirado
    Dado un token expirado
    Cuando se consulta
    Entonces la respuesta es 404 uniforme

Característica: Intento fallido
  Escenario: Destinatario ausente
    Dado una orden en DELIVERING
    Cuando el repartidor registra DESTINATARIO_AUSENTE
    Entonces debe adjuntar evidencia y hora de llegada
    Y la orden pasa a FAILED_ATTEMPT
    Y el sistema exige RESCHEDULED o RETURNING como siguiente acción

Característica: Cierre financiero
  Escenario: Efectivo pendiente
    Dado una orden entregada con COD no conciliado
    Cuando finanzas intenta cerrar la orden
    Entonces el sistema bloquea CLOSED
    Y muestra CASH_PENDING

Característica: No captación de clientes aliados
  Escenario: Campaña de marketing
    Dado un cliente cuyo owner_org es un aliado
    Cuando se genera una audiencia promocional de la plataforma
    Entonces ese cliente queda excluido salvo consentimiento y autorización contractual registradas

Característica: Recuperación
  Escenario: Restaurar un backup
    Dado un backup diario válido
    Cuando se ejecuta el runbook de restauración en un entorno vacío
    Entonces se recuperan organizaciones, órdenes, eventos y referencias de archivos
    Y la verificación de integridad no presenta faltantes


Feature: Actualizaciones en tiempo real con SignalR

  Scenario: Un usuario de otra organización no recibe eventos
    Given dos organizaciones sin relación comercial
    And un usuario conectado al OperationsHub de la primera organización
    When una orden de la segunda organización cambia de estado
    Then el usuario no recibe el evento

  Scenario: El cliente recupera estado después de desconectarse
    Given un cliente conectado al tracking público
    When pierde la conexión y ocurren dos cambios de estado
    And se reconecta
    Then consulta el estado actual por REST
    And presenta la versión más reciente sin duplicar eventos

  Scenario: Solo se publica después del commit
    Given una transición de orden que termina en rollback
    Then no se publica ningún evento SignalR
    And no existe evento en el outbox


Característica: Arquitectura modular
  Escenario: Un módulo intenta acceder a infraestructura privada de otro módulo
    Dado el conjunto de proyectos de la solución
    Cuando se ejecutan las pruebas de arquitectura
    Entonces la referencia debe fallar
    Y el cambio no puede integrarse

Característica: API sin estado local
  Escenario: Una petición continúa en otra réplica
    Dado dos réplicas de API detrás de un balanceador
    Y una operación autenticada iniciada contra la primera réplica
    Cuando la siguiente consulta se atiende en la segunda réplica
    Entonces el usuario conserva su autorización y tenant
    Y el estado se recupera de servicios compartidos
    Y no existe dependencia de memoria local o disco local

Característica: Worker escalado e idempotente
  Escenario: Dos Workers reclaman el mismo mensaje
    Dado un mensaje de outbox pendiente
    Cuando dos Workers intentan procesarlo simultáneamente
    Entonces exactamente uno ejecuta el efecto
    Y el otro observa el lease o resultado ya procesado
    Y no se duplica la notificación, POD o liquidación

Característica: SignalR multi-instancia
  Escenario: Publicación y conexión viven en nodos diferentes
    Dado un cliente conectado al nodo API A
    Y una transición procesada por el nodo API o Worker B
    Cuando se publica el evento por el backplane aprobado
    Entonces el cliente recibe una sola actualización autorizada
    Y un cliente de otro tenant no recibe el evento

Característica: Protección de la base transaccional
  Escenario: Reporte pesado durante operación
    Dado tráfico normal de creación y actualización de órdenes
    Cuando se genera un reporte de alto volumen
    Entonces la latencia de operaciones críticas permanece dentro del presupuesto
    Y el reporte se ejecuta en read model, réplica o proceso asíncrono aprobado

Característica: Preparación multi-ciudad
  Escenario: Configuración de una ciudad sintética
    Dado una segunda ciudad con zonas y tarifas de prueba
    Cuando se crea y cotiza una orden dentro de esa ciudad
    Entonces se aplican exclusivamente sus zonas, horarios y tarifas
    Y no se mezclan repartidores o aliados de otra ciudad


Característica: Contrato cotización a orden
  Escenario: La orden copia ubicaciones y paquetes de la cotización
    Dado una cotización vigente con origen, destino, servicio y paquetes persistidos
    Cuando se crea una orden con quote_id
    Entonces la orden referencia las mismas ubicaciones
    Y copia el snapshot de paquetes y precio
    Y no lee ubicaciones desde breakdown

Característica: RLS forzada y roles separados
  Escenario: El rol de aplicación no puede bypassear RLS
    Dado que las tablas pertenecen al rol de migraciones
    Y la API usa un rol NOBYPASSRLS que no es propietario
    Cuando consulta sin app.current_org_ids autorizado
    Entonces obtiene cero filas o error de contexto
    Y no puede desactivar RLS

  Escenario: Documentos y auditoría están aislados
    Dado documentos de repartidor y auditorías de dos organizaciones
    Cuando el rol de aplicación opera con una sola organización autorizada
    Entonces no observa filas de la otra organización

Característica: Membresías multi-organización
  Escenario: Un usuario tiene roles distintos por organización
    Dado un usuario dispatcher en la organización A y viewer en la organización B
    Cuando activa el contexto de A
    Entonces puede despachar en A
    Cuando activa el contexto de B
    Entonces no puede despachar en B

Característica: Custodia y cancelación
  Escenario: Cancelación en punto de recolección sin custodia
    Dado una orden AT_PICKUP sin POD de recolección
    Cuando un actor autorizado cancela con motivo
    Entonces la orden pasa a CANCELLED
    Y no se genera retorno ni POD de devolución

  Escenario: Intento fallido no salta directamente a entregado
    Dado una orden FAILED_ATTEMPT
    Cuando se solicita transición directa a DELIVERED
    Entonces la API responde conflicto
    Y exige reanudación por DELIVERING o reprogramación según custodia

Característica: Ubicación de repartidor
  Escenario: Persistencia y publicación autorizada
    Dado un repartidor autenticado con asignación activa
    Cuando publica una posición con client_event_id nuevo
    Entonces la posición se persiste una vez
    Y el outbox publica DriverLocationUpdated a operaciones autorizadas
    Y tracking público no recibe coordenadas exactas

  Escenario: Posición duplicada
    Dado una posición ya aceptada
    Cuando se reenvía el mismo client_event_id
    Entonces no se crea otra fila
    Y la respuesta indica duplicate=true

Característica: Idempotencia financiera y de incidencias
  Escenario: Crear oferta externa requiere idempotencia
    Cuando se crea una oferta sin Idempotency-Key
    Entonces la API rechaza la solicitud

  Escenario: Abrir incidencia requiere idempotencia
    Cuando se abre una incidencia sin Idempotency-Key
    Entonces la API rechaza la solicitud

Característica: Privilegios append-only v0.5
  Escenario: La API no puede modificar evidencia final
    Dado un Proof persistido
    Cuando el rol paqueteria_app intenta UPDATE o DELETE
    Entonces PostgreSQL responde permiso denegado
    Y el registro permanece sin cambios

  Escenario: El Worker no modifica directamente el outbox
    Dado un outbox_event pendiente
    Cuando paqueteria_worker intenta SELECT, UPDATE o DELETE directo
    Entonces la base rechaza la operación
    Cuando invoca security.claim_outbox
    Entonces recibe una fila PROCESSING con lease_token y lease_expires_at

Característica: Bootstrap bajo FORCE RLS
  Escenario: Resolver identidad sin contexto tenant previo
    Dado un identity_subject válido emitido por el proveedor OIDC
    Y app.current_user_id y app.current_org_ids están vacíos
    Cuando paqueteria_app ejecuta security.resolve_identity_context
    Entonces obtiene exclusivamente el user_id y membresías activas de ese subject
    Y no puede consultar directamente usuarios de otras identidades

  Escenario: Tracking no permite enumeración
    Dado un token inexistente, uno expirado y uno revocado
    Cuando se consultan por el endpoint público
    Entonces los tres devuelven el mismo status 404
    Y el mismo tipo de Problem Details
    Y no exponen organización, order_id ni razón de rechazo

Característica: Contexto tenant limitado a transacción
  Escenario: Reaplicar contexto después de un fallo transitorio
    Dado una unidad de trabajo con EnableRetryOnFailure
    Y el primer intento falla después de abrir la transacción
    Cuando EF Core reintenta la unidad completa
    Entonces abre una nueva transacción
    Y vuelve a aplicar current_user_id y current_org_ids antes de consultar
    Y no reutiliza el contexto de una solicitud anterior

  Escenario: Consulta tenant fuera de transacción
    Dado un DbContext sin transacción explícita
    Cuando ejecuta una consulta tenant AsNoTracking
    Entonces en desarrollo o pruebas se genera un error arquitectónico
    Y en PostgreSQL la consulta falla cerrada o devuelve cero filas

  Escenario: PgBouncer no filtra contexto entre tenants
    Dado PgBouncer configurado en transaction pooling
    Y dos solicitudes consecutivas de organizaciones diferentes reutilizan una conexión física
    Cuando ambas ejecutan su unidad de trabajo
    Entonces cada una observa únicamente sus datos
    Y current_org_ids vuelve al valor vacío al terminar cada transacción

Característica: Cotización de uso único
  Escenario: Dos claves distintas intentan consumir la misma cotización
    Dado una cotización ACTIVE y vigente
    Cuando dos CreateOrder con Idempotency-Key diferentes se ejecutan concurrentemente
    Entonces exactamente una orden se crea
    Y la cotización queda USED
    Y la otra petición recibe conflicto sin crear una segunda orden

Característica: Snapshot sin PII
  Escenario: Persistir una cotización con datos de contacto
    Dado una solicitud con dirección, nombre, teléfono y referencias
    Cuando se crea la cotización
    Entonces request_snapshot_redacted contiene solo ids, zonas, servicio y atributos no personales
    Y no contiene dirección, nombre, teléfono ni referencias de acceso en texto claro
    Y cualquier snapshot personal adicional está cifrado y versionado

Característica: Esquemas físicos por módulo
  Escenario: La migración crea todos los schemas normativos
    Cuando se aplica AI-06 y AI-18 en una base vacía
    Entonces existen identity, organizations, clients, locations, pricing, orders, dispatch, drivers, routes, custody, incidents, finance, allies, notifications, reporting, platform y security
    Y default privileges cubre cada schema runtime

  Escenario: FK cross-schema no autorizada
    Dado una migración que agrega una FK entre módulos no incluida en la matriz permitida
    Cuando se ejecutan ArchitectureTests de catálogo
    Entonces la prueba falla

Característica: Evidencia mediante carga firmada
  Escenario: Finalizar una evidencia validada
    Dado una proof_upload_session READY con objeto validado en cuarentena
    Cuando el cliente envía upload_session_id, tipo, hash y captured_at
    Entonces el objeto se promueve a almacenamiento definitivo
    Y se crea un único Proof append-only
    Y la sesión queda CONSUMED

  Escenario: API no recibe multipart
    Cuando se intenta enviar un archivo multipart a /orders/{id}/proofs
    Entonces la API responde tipo de contenido no soportado
    Y no escribe bytes en disco local

Característica: GPS por lotes y lane independiente
  Escenario: Lote con posiciones nuevas y repetidas
    Dado un lote de tres client_event_id donde uno ya existe
    Cuando el repartidor publica el lote
    Entonces la respuesta conserva tres resultados en el mismo orden
    Y dos son ACCEPTED y uno DUPLICATE
    Y no se crea una segunda fila para el duplicado

  Escenario: Telemetría no retrasa eventos críticos
    Dado una carga alta de driver_positions
    Y eventos de pago y custodia pendientes
    Cuando procesan los Workers
    Entonces payment/custody usan platform.outbox_events y su presupuesto de lag
    Y GPS usa platform.location_outbox_events y un consumer independiente
    Y oldest business outbox age permanece dentro del umbral

Característica: Política 401 403 404
  Escenario: Solicitud sin autenticación
    Cuando un cliente consulta una ruta privada sin token válido
    Entonces recibe 401

  Escenario: Actor sin capacidad dentro de su organización
    Dado un business_operator autenticado que puede ver una orden
    Cuando intenta aplicar un override financiero
    Entonces recibe 403

  Escenario: Recurso de otro tenant
    Dado un actor autenticado de la organización A
    Y una orden de la organización B
    Cuando consulta la orden
    Entonces recibe 404 uniforme
    Y la respuesta no confirma existencia

Característica: Custodia después de reprogramación
  Escenario: Reanudar entrega sin custodia
    Dado una orden FAILED_ATTEMPT que pasó a RESCHEDULED
    Y custody_acquired=false
    Cuando se intenta RESCHEDULED a DELIVERING
    Entonces la transición se rechaza

Característica: COD mínimo en MVP-0
  Escenario: Entregar COD sin registro de efectivo
    Dado una orden con cod_expected_cents mayor a cero
    Y no existe cod_transaction RECORDED o RECONCILED
    Cuando se intenta DELIVERED
    Entonces la transición se rechaza con CASH_NOT_RECORDED

  Escenario: Cerrar COD no conciliado
    Dado una orden DELIVERED con cod_transaction RECORDED
    Cuando se intenta CLOSED
    Entonces la transición se rechaza con CASH_PENDING

Característica: Ventana de reclamación
  Escenario: Abrir reclamación después de la ventana
    Dado una orden CLOSED cuyo claim_window_ends_at ya venció
    Cuando se intenta CLAIM_OPEN
    Entonces la transición se rechaza

  Escenario: Finalización idempotente
    Dado una orden CLOSED con ventana vencida y finalized_at vacío
    Cuando el job de finalización se ejecuta dos veces
    Entonces finalized_at se fija una sola vez
    Y no se modifican OrderEvents ni Proofs

Característica: Lista blanca transaccional
  Escenario: Aparece un sexto coordinador cross-module
    Dado un nuevo flujo que comparte transacción entre módulos
    Y no está en transaction_coordinator_allowlist
    Cuando se ejecutan ArchitectureTests
    Entonces la prueba falla y exige ADR

Característica: Extensiones y hash simétrico v0.6
  Escenario: Bootstrap y tracking ejecutan en PostgreSQL real
    Dado una base PostgreSQL/PostGIS vacía
    Cuando se aplican AI-06 y AI-18
    Entonces pgcrypto existe en extensions
    Y PostGIS permanece en public
    Y paqueteria_app y paqueteria_worker no tienen CREATE sobre public
    Y security.resolve_identity_context se ejecuta con FORCE RLS
    Y security.get_public_tracking_projection se ejecuta sin error de digest

  Escenario: C# y SQL producen el mismo hash
    Dado un vector fijo de token Base64URL sin padding
    Cuando C# calcula SHA-256 sobre sus bytes UTF-8 exactos
    Y PostgreSQL calcula extensions.digest(convert_to(token,'UTF8'),'sha256')
    Entonces ambos resultados bytea tienen 32 bytes
    Y son idénticos
    Y agregar un espacio al token produce un hash diferente

Característica: Lifecycle del outbox con lease v0.6
  Escenario: Settle válido
    Dado un mensaje reclamado con lease_token vigente
    Cuando el Worker invoca settle_outbox con el mismo token y PROCESSED
    Entonces el mensaje queda PROCESSED
    Y se limpian lease_token, lease_expires_at, locked_at y locked_by

  Escenario: Settle obsoleto
    Dado un mensaje reencolado y reclamado con un lease_token nuevo
    Cuando el Worker anterior intenta settle con el token viejo
    Entonces la función devuelve false
    Y no modifica el mensaje

  Escenario: Recuperar Worker muerto
    Dado un mensaje PROCESSING cuyo lease expiró
    Cuando ScheduledTasks invoca requeue_stale_outbox
    Entonces el mensaje queda RETRY con lease vacío
    Y puede ser reclamado nuevamente
    Y si superó max_attempts queda DEAD

  Escenario: Productor sin RETURNING
    Dado un productor autorizado de outbox
    Cuando EF Core inserta el mensaje
    Entonces proporciona id, status, attempts, available_at y created_at
    Y el SQL emitido no contiene RETURNING
    Y la operación funciona sin privilegio SELECT

Característica: Tracking público fail-closed v0.6
  Esquema del escenario: Mapa completo SQL y C#
    Dado el estado interno <interno>
    Cuando la política C# y security.map_public_order_status lo transforman
    Entonces ambos devuelven <publico>
    Ejemplos:
      | interno          | publico             |
      | DRAFT            | CREATED             |
      | CONFIRMED        | CREATED             |
      | READY_FOR_PICKUP | SCHEDULED           |
      | ASSIGNED         | SCHEDULED           |
      | AT_PICKUP        | SCHEDULED           |
      | PICKED_UP        | IN_TRANSIT          |
      | IN_TRANSIT       | IN_TRANSIT          |
      | DELIVERING       | OUT_FOR_DELIVERY    |
      | FAILED_ATTEMPT   | DELIVERY_EXCEPTION  |
      | RESCHEDULED      | SCHEDULED           |
      | RETURNING        | RETURNING            |
      | RETURNED         | RETURNED             |
      | DELIVERED        | DELIVERED            |
      | CLOSED           | DELIVERED            |
      | CLAIM_OPEN       | DELIVERED            |
      | CLAIM_RESOLVED   | DELIVERED            |
      | CANCELLED        | CANCELLED            |

  Escenario: Evento interno permanece privado
    Dado un order_event con public_event_code nulo
    Cuando un token válido consulta tracking
    Entonces el evento no aparece en timeline
    Y el payload interno nunca se devuelve

  Escenario: Estado desconocido
    Dado una fila sintética con estado no mapeado en un fixture de contrato
    Cuando el Worker intenta publicar
    Entonces C# lanza PublicStatusMappingException y genera alerta
    Cuando se consulta la proyección SQL anónima
    Entonces no devuelve proyección y la API responde 404 uniforme

Característica: Evidencia legal de aceptación v0.6
  Escenario: Vector canónico estable
    Dado un OrderAcceptanceEvidence conocido
    Cuando OrderAcceptanceCanonicalForm v1 lo serializa
    Entonces las claves están en el orden normativo
    Y UUIDs están en formato D minúscula
    Y accepted_at_client usa UTC con siete fracciones y Z
    Y el UTF-8 no contiene BOM
    Y el SHA-256 coincide con el vector permanente

  Escenario: Aceptación atómica y append-only
    Dado una cotización ACTIVE
    Cuando se crea la orden
    Entonces quote, order, order_acceptance, primer order_event y outbox se confirman en una transacción
    Y order_acceptances aplica RLS por owner_org_id
    Y UPDATE o DELETE runtime es rechazado

Característica: Provisioning transaccional v0.6
  Escenario: Primer usuario
    Dado un principal OIDC válido sin usuario local
    Cuando la aplicación genera un user_id y lo fija como current_user_id en la transacción
    Y current_org_ids es el arreglo vacío {}
    Entonces inserta identity.users sin usar el rol bootstrap para escribir
    Y identity_subject coincide con el principal validado

  Escenario: Nueva organización
    Dado un usuario autorizado para crear organización
    Cuando la aplicación genera organization_id y lo agrega solo al uuid[] de la transacción
    Entonces organización, membresía inicial y auditoría se insertan atómicamente
    Y un rollback no deja registros parciales

Característica: Lane de telemetría y retención v0.6
  Escenario: Una posición produce como máximo un mensaje
    Dado un driver_position_id ya insertado en location_outbox_events
    Cuando un retry intenta insertarlo nuevamente
    Entonces UNIQUE(driver_position_id) rechaza el duplicado
    Y no existe FK hacia driver_positions

  Escenario: Purga segura
    Dado mensajes PENDING, RETRY, PROCESSING, PROCESSED y DEAD
    Cuando el Worker invoca purge_outbox en dry-run
    Entonces solo cuenta PROCESSED y DEAD anteriores a los cutoffs
    Cuando ejecuta la purga real
    Entonces jamás elimina PENDING, RETRY ni PROCESSING
    Y paqueteria_maintenance no puede claim ni settle

Característica: Dinero de 64 bits v0.6
  Escenario: Contratos monetarios
    Cuando se inspeccionan SQL y OpenAPI
    Entonces todas las columnas de centavos son bigint
    Y todos los campos API de centavos usan format int64
    Y ninguna regla monetaria usa float o double

