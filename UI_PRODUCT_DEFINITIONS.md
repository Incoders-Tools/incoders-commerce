# Definiciones de interfaz del producto

Estas decisiones establecen una experiencia consistente para la gestión comercial y el canal online antes de redactar especificaciones o implementar. Complementan el [PRD](./PRD.md) sin modificar el alcance funcional ni las decisiones arquitectónicas pendientes.

## Decisiones confirmadas

| Área | Definición de producto |
|---|---|
| Imágenes de productos | La ficha de cada producto incluye la pestaña **Imágenes** con una imagen miniatura y una imagen de producto. |
| Rol de la miniatura | Identifica el producto en contextos compactos de gestión. |
| Rol de la imagen de producto | Presenta el producto en el catálogo y el carrito de compras online. |
| Vistas de ABM | Todo ABM —entidades de negocio, maestros, parámetros y configuración— muestra los mismos datos autorizados en una vista tabular y una vista visual de tarjetas o cuadrícula. |
| Regla unificada | Ambas vistas son obligatorias para toda entidad del sistema; Productos y Clientes son ejemplos, no excepciones. |
| Temas | La aplicación admite exactamente dos temas: claro y oscuro. |
| Administración | Las administraciones local Windows y web requieren inicio de sesión administrativo y tienen paridad funcional para las operaciones de negocio autorizadas. |
| Excepción local | POS y operaciones directas con periféricos permanecen en el entorno local. |
| Autoridad offline | Durante una caída de Internet, la operación local es la autoridad resiliente y offline-first. |
| Datos remotos | La web muestra la frescura de datos: una sucursal desconectada se refleja hasta su última sincronización cloud exitosa, no como tiempo real. |
| Captura documental | La captura local desde cámara, escáner o archivo puede asistir la recepción de comprobantes de proveedores; toda sugerencia OCR/IA requiere revisión humana antes de un movimiento definitivo de inventario. |

## Alcance y consistencia

- La selección de vista cambia la presentación, no los permisos, el alcance de datos ni las reglas de negocio.
- Todo ABM conserva el mismo patrón de selección de vistas; cada entidad puede adaptar su representación visual sin perder ese contrato común.
- Cada entidad conserva una identidad visual consistente entre sus dos vistas.
- Las dos imágenes del producto responden a propósitos distintos y deben conservar esa diferenciación en las experiencias local y online.
- La vista visual está orientada a reconocer rápidamente entidades cuando una lista corta se comprende mejor por tarjetas o cuadrícula que por filas.
- Estas decisiones aplican tanto a la evolución del primer vertical como al núcleo reutilizable para otros comercios.
- El cliente administrativo local deberá ser moderno y atractivo; esta expectativa de producto no selecciona un framework ni una tecnología de interfaz.
- La paridad funcional no exige reutilizar el código de interfaz entre escritorio y web: son dos experiencias que pueden evolucionar de forma independiente sobre las mismas reglas de negocio y permisos.

## Fuera de alcance por ahora

- Galerías de imágenes, múltiples imágenes de producto, videos o gestión multimedia avanzada.
- Temas adicionales, temas personalizados por organización o personalización libre de colores.
- Decisiones de framework, componentes, almacenamiento de archivos, dimensiones de imágenes o implementación técnica.
- Especificaciones de interacción detalladas, código o planificación de implementación.
- Una selección definitiva de tecnología, framework o estrategia de reutilización de código para escritorio y web.

## Revisión posterior

Antes de especificar cada módulo, validar los flujos reales de catálogo, clientes y administración para precisar criterios de visualización, sin cambiar estas decisiones base salvo una decisión de producto explícita.
