# PRD — Plataforma de Gestión Comercial Incoders

**Versión:** 2.0  
**Estado:** Definición funcional inicial  
**Fecha:** 2026-09-11  
**Nombre definitivo:** Pendiente  
**Primer vertical:** Carnicerías y distribución cárnica

---

## 1. Propósito

Este documento define la visión y el alcance funcional de una plataforma integral de gestión comercial y operativa desarrollada por Incoders.

La primera implementación resolverá la operación de una carnicería que también realiza distribución mayorista. Opera dos sucursales, inicialmente con una notebook POS por sucursal que aloja físicamente el nodo local. La arquitectura de producto conserva una evolución posterior a varias terminales por sucursal. Combina venta al público, pedidos, reparto, compras, inventario, cuentas corrientes y tareas administrativas.

El núcleo deberá ser reutilizable por otros comercios minoristas, mayoristas y distribuidores. Este PRD no define todavía el stack tecnológico, la base de datos ni las herramientas de infraestructura. Esas decisiones se documentarán posteriormente.

## 2. Definición del producto

La solución será una **plataforma de gestión comercial y operativa offline-first para comercios minoristas y distribuidores**, con:

- Punto de venta.
- Pedidos presenciales, internos y online.
- Clientes, contactos, proveedores y empleados.
- Compras, stock y listas de precios.
- Cuentas corrientes.
- Caja, caja chica, gastos y cuentas financieras.
- Repartos y remitos.
- Tareas, alertas y notificaciones.
- Campañas comerciales.
- Reportes e inteligencia artificial analítica.
- Funcionalidades verticales para carnicerías, incluido el romaneo.

El POS es un módulo central, pero no representa por sí solo al producto. El sistema tampoco se define como CRM: contiene un CRM reducido integrado en una plataforma de operaciones comerciales.

> Gestionar ventas, pedidos, stock, caja, cuentas corrientes, compras y repartos desde un solo sistema, manteniendo la operación local aun sin Internet.

## 3. Contexto inicial

- Dos sucursales.
- Perfil inicial por sucursal: una notebook POS que aloja físicamente el nodo local.
- Arquitectura de producto escalable: varias terminales podrán compartir el nodo local de una sucursal.
- Versiones modernas de Windows.
- Venta minorista en punto de venta.
- Venta mayorista y distribución.
- Pedidos recibidos por WhatsApp, teléfono, mostrador y portal online.
- Diferentes listas de precios por tipo de cliente y canal.
- Manejo de carne vacuna, porcina y pollo.
- Recepción de medias reses y mercadería fraccionada.
- Operación local independiente de Internet.
- Consulta y administración remota desde cloud.
- Romaneo aproximadamente tres o cuatro veces por año.

## 4. Objetivos

### 4.1 De negocio

- Centralizar la operación comercial y administrativa.
- Evitar registros duplicados y tareas manuales.
- Saber cuánto se debe, a quién, cuánto deben los clientes y por qué concepto.
- Mantener inventario trazable por movimientos.
- Integrar pedidos de todos los canales.
- Mejorar preparación, reparto y cobranza.
- Consolidar información de ambas sucursales.
- Permitir crecimiento hacia nuevos comercios.
- Crear una base operativa apta para automatización, reportes e IA.

### 4.2 De usuario

- Vender rápidamente con pocos pasos.
- Continuar vendiendo sin Internet.
- Consultar saldos y movimientos comprensibles.
- Importar información en lugar de cargarla manualmente.
- Acceder desde dispositivos móviles a funciones autorizadas.
- Recibir recordatorios y gestionar tareas.
- Obtener reportes sin mantener planillas paralelas.

### 4.3 De diseño

- Separar reglas de negocio de interfaces, hardware y servicios externos.
- No duplicar reglas entre local y cloud.
- Sincronizar de manera confiable, reintentable y auditable.
- Soportar organizaciones y sucursales desde el modelo funcional.
- Evolucionar mediante módulos.

## 5. Alcance

Todo lo definido en este PRD pertenece al alcance general del producto. El orden de implementación podrá dividirse en etapas, pero ninguna etapa posterior debe interpretarse como opcional o fuera de alcance.

### 5.1 Incluido

- Aplicaciones locales para Windows moderno.
- Varias terminales en una LAN.
- Múltiples sucursales.
- Sincronización cloud.
- Administración web responsive.
- POS y hardware.
- Pedidos omnicanal y pedidos online.
- Pasarela de pago.
- Compras, stock y romaneo.
- Clientes, proveedores, contactos y empleados.
- Cuentas corrientes y tesorería.
- Caja chica y gastos.
- Repartos y remitos.
- Listas de precios e importación/exportación.
- Tareas, notificaciones y campañas.
- Facturación.
- Reportes y asistente de IA.

### 5.2 Producto independiente futuro

La aplicación que utilizará imágenes y videos de una media res para estimar o generar automáticamente el romaneo mediante machine learning **no forma parte de esta plataforma**.

Será un producto independiente, vendible por separado e integrable mediante contratos y APIs. Su descubrimiento funcional comenzará con la observación del proceso real de romaneo.

La plataforma sí deberá poder:

- Recibir sus resultados.
- Asociarlos con organización, sucursal, proveedor, lote y recepción.
- Permitir revisión humana.
- Generar movimientos de stock únicamente luego de la confirmación.
- Conservar la relación con evidencias y cálculos.

