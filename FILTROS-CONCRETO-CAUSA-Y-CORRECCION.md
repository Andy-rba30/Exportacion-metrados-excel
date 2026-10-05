# Por qué los filtros de concreto se quedaban "pegados" y cómo quedó corregido

**Estado: corregido** en el commit `c8559b8` de la rama `claude/happy-sagan-dcz244`
(`src/ExportacionMetrados/Core/Metrado/GeneradorFiltrosVista.cs`). La corrección cubre **todos** los
elementos de concreto: vigas, columnas, cimentaciones, losas, muros y "Otros", porque el cambio está en el
bucle que crea los filtros predeterminados de concreto de todas las categorías, no solo en el de suelos.

## Qué pasaba

Cada filtro predeterminado de concreto (`Metrado - Concreto - Losas`, `... - Vigas`, `... - Columnas`,
`... - Cimentaciones`, `... - Muros`, `... - Otros`) se creaba con **dos condiciones**:

1. la categoría de Revit (Suelos, Armazón estructural, Pilares estructurales...), y
2. `Metrado - Material` = `CONCRETO`.

No miraba `Metrado - Elemento`. Por eso un suelo al que escribías `SOLADO` en `Metrado - Elemento` seguía
cumpliendo las dos condiciones de `Metrado - Concreto - Losas` (es un suelo y es concreto) **y además** las
de su filtro propio `Metrado - Concreto - SOLADO` (material `CONCRETO` y elemento `SOLADO`).

Cuando un elemento cumple dos filtros de la misma vista, Revit aplica el que está **más arriba** en la lista de
Visibilidad/Gráficos → Filtros. Los predeterminados se añaden a la vista antes que los propios, así que
siempre quedan encima. Resultado:

- el suelo conservaba el color del filtro de Losas aunque el parámetro ya dijera `SOLADO`;
- al quitar la visibilidad de todos los filtros y devolverla solo al propio, el suelo seguía oculto o
  coloreado por Losas, porque el filtro de Losas, más arriba, seguía mandando;
- daba la impresión de que el elemento "se desligaba" del filtro propio y "volvía" al anterior.

Lo mismo ocurría con cualquier otro elemento de concreto al que dieras un texto propio: una viga con
`ESCALERAS` seguía dentro de `Metrado - Concreto - Vigas`, una zapata con `SOBRECIMIENTOS` dentro de
`Metrado - Concreto - Cimentaciones`, etc.

## Por qué con estructuras metálicas sí funcionaba

Los filtros predeterminados de acero estructural (`Metrado - Acero estructural - Vigas`, etc.) ya se creaban
con una **tercera condición**: `Metrado - Elemento` = su grupo (`VIGAS`, `COLUMNAS`, `CONEXIONES`, `OTROS`).
Una viga metálica con `BARANDAS` deja de cumplir `Metrado - Elemento = VIGAS`, sale del filtro
predeterminado y solo la pinta su filtro propio. Los filtros de refuerzo también filtran por el valor exacto
de `Metrado - Elemento`. Solo los de concreto carecían de esa regla.

## La corrección

Los filtros predeterminados de concreto ahora exigen también `Metrado - Elemento` = su grupo:

| Filtro | Reglas antes | Reglas ahora |
|---|---|---|
| `Metrado - Concreto - Vigas` | Armazón estructural + `Material = CONCRETO` | Armazón estructural + `Material = CONCRETO` + `Elemento = VIGAS` |
| `Metrado - Concreto - Columnas` | Pilares + `Material = CONCRETO` | Pilares + `Material = CONCRETO` + `Elemento = COLUMNAS` |
| `Metrado - Concreto - Cimentaciones` | Cimentación estructural + `Material = CONCRETO` | + `Elemento = CIMIENTOS` |
| `Metrado - Concreto - Losas` | Suelos + `Material = CONCRETO` | + `Elemento = LOSAS` |
| `Metrado - Concreto - Muros` | Muros + `Material = CONCRETO` | + `Elemento = MUROS` |
| `Metrado - Concreto - Otros` | Conexiones, modelos genéricos y cubiertas + `Material = CONCRETO` | + `Elemento = OTROS` |

Así cada elemento de concreto cumple **un solo filtro** del plugin: el predeterminado de su grupo si tiene el
texto estándar, o su filtro propio si le escribiste un texto propio. Ya no hay dos filtros compitiendo y el
orden en la lista de la vista deja de importar.

Es la misma regla con la que se filtran las tablas de concreto (`Metrado concreto - Losas` ya filtraba por
`Metrado - Elemento = LOSAS`), así que ahora el filtro de colores y la tabla muestran exactamente lo mismo.

Además, al crear un filtro con dos parámetros se comprueba que **ambos** admiten filtro en la categoría; antes
solo se comprobaba el primero.

## Qué tienes que hacer en tu modelo

1. Instalar la versión compilada con este cambio.
2. Pulsar una vez **Actualizar filtros** (Parámetros y filtros, pestaña 2) o repetir el metrado. Los filtros
   predeterminados que ya existen en el proyecto se **actualizan** con la regla nueva; no se duplican ni hay
   que borrarlos.
3. Comprobar en Visibilidad/Gráficos (VV) → Filtros → Editar/Nuevo: `Metrado - Concreto - Losas` debe mostrar
   las tres reglas (`Metrado - Material` igual a `CONCRETO` y `Metrado - Elemento` igual a `LOSAS`).

Si algún filtro tenía el texto estándar pero el elemento no tiene `Metrado - Elemento` escrito (por ejemplo,
se creó el parámetro pero nunca se ejecutó el metrado sobre esa categoría), ese elemento no entrará en ningún
filtro hasta que ejecutes la pestaña 1 o el metrado automático, que rellenan el parámetro.

## Lo que no cambia

- Los filtros propios (`Metrado - Concreto - SOLADO`, `Metrado - Refuerzo - SOLADO`) se crean igual que antes,
  con el valor exacto de los dos parámetros.
- Si el parámetro `Metrado - Elemento` no existiera en el proyecto, los filtros de concreto vuelven a crearse
  solo con la regla de material, como antes.
