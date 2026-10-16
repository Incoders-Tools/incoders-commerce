# Dominio de productos

Este documento define el dominio de productos reutilizable y de negocio para Incoders Commerce. Mantiene el núcleo comercial independiente de cualquier rubro y, al mismo tiempo, permite vocabulario vertical opcional cuando un comercio lo necesita. Es una definición funcional, no un diseño técnico ni un modelo de datos.

## Resultado

El mismo catálogo puede describir carnes, alimentos, bebidas, carbón, aceites y mercadería futura sin tratar un rubro o una característica física como un tipo de producto fijo.

## Distinciones fundamentales

| Concepto | Responsabilidad de negocio | No debe confundirse con |
|---|---|---|
| **Producto** | La identidad comercial: lo que el negocio reconoce, nombra, informa y puede activar o discontinuar. | Un envase, un código de barras, una cantidad de stock o un subtipo específico de un rubro. |
| **Presentación** | Una forma vendible o gestionable en inventario de un producto, como una botella, bolsa, bandeja, caja o pieza pesada individualmente. Contiene los hechos comerciales que cambian entre formas. | Un producto diferente solo porque cambian el envase, la cantidad o el comportamiento de venta. |
| **Categoría comercial** | Una agrupación comercial navegable y reportable, organizada como jerarquía. | Una variante de producto, un comportamiento de inventario o una clasificación vertical. |
| **Unidad** | La medida de negocio usada en un contexto comercial específico: compra, almacenamiento o venta. | El comportamiento que determina cómo se gestiona la cantidad. |
| **Comportamiento de venta e inventario** | La regla que explica si una presentación se gestiona por conteo, por peso medido o por peso determinado individualmente. | Una categoría o una familia rígida de productos. |
| **Clasificación vertical** | Vocabulario estructurado opcional, útil solamente para un rubro en particular. | Un campo obligatorio o un reemplazo del núcleo reutilizable. |

## Producto y presentación

Un producto es el concepto comercial estable. Una presentación es la forma en la que ese producto se compra, almacena, prepara o vende. Un producto puede tener una o más presentaciones cuando esas formas necesitan un tratamiento comercial distinto.

Ejemplos:

- El producto **aceite de girasol** puede presentarse como una botella de 900 ml y un envase de 5 L.
- El producto **carbón** puede presentarse como una bolsa de 3 kg y una bolsa de 10 kg.
- El producto **agua con gas** puede presentarse como una botella individual y como un multipack.
- El producto **asado de tira** puede presentarse para venta por peso medido o como una bandeja preparada de peso variable.

Las presentaciones hacen explícitos el envase, los códigos, las unidades aplicables y el comportamiento de venta e inventario sin forzar la duplicación de productos. Un negocio puede definir productos separados cuando tengan identidades comerciales diferentes; la decisión es comercial, no un atajo de implementación.

## Categorías comerciales jerárquicas

Las categorías comerciales organizan el catálogo desde lo general hasta lo específico y pueden usarse para navegación, precios, gestión de surtido e informes. Una categoría puede tener categorías hijas, por ejemplo:

```text
Alimentos
└── Bebidas
    ├── Agua
    └── Aceites

Carnes
└── Carne fresca
    ├── Vacuna
    └── Porcina
```

La jerarquía es comercial y configurable. No determina cómo se mide, almacena o procesa un producto, ni reemplaza clasificaciones verticales opcionales como especie o corte.

## Comportamiento de venta e inventario

Cada presentación declara el comportamiento necesario para operarla de manera consistente en ventas, pedidos, stock e informes.

| Comportamiento | Significado | Ejemplos |
|---|---|---|
| **Por unidad** | La cantidad es un conteo de unidades discretas. | Una botella, una bolsa de carbón, una lata, una bandeja sellada. |
| **Por peso** | La cantidad se mide por peso. | Granos a granel, fiambres, un corte vendido por kilogramo. |
| **Por peso variable** | Cada pieza física o envase tiene un peso real que puede conocerse recién al recibirlo, prepararlo o venderlo. Por ello, las cantidades planificadas y finales pueden diferir según una política de negocio explícita. | Una bandeja de carne preparada, una horma de queso, un pescado entero. |

Este comportamiento se aplica a la presentación, no a una categoría o vertical completo. Por ejemplo, una carnicería puede vender una presentación por kilogramo y otra como bandeja de peso variable; un comercio de alimentos puede usar el mismo comportamiento para queso o productos frescos.

## Unidades de compra, almacenamiento y venta

El negocio puede usar unidades diferentes para compra, almacenamiento y venta. Las unidades elegidas describen la realidad comercial en cada etapa y deben resultar comprensibles para el personal y en los documentos históricos.

| Contexto | Pregunta que responde | Ejemplos |
|---|---|---|
| **Unidad de compra** | ¿Cómo se adquiere el artículo al proveedor? | Caja, cajón, media res, kilogramo, tambor. |
| **Unidad de almacenamiento** | ¿Con qué medida se controla la disponibilidad? | Botella, bolsa, kilogramo, pallet, porción. |
| **Unidad de venta** | ¿Cómo lo compra el cliente? | Unidad, kilogramo, pack, litro. |

El dominio admite una relación definida por el negocio entre estas unidades cuando una presentación pasa de un contexto a otro. Esa relación debe ser suficientemente explícita para explicar stock, conversión, merma y la cantidad mostrada al cliente; su representación técnica queda deliberadamente fuera de este documento.

## Clasificaciones verticales opcionales

Las clasificaciones verticales enriquecen un producto o presentación solamente cuando una capacidad de negocio lo requiere. Son vocabulario de negocio estructurado y gobernado, no tipos de producto codificados de manera rígida, y permanecen ausentes en organizaciones que no usan el vertical correspondiente.

### Carnicería y distribución cárnica

Cuando está habilitada la capacidad de carnicería, los productos o presentaciones pertinentes pueden incluir información estructurada opcional como:

- **Especie**, por ejemplo vacuna, porcina, aviar u otra especie configurada.
- **Corte**, seleccionado de un catálogo controlado de cortes adecuado al negocio y, cuando corresponda, a su especie.
- **Estado de procesamiento**, como el estado definido por el negocio para recepción, desposte, recorte, preparación, conservación o aptitud para la venta.

Estas clasificaciones respaldan compras, procesamiento, trazabilidad, romaneo, stock e informes. No crean clases rígidas de productos para vaca, cerdo, pollo ni ninguna otra especie. Un artículo cárnico sigue siendo un producto con una o más presentaciones; su información vertical opcional agrega significado sin modificar el núcleo reutilizable.

## Reglas de extensión

1. Mantener los conceptos de producto, presentación, categoría, unidad y comportamiento comunes a todos los verticales comerciales.
2. Agregar una clasificación vertical solo cuando represente un concepto de negocio estructurado con valor operativo o de reporte claro.
3. No usar categorías para codificar comportamiento de medición, envase, especie, cortes o estados de procesamiento.
4. No volver obligatoria una clasificación vertical para productos ajenos a ese vertical.
5. Preferir vocabulario controlado configurable antes que nuevos tipos rígidos de producto cuando un vertical necesita más detalle.
6. Conservar el significado usado al momento de una venta, compra, movimiento de inventario o transformación para que las operaciones históricas sigan siendo comprensibles.

## Límite de alcance

Este documento define lenguaje y límites de negocio. No selecciona tecnologías, estructuras de persistencia, APIs, identificadores, esquemas ni patrones de implementación. Esas decisiones requieren trabajo separado de arquitectura y diseño.