## 6. Principios

- **Offline-first:** Internet no será necesario para vender localmente.
- **Operación integrada:** pedido, venta, pago, inventario y cuenta estarán relacionados.
- **Trazabilidad:** saldos y stock surgirán de movimientos explicables.
- **Velocidad en caja:** las acciones frecuentes requerirán pocas interacciones.
- **Seguridad por diseño:** acceso por organización, sucursal, rol y permiso.
- **Auditoría:** se registrará quién, cuándo, dónde y por qué.
- **Configuración antes que bifurcación:** los verticales se resolverán con módulos y reglas.
- **Hardware desacoplado:** el dominio no dependerá de un fabricante.
- **IA verificable:** respuestas con período, filtros, métricas y fuente.
- **Confirmación humana:** OCR/IA no impactará stock definitivamente sin revisión.

## 7. Actores

### 7.1 Administrador del sistema

- Administrar organizaciones y capacidades.
- Gestionar configuración técnica, diagnósticos e integraciones.
- Acceder a auditoría técnica autorizada.

### 7.2 Propietario o administrador

- Administrar sucursales, usuarios, permisos y configuración.
- Acceder a información comercial y financiera.
- Gestionar maestros, clientes, proveedores y empleados.
- Consultar reportes consolidados.

### 7.3 Administrador de sucursal

- Gestionar las sucursales autorizadas.
- Consultar cajas, pedidos, stock y repartos.

### 7.4 Cajero

- Abrir y cerrar caja.
- Crear ventas, recibir pagos e imprimir comprobantes.
- Aplicar solamente descuentos o cambios autorizados.

### 7.5 Responsable financiero

- Gestionar cuentas, cobros, pagos, gastos y cuentas corrientes.
- Consultar cierres e informes financieros.

### 7.6 Responsable de inventario y compras

- Gestionar productos, proveedores, compras y recepciones.
- Ajustar stock y ejecutar procesos autorizados de transformación o romaneo.

### 7.7 Responsable de pedidos y reparto

- Confirmar y preparar pedidos.
- Asignar repartos, emitir remitos y gestionar rendiciones.

### 7.8 Repartidor

- Consultar entregas asignadas.
- Actualizar estados, incidentes, cobros y evidencias.

### 7.9 Empleado

- Acceder a funciones y tareas asignadas.
- Consultar información personal autorizada.

### 7.10 Cliente online

- Acceder mediante enlace o código seguro.
- Crear, pagar y consultar pedidos sin login tradicional.

### 7.11 Analista

- Consultar reportes autorizados sin modificar la operación.

Los roles serán configurables y estarán compuestos por permisos granulares.

## 8. Organizaciones y sucursales

- El producto admitirá múltiples organizaciones.
- Cada organización podrá tener varias sucursales.
- Cada sucursal tendrá terminales, cajas y ubicaciones de stock.
- Los usuarios podrán limitarse a una o varias sucursales.
- Los reportes admitirán vista individual y consolidada.
- Cada terminal tendrá identidad, configuración y estado.
- Todo registro deberá estar correctamente aislado por organización.

La estrategia física de multitenencia o bases dedicadas se definirá posteriormente. El modelo funcional incluirá organización y sucursal donde corresponda.

## 9. Requisitos funcionales

### 9.1 Identidad y permisos

- Usuarios activos, suspendidos e inhabilitados.
- Roles iniciales y configurables.
- Permisos granulares.
- Alcance por organización y sucursal.
- Restricciones específicas para finanzas, costos, precios y auditoría.
- Autorización superior para descuentos, anulaciones y ajustes configurables.
- Registro de sesiones y acciones sensibles.
- La administración local Windows y la administración web requerirán inicio de sesión de usuarios administrativos.
- Ambas administraciones ofrecerán, según los permisos autorizados, paridad funcional para la gestión de negocio: clientes, proveedores, cuentas corrientes, catálogo, configuración, reportes y demás operaciones administrativas aplicables.
- Continuidad local autorizada ante indisponibilidad de Internet.

### 9.2 Catálogo

- Productos y sus presentaciones comerciales.
- Categorías comerciales jerárquicas, marcas y atributos.
- Comportamientos de venta e inventario por presentación: por unidad, por peso o por peso variable.
- Unidades de compra, almacenamiento y venta, con relaciones comerciales explícitas cuando difieran.
- Códigos internos y múltiples códigos de barras.
- Productos activos, inactivos o temporalmente no disponibles.
- Impuestos, costos, precios, márgenes y reglas de redondeo.
- Ficha de producto con pestaña **Imágenes**.
- Una imagen miniatura para identificar el producto en vistas compactas y una imagen de producto para su presentación en catálogo y carrito de compras online.
- La definición inicial cubre esas dos imágenes con roles diferenciados; no define una galería ni gestión multimedia avanzada.
- Archivos asociados al producto cuando corresponda.
- Búsqueda rápida por descripción o código.
- Clasificaciones estructuradas opcionales habilitables por vertical, sin convertirlas en tipos rígidos de producto.

La definición funcional reutilizable del catálogo está documentada en [Dominio de productos](./docs/domain/product-domain.md). El núcleo no se acoplará a carnicerías: las clasificaciones de especie, corte y estado de procesamiento serán información estructurada opcional del vertical cárnico, no tipos de producto para vaca, cerdo, pollo u otros rubros.

### 9.3 Listas de precios

- Lista minorista.
- Lista mayorista o de distribución.
- Listas adicionales por segmento, canal o cliente.
- Asignación predeterminada por cliente.
- Vigencia, versiones e historial.
- Incremento masivo porcentual, por monto o por selección.
- Vista previa, redondeo, aprobación y publicación.
- Importación y exportación mediante Excel.
- Validación de errores y duplicados.
- Borradores y reversión controlada.
- Auditoría de cada cambio.

Los precios usados en operaciones históricas no cambiarán al actualizar una lista.

### 9.4 Clientes

- Personas y empresas.
- Datos identificatorios, fiscales y comerciales.
- Direcciones, ciudades y canales de contacto.
- Segmento minorista, mayorista o distribución.
- Lista de precios y condición de venta.
- Límite de crédito y cuenta corriente.
- Preferencias de entrega y facturación.
- Estado activo, suspendido o bloqueado.
- Historial de pedidos, ventas, pagos y comunicaciones.
- Resúmenes por rango de fechas.
- Código o enlace seguro para pedidos online.

### 9.5 Contactos y CRM reducido

- Contactos que todavía no son clientes.
- Datos, dirección, origen y estado del lead.
- Notas e historial de interacciones.
- Próxima acción y responsable.
- Tareas, visitas y entrega de muestras.
- Conversión a cliente sin perder historial.
- Segmentación para campañas.

### 9.6 Proveedores y compras

- Datos comerciales, fiscales y de contacto.
- Órdenes de compra.
- Recepciones parciales o completas.
- Facturas, remitos y certificados.
- Listas de precios de proveedores.
- Historial de costos.
- Condiciones y cuenta corriente.
- Pagos parciales, notas y ajustes.
- Gastos asociados.
- Trazabilidad entre compra, recepción, stock, documento y pago.

### 9.7 Recepción inteligente de documentos

- Captura local de documentos e imágenes desde cámaras, escáneres o archivos para la recepción de comprobantes de proveedores, facturas, certificados y remitos.
- Extracción y sugerencias mediante OCR/IA.
- Reconocimiento de productos, cantidades, pesos, lotes, fechas y referencias.
- Configuraciones por proveedor y formato.
- Recepción generada en borrador.
- Indicadores de confianza y campos no reconocidos.
- Corrección manual.
- Conservación del documento original.
- Revisión y confirmación humana autorizada de toda sugerencia OCR/IA antes de crear movimientos definitivos de inventario.
- Prevención de duplicados y auditoría.

### 9.8 Inventario

- Stock por organización, sucursal y ubicación.
- Libro de movimientos.
- Ingreso por compra o recepción.
- Egreso por venta.
- Reserva y liberación por pedido.
- Transferencia entre sucursales o ubicaciones.
- Devoluciones, mermas, consumo y ajustes.
- Conteo físico e inventario inicial.
- Inventario coherente con el comportamiento de cada presentación: por unidad, por peso o por peso variable.
- Lotes y trazabilidad cuando corresponda.
- Stock físico, reservado y disponible.
- Política configurable de stock negativo.
- Alertas por mínimos, diferencias o vencimientos.
- Historial completo por producto.

### 9.9 Punto de venta

- Login, selección de terminal y apertura de caja.
- Ventas nuevas, suspendidas y recuperadas.
- Búsqueda y escaneo de productos.
- Lectura de códigos generados por balanza.
- Cantidades, pesos y observaciones.
- Selección o alta rápida de cliente según permiso.
- Aplicación automática de lista.
- Descuentos autorizados.
- Pagos simples o combinados.
- Efectivo, transferencia, Mercado Pago, cuentas bancarias, billeteras y otros.
- Venta en cuenta corriente.
- Cálculo de vuelto.
- Ticket, reimpresión y facturación.
- Cancelaciones y devoluciones.
- Cierre de caja.
- Funcionamiento sin Internet.

### 9.10 Pedidos omnicanal

Todos los pedidos ingresarán en un circuito común y conservarán su origen:

- Mostrador.
- POS.
- WhatsApp.
- Teléfono.
- Carga administrativa.
- Portal online.
- Integraciones futuras.

Cada pedido contendrá:

- Organización y sucursal.
- Cliente y canal.
- Lista de precios.
- Productos, pesos y observaciones.
- Retiro o entrega.
- Fecha, franja, dirección y ciudad.
- Prioridad y responsable.
- Estados operativo, de pago, facturación y entrega separados.
- Historial, comprobantes y documentos.

Estados operativos iniciales:

- Borrador.
- Recibido.
- Pendiente de confirmación.
- Confirmado.
- En preparación.
- Preparado.
- Listo para retiro o despacho.
- En reparto.
- Entregado o parcialmente entregado.
- Cancelado.

Los estados serán configurables o extensibles sobre un conjunto canónico.

### 9.11 Pedidos online

- Acceso sin login mediante token no predecible y revocable.
- Asociación segura con cliente y organización.
- Catálogo y lista correspondiente.
- Disponibilidad según sucursal, día o stock.
- Carrito, pesos estimados y observaciones.
- Retiro o reparto.
- Dirección y franja.
- Pago integrado.
- Actualización automática de pago.
- Prioridad configurable para pedidos pagados.
- Consulta segura del estado.
- Ingreso automático al circuito local.
- Idempotencia ante reintentos.
- Política para diferencias entre peso estimado y real.

### 9.12 Estados financieros

Se mantendrán separados:

- Estado operativo.
- Estado de pago.
- Estado de facturación.
- Estado de entrega.

Estados de pago:

- Pendiente.
- Parcialmente pagado.
- Pagado.
- Vencido.
- Rechazado.
- Reembolsado parcial o total.
- Cancelado.

### 9.13 Cuentas corrientes

- Cuentas de clientes, proveedores y empleados.
- Movimientos con fecha, importe, concepto y origen.
- Ventas a crédito, compras, pagos, cobros, anticipos, notas y ajustes.
- Pagos parciales y vencimientos.
- Saldo calculado desde los movimientos.
- Resumen por rango de fechas con saldo inicial y final.
- Identificación de quién debe, a quién y por qué.
- Adjuntos y referencias.
- Reversos auditables en lugar de eliminación silenciosa.
- Deuda total, vencida y antigüedad de saldos.

### 9.14 Cuentas financieras

Tipos iniciales:

- Caja principal.
- Caja por terminal.
- Caja chica.
- Cuenta bancaria.
- Mercado Pago.
- Billetera virtual.
- Transferencias pendientes.
- Otras cuentas configurables.

Cada movimiento registrará cuenta, importe, moneda, concepto, categoría, medio, fechas, usuario, sucursal, documento relacionado y evidencia. Se admitirán transferencias entre cuentas sin contabilizarlas erróneamente como ingresos o gastos.

### 9.15 Caja y caja chica

- Apertura y cierre por caja y turno.
- Fondo inicial.
- Ingresos, egresos, aportes y retiros.
- Ventas por medio de pago.
- Cobros de deuda diferenciados de ventas.
- Gastos pagados desde caja.
- Importe esperado, declarado y diferencia.
- Justificación de diferencias.
- Caja chica con responsable, fondo, límites y rendiciones.
- Adjuntos y auditoría.

### 9.16 Gastos

- Fecha, beneficiario, categoría y concepto.
- Organización y sucursal.
- Cuenta de pago.
- Estados pendiente, aprobado, pagado, rechazado o anulado.
- Comprobante.
- Gastos recurrentes.
- Asociación con compras, vehículos o reparto.
- Aprobaciones por monto y permiso.
- Reportes por categoría, período, sucursal y cuenta.

### 9.17 Reporte diario

- Ventas brutas y netas.
- Descuentos, cancelaciones y devoluciones.
- Ventas por sucursal, terminal, cajero y canal.
- Ventas por medio de pago.
- Pagos combinados.
- Ventas a cuenta corriente.
- Cobros de deudas anteriores.
- Gastos, retiros y diferencias.
- Totales por cuenta financiera.
- Pedidos por estado.
- Documentos fiscales emitidos.

Se distinguirán fecha de venta, pago, entrega y fecha contable.

### 9.18 Repartos

- Ciudades, zonas, direcciones, días y franjas.
- Agrupación de pedidos en repartos.
- Responsable, vehículo y orden de entrega.
- Impresión de remitos.
- Despacho y seguimiento de estados.
- Entrega completa o parcial.
- Incidentes, rechazos y devoluciones.
- Cobro contra entrega.
- Rendición del repartidor.
- Evidencia de entrega.
- Optimización automática de rutas como evolución.

### 9.19 Empleados

- Legajo, contacto, estado y sucursales.
- Usuario y rol asociado.
- Adelantos, compras, descuentos y otros movimientos.
- Saldo e historial con el comercio.
- Documentación de acceso restringido.

No se incluye inicialmente una liquidación integral de sueldos, aportes e impuestos. Si se incorpora, requerirá un análisis legal y contable específico.

### 9.20 Tareas, alertas y notificaciones

Se distinguirán:

- **Tarea:** trabajo que debe completarse.
- **Recordatorio:** programación temporal.
- **Notificación:** mensaje enviado por un canal.

Las tareas incluirán:

- Organización, sucursal, título y descripción.
- Fecha, hora, prioridad y estado.
- Responsable o equipo opcional.
- Tareas generales que pueda tomar cualquier usuario autorizado.
- Recurrencia.
- Relación con cliente, contacto, pedido, proveedor o proceso.
- Dirección cuando corresponda.
- Comentarios y evidencia.
- Historial y escalamiento.

Estados:

- Pendiente.
- Disponible.
- Tomada.
- En progreso.
- Completada.
- Cancelada.
- Vencida.

Casos: descongelar mercadería, llevar muestras, avisar sobre desposte, actualizar precios, cobrar deuda y preparar repartos.

Canales a evaluar: sistema, web/móvil, correo y WhatsApp.

### 9.21 Campañas

- Campañas por beneficio, día o evento.
- Segmentación por cliente, ciudad, lista, historial o inactividad.
- Selección manual.
- Plantillas y programación.
- Enlace al local, catálogo o promoción.
- Consentimientos y exclusiones.
- Métricas de entrega, pedidos y ventas atribuibles.
- Controles para evitar envíos erróneos o excesivos.

### 9.22 Facturación

- Tickets y comprobantes internos.
- Facturas y notas asociadas.
- Factura A cuando el cliente la solicite y corresponda.
- Datos fiscales, numeración y punto de venta.
- Relación entre pedido, venta, pago y comprobante.
- Impresión, descarga y reenvío.
- Anulación mediante flujos autorizados.
- Integración fiscal argentina sujeta a especificación normativa y técnica.

### 9.23 Hardware

- Lector de códigos.
- Balanza.
- Impresora térmica.
- Impresora de remitos.
- Cajón de dinero.
- Configuración por terminal.
- Diagnóstico y prueba de conexión.
- Adaptadores por fabricante o protocolo.
- Manejo de errores y operación degradada.

Los modelos y protocolos exactos se relevarán en ambas sucursales.

### 9.24 Romaneo operativo

El romaneo forma parte de la plataforma, pero se implementará luego de los módulos de uso cotidiano por su baja frecuencia actual.

- Recepción de media res vinculada con proveedor, documento, lote, sucursal, fecha y peso.
- Registro de desposte.
- Cortes y subproductos.
- Peso de resultados.
- Hueso, grasa, merma y otros destinos.
- Conciliación entre entrada y salidas.
- Rendimiento total y por corte.
- Costeo y distribución del costo.
- Movimientos de inventario.
- Correcciones auditadas.
- Adjuntos y evidencias.
- Información estructurada opcional de especie, corte y estado de procesamiento cuando el vertical cárnico esté habilitado, sin tipos rígidos por especie.
- Recepción directa de productos que no requieren romaneo.
- Importación futura desde el producto de romaneo automático.

El detalle se completará después de documentar una actividad real.

### 9.25 Reportes

- Ventas por período, sucursal, canal, cliente, producto, categoría y lista.
- Venta minorista, mayorista, reparto y online.
- Unidades y kilos.
- Productos y cortes más vendidos.
- Mejores clientes.
- Ticket promedio y márgenes.
- Stock, mermas y diferencias.
- Compras y evolución de costos.
- Cuentas por cobrar y pagar.
- Gastos.
- Rendimiento de reparto y tiempos de pedidos.
- Campañas.
- Romaneo.
- Vista por sucursal y consolidada.
- Exportación.

### 9.26 Asistente de IA

Permitirá preguntas como:

- ¿Cuál fue el corte más vendido en febrero de 2026?
- ¿Cómo se comparan dos meses?
- ¿Qué cliente compró más?
- ¿Cuánto se vendió en local, reparto y online?
- ¿Qué clientes tienen deuda vencida?

Deberá:

- Respetar organización, sucursal, rol y permiso.
- Usar métricas definidas y consultas controladas.
- Informar período, filtros, unidad y definición.
- Permitir abrir el reporte subyacente.
- Distinguir hechos, cálculos e inferencias.
- Proteger información sensible.
- Generar tablas y gráficos cuando corresponda.
- No ejecutar SQL arbitrario ni acciones destructivas.

## 10. Modelo conceptual

Entidades principales:

- Organización, sucursal y terminal.
- Usuario, rol y permiso.
- Contacto, cliente, proveedor y empleado.
- Producto, presentación, categoría comercial jerárquica, unidad y código.
- Comportamiento de venta e inventario y clasificaciones verticales opcionales.
- Lista y versión de precios.
- Pedido, venta, ítems y pagos.
- Cuenta corriente y movimiento.
- Cuenta financiera y movimiento.
- Caja, turno y cierre.
- Gasto.
- Compra y recepción.
- Inventario, movimiento, lote y ubicación.
- Reparto, entrega y remito.
- Tarea, recordatorio y notificación.
- Campaña.
- Documento y adjunto.
- Romaneo, entrada y resultado.
- Auditoría y sincronización.

Los agregados y relaciones definitivos se documentarán en el diseño de dominio.

## 11. Operación local y cloud

### 11.1 Local

- Las sucursales venderán sin Internet.
- La operación local es la autoridad resiliente y offline-first durante una interrupción de Internet.
- En el perfil inicial, la notebook POS y el nodo local se alojarán en el mismo equipo físico, sin eliminar la frontera lógica del nodo.
- La arquitectura de producto conservará la posibilidad de que varias terminales compartan los servicios locales de una sucursal.
- La administración local Windows tendrá paridad funcional con la administración web para las operaciones de gestión de negocio autorizadas.
- POS, venta, impresión y operaciones directas con periféricos permanecerán exclusivamente en el entorno local.
- Los cambios pendientes sobrevivirán reinicios.
- Se mostrará el estado de conexión y sincronización.

El cliente de escritorio local deberá ofrecer una experiencia administrativa moderna y atractiva, sin que ello determine todavía un framework o tecnología de interfaz.

### 11.2 Cloud

- Pedidos y pagos online.
- Administración remota y móvil, con paridad funcional respecto de la administración local para las operaciones de gestión de negocio autorizadas.
- Reportes consolidados, remotos y móviles.
- Campañas y notificaciones.
- IA.
- Backups.
- Intercambio de información entre sucursales.

La web requerirá inicio de sesión administrativo para la gestión de negocio. No reproduce el POS ni controla periféricos; esas operaciones permanecen locales.

La administración web mostrará la frescura de sus datos. Mientras una sucursal esté desconectada, el estado remoto será válido solamente hasta la última sincronización cloud exitosa de esa sucursal; no se presentará como tiempo real.

### 11.3 Sincronización

- Identificadores globalmente únicos.
- Operaciones idempotentes y reintentables.
- Sin duplicación de pedidos, pagos, ventas o movimientos.
- Persistencia de eventos hasta confirmación.
- Diagnóstico de retrasos, fallos y conflictos.
- Descarga de pedidos online al entorno local.
- Carga de operaciones locales hacia cloud.
- Autoridad explícita por tipo de dato.
- Revisión humana de conflictos no resolubles.
- Prohibición de sobrescribir silenciosamente movimientos confirmados.
- Conservación de origen y auditoría.
- Antes de reemplazar el equipo local, la sincronización deberá finalizar con confirmación durable del cloud y reconciliación del estado local sincronizable. El procedimiento de reemplazo se define como arquitectura operativa.

Autoridad preliminar:

| Información | Autoridad primaria |
|---|---|
| Venta y caja | Sucursal local |
| Hardware | Sucursal local |
| Pedido online | Cloud |
| Pago online | Pasarela/cloud |
| Preparación y entrega | Operación local |
| Stock de sucursal | Sucursal local |
| Campañas | Cloud |
| Reportes consolidados | Cloud |
| Maestros compartidos | Pendiente por tipo |
| Usuarios y permisos | Pendiente, con continuidad local |

### 11.4 Perfil inicial de sucursal y reemplazo de equipo

- El perfil inicial de sucursal utilizará una notebook POS que alojará físicamente el nodo local y sus datos locales.
- El modelo multiestación se conserva como arquitectura de producto: una futura terminal se comunicará con el mismo límite lógico del nodo local y no tendrá una base de negocio independiente.
- El reemplazo de notebook será una operación coordinada, distinta de una actualización de aplicación.
- El nuevo equipo recibirá una identidad nueva al iniciar sesión o enrolarse y completará un bootstrap consistente de organización y sucursal mediante un snapshot versionado más deltas antes de habilitar el POS.
- Antes de operar se validarán datos operativos y periféricos; después se retirará y revocará el equipo anterior.
- El cloud solo puede restaurar datos que recibió y confirmó durablemente. Por ello se conservará un backup local verificable como resguardo adicional.

La definición operativa completa se mantiene en [SINGLE_DEVICE_BRANCH_PROFILE.md](./SINGLE_DEVICE_BRANCH_PROFILE.md) para no sobrecargar este PRD.

## 12. Flujos principales

### 12.1 Venta presencial

1. El cajero inicia sesión y abre caja.
2. Escanea o busca productos.
3. El sistema interpreta cantidades, pesos y precios.
4. Se selecciona cliente y lista cuando corresponda.
5. Se registran uno o más medios de pago o cuenta corriente.
6. Se confirma la venta.
7. Se afectan caja, pagos, stock y cuenta.
8. Se imprime el comprobante.

### 12.2 Pedido online pagado

1. El cliente accede mediante enlace seguro.
2. Consulta catálogo y precios.
3. Crea el pedido y elige entrega o retiro.
4. Paga online.
5. El pedido recibe prioridad configurable.
6. La sucursal lo descarga.
7. Se prepara, pesa y ajusta.
8. Se entrega o despacha.
9. Los estados se sincronizan.

### 12.3 Pedido por WhatsApp

1. Un usuario identifica al cliente.
2. Carga el pedido con origen WhatsApp.
3. Define entrega y pago.
4. El pedido sigue el circuito común.

### 12.4 Compra y recepción

1. Se registra o importa el documento.
2. OCR/IA propone datos si corresponde.
3. Un usuario revisa y confirma.
4. Se mueve inventario.
5. Se actualiza la cuenta del proveedor.
6. Se conservan costos y auditoría.

### 12.5 Reparto

1. Se agrupan pedidos preparados.
2. Se asignan responsable y vehículo.
3. Se imprimen remitos.
4. Se registran entregas, cobros, devoluciones e incidentes.
5. El repartidor rinde los valores.

### 12.6 Cierre diario

1. Se consolidan movimientos.
2. El usuario declara importes.
3. Se calculan diferencias.
4. Se justifican y aprueban cuando corresponde.
5. Se emite el reporte.
6. Se sincroniza la información.

## 13. Reglas transversales

- Toda operación pertenece a una organización.
- Toda operación física identifica sucursal.
- El usuario opera solamente en su alcance.
- Se conserva el origen de pedidos, ventas y movimientos.
- Saldos y stock se explican por movimientos.
- Anulaciones generan reversos auditables.
- Estado operativo, pago, facturación y entrega son independientes.
- Los precios aplicados se conservan históricamente.
- Un reintento no duplica pedidos ni pagos.
- Las importaciones se validan antes de confirmar.
- Las acciones automáticas sensibles son trazables.
- La diferencia entre peso estimado y real se resolverá mediante una política explícita.
- La reserva de stock dependerá de reglas por canal y estado.
- Todo ABM del sistema —entidades de negocio, maestros, parámetros y configuración— mostrará los mismos datos autorizados en dos vistas seleccionables: una vista tabular y una vista visual de tarjetas o cuadrícula.
- La disponibilidad de ambas vistas es una regla unificada para todas las entidades, sin alterar permisos, disponibilidad de datos ni reglas de negocio.
- La paridad funcional entre administración local y web se refiere a las operaciones de negocio autorizadas, no al POS ni a las operaciones directas de periféricos, que son locales.
- Las experiencias de administración local y web pueden evolucionar como interfaces independientes; no se exige reutilizar el código de interfaz entre ambas.

### 13.1 Gestión transversal de archivos, cambios y trazabilidad

- Existirá un componente común para cargar, consultar y vincular archivos, documentos e imágenes con los procesos de negocio que los requieran.
- Los archivos conservarán su relación con la organización, sucursal y operación o entidad que los originó, cuando corresponda.
- Los usuarios autorizados podrán editar información mientras el estado de la operación lo permita, sin alterar el significado de los registros históricos.
- Cuando una modificación directa no sea válida, se usarán correcciones, anulaciones o reversos controlados, con motivo y sin eliminación silenciosa.
- La auditoría distinguirá quién realizó una acción, cuándo, desde qué contexto y por qué; para cambios, conservará el valor anterior y el resultante cuando aplique.
- Los logs de operaciones del sistema registrarán la ejecución y el resultado de operaciones manuales o automáticas relevantes, incluidas sincronizaciones, integraciones y errores, para diagnóstico autorizado. Estos logs complementan la auditoría de negocio y seguridad; no la reemplazan.
## 14. Requisitos no funcionales

### 14.1 Disponibilidad

- Venta sin Internet.
- Persistencia de cambios ante reinicios.
- Reanudación automática de sincronización.
- Backups y restauración comprobables.

### 14.2 Rendimiento

- Respuesta inmediata percibida en búsqueda y escaneo.
- Confirmación local sin esperar Internet.
- Reportes pesados aislados del flujo de caja.
- Objetivos cuantitativos a definir con mediciones reales.

### 14.3 Seguridad

- Autenticación segura.
- Autorización en servicios, no solo interfaz.
- Aislamiento entre organizaciones.
- Comunicaciones cifradas.
- Protección de tokens y credenciales.
- Retención y minimización de datos.

### 14.4 Auditoría

Se registrarán como mínimo:

- Sesiones.
- Aperturas y cierres.
- Ventas, devoluciones y cancelaciones.
- Descuentos y cambios de precios.
- Cobros, pagos y ajustes.
- Movimientos de stock.
- Publicación de listas.
- Importaciones.
- Confirmaciones de OCR/IA.
- Cambios de permisos y configuración.
- Reimpresiones.

### 14.5 Usabilidad

- POS optimizado para teclado, lector, mouse y tacto.
- Controles grandes y legibles.
- Pocos pasos para acciones frecuentes.
- Mensajes de error accionables.
- Identificación visible de sucursal, caja, usuario y sincronización.
- El cliente administrativo local de Windows será moderno y atractivo, sin imponer una tecnología de interfaz.
- La aplicación soportará exactamente dos temas visuales: claro y oscuro. No se incluyen temas adicionales en esta definición.
- Administración web responsive, con indicación visible de la frescura de datos por sucursal cuando corresponda.

### 14.6 Compatibilidad

- Aplicación local para Windows moderno.
- Arquitectura inicial Windows x64.
- Una sola base de código; no soluciones separadas para x86 y x64.
- x86 o ARM solamente si surge una necesidad real.

### 14.7 Observabilidad

- Logs correlacionables.
- Estado de base, sincronización y periféricos.
- Diagnóstico por terminal y sucursal.
- Métricas de operaciones pendientes.
- Versión instalada.
- Exportación segura de diagnóstico.

## 15. Métricas de éxito

- Tiempo promedio de venta.
- Ventas completadas sin Internet.
- Diferencias de caja y stock.
- Pedidos entregados a tiempo.
- Tiempo por estado.
- Pedidos por canal.
- Conversión y pago online.
- Deuda total y vencida.
- Tiempo de carga de compras y precios.
- Errores de importación.
- Disponibilidad de terminales y hardware.
- Retraso de sincronización.
- Cumplimiento de tareas.

Las metas numéricas se establecerán luego de relevar la operación actual.

## 16. Orden de implementación

El orden prioriza frecuencia y dependencias. Todo pertenece al producto.

### Etapa 1 — Operación comercial local

- Organizaciones, sucursales, usuarios y permisos.
- Catálogo y listas minorista/mayorista.
- POS y hardware.
- Ventas, pagos, cajas y cierre diario.
- Clientes.
- Inventario por movimientos.
- Auditoría y respaldo.

### Etapa 2 — Pedidos y distribución

- Pedidos omnicanal.
- Preparación y estados.
- Cuentas de clientes.
- Ciudades, zonas y direcciones.
- Repartos, remitos y rendiciones.
- Tareas y responsables.

### Etapa 3 — Compras y finanzas

- Proveedores, compras y recepciones.
- Cuentas de proveedores.
- Cuentas financieras.
- Caja chica y gastos.
- Empleados y cuentas internas.
- Importación/exportación y precios masivos.

### Etapa 4 — Cloud y canal online

- Sincronización.
- Administración web/móvil.
- Reportes consolidados.
- Pedidos y pagos online.
- Notificaciones y backups.

### Etapa 5 — Automatización y analítica

- Campañas.
- Tableros avanzados.
- Asistente de IA.
- Recepción inteligente de documentos.
- Alertas proactivas.

### Etapa 6 — Romaneo integrado

- Relevamiento definitivo.
- Romaneo manual asistido.
- Rendimientos y costeo.
- Movimientos confirmados de stock.
- Reportes.
- Integración con el producto externo de romaneo automático.

Sincronización, aislamiento, auditoría e inventario deberán contemplarse desde el diseño inicial.

## 17. Decisiones confirmadas

- El producto supera un POS tradicional.
- El primer vertical es carnicerías y distribución cárnica.
- El núcleo será reutilizable por otros comercios.
- El dominio de productos separará producto, presentación, categoría comercial, unidades y comportamiento de venta e inventario; las clasificaciones por vertical serán opcionales y estructuradas.
- Todo el alcance del PRD pertenece al producto.
- El romaneo operativo pertenece al producto y se implementará al final.
- La app de romaneo automático con imágenes/video y ML será independiente.
- La aplicación local será para Windows moderno x64.
- La primera organización tiene dos sucursales; su perfil inicial por sucursal utiliza una notebook POS que aloja físicamente el nodo local.
- La arquitectura de producto conserva una topología escalable de varias terminales por sucursal.
- La venta local continuará sin Internet.
- Existirá acceso cloud/web.
- Habrá varias listas de precios.
- Habrá caja chica, gastos, cuentas y cuentas corrientes.
- Todos los pedidos compartirán circuito.
- Los pedidos online admitirán pago.
- Cada producto tendrá una pestaña Imágenes con una miniatura y una imagen destinada al catálogo y carrito online.
- Los maestros y listas de parámetros o configuración autorizados podrán alternar entre vista tabular y vista visual de tarjetas o cuadrícula; Productos y Clientes son casos obligatorios.
- La aplicación soportará únicamente los temas claro y oscuro.
- Las administraciones local Windows y web tendrán paridad funcional para la gestión autorizada; POS y periféricos directos son locales.
- La operación local es la autoridad resiliente ante una caída de Internet y la web informará la última sincronización cloud exitosa de una sucursal desconectada.
- No se exige reutilizar el código de interfaz entre escritorio y web.
- Las tecnologías todavía no están cerradas.

## 18. Decisiones pendientes

### Dominio

- Flujo y costeo real del romaneo.
- Diferencias entre peso estimado, preparado y facturado.
- Reserva de inventario y stock negativo.
- Lotes, vencimientos y trazabilidad sanitaria.
- Datos compartidos o particulares por sucursal.
- Transferencias entre sucursales.
- Alcance de nómina.
- Matriz final de permisos.
- Reglas de aprobación.

### Integraciones

- Modelos y protocolos de balanzas e impresoras.
- Pasarela de pago.
- Facturación electrónica.
- Proveedores de notificaciones.
- Formatos Excel.
- Documentos de frigoríficos.

### Arquitectura

- Bases local y cloud.
- Multitenencia física.
- Topología local.
- Autoridad de datos.
- Sincronización y conflictos.
- Actualizaciones.
- Backup y recuperación.
- Tecnologías desktop, web y servicios.
- Acceso local ante caída de identidad cloud.

### Producto

- Nombre.
- Planes, módulos y licenciamiento.
- Onboarding.
- Soporte y mantenimiento.

## 19. Riesgos

- Subestimar la sincronización offline/cloud.
- Implementar demasiados módulos simultáneamente.
- Duplicar lógica local y web.
- Modelar saldo o stock sin movimientos.
- Automatizar con IA sin revisión.
- Desconocer el hardware real.
- Confundir dos sucursales con replicación directa.
- Mezclar facturación, pago, entrega y operación.
- Diseñar solo para carnicerías.
- Generalizar en exceso y perjudicar al primer cliente.
- Ampliar nómina sin análisis legal.
- No definir qué ocurre con pedidos online durante desconexiones.

## 20. Documentación derivada

- README actualizado.
- Glosario.
- Mapa de módulos.
- Matriz de permisos.
- Catálogo de estados y transiciones.
- Modelo conceptual de datos.
- SDD local/cloud.
- Diseño de sincronización.
- Especificaciones de inventario y cuentas.
- Relevamiento de hardware.
- Especificación de pedidos online y pagos.
- Especificación fiscal.
- Seguridad y auditoría.
- Plan de pruebas.
- ADR por decisión tecnológica.
- Documento de descubrimiento del romaneo.
- [Perfil inicial de sucursal de un equipo y reemplazo coordinado](./SINGLE_DEVICE_BRANCH_PROFILE.md).

## 21. Criterio de visión completa

La visión estará completa cuando una organización pueda:

1. Administrar usuarios, sucursales, clientes, contactos, empleados y proveedores.
2. Mantener catálogo, costos, listas e inventario.
3. Comprar y recibir mercadería.
4. Vender localmente sin Internet.
5. Recibir pedidos desde todos los canales, incluido online con pago.
6. Preparar, despachar y entregar.
7. Gestionar cajas, caja chica, cuentas, gastos y cuentas corrientes.
8. Explicar todas sus deudas y saldos.
9. Coordinar tareas y notificaciones.
10. Ejecutar campañas.
11. Consultar información consolidada local y remotamente.
12. Obtener reportes y respuestas verificables mediante IA.
13. Registrar romaneos y sus movimientos.
14. Integrarse con el producto independiente de romaneo automático.

## 22. Próximo paso

Validar este PRD con la operación real de ambas sucursales y transformar las decisiones pendientes en documentos de descubrimiento. Luego se debatirá y documentará la arquitectura técnica sin comprometer prematuramente tecnologías.

La visita a la actividad de romaneo deberá registrar vocabulario, actores, pasos, documentos, mediciones, excepciones, mermas y criterios de costeo.




