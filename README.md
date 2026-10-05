# Exportación de Metrados a Excel (plugin para Revit)

Plugin para Autodesk Revit, escrito en C#, que exporta las **tablas de planificación / metrados**
(`ViewSchedule`) del proyecto a un libro de Excel (`.xlsx`), con una hoja por tabla.

- No requiere tener Microsoft Excel instalado (usa [ClosedXML](https://github.com/ClosedXML/ClosedXML)).
- Exporta las celdas **tal como se ven en Revit**: unidades, redondeo, totales, agrupaciones y filas
  de subtotal, porque lee el texto renderizado de la tabla con `ViewSchedule.GetCellText`.
- Convierte los valores numéricos a números reales de Excel conservando la unidad como formato de celda
  (por ejemplo `12.50 m²` queda como `12.5` con formato `#,##0.00 "m²"`), para que se puedan sumar
  y usar en fórmulas.
- Ventana de selección con buscador, selección múltiple y opciones de formato.
- Botón **Metrado automático**: calcula el concreto (m³), los perfiles metálicos (kg, por longitud × área de
  sección × densidad) y el acero de refuerzo (kg) de vigas, columnas y otros elementos estructurales leyendo
  directamente el modelo, sin necesitar tablas de planificación.
- Filtros de vista por colores (opcionales) para comprobar visualmente qué elementos entran en cada tabla:
  concreto, acero estructural y refuerzo por partición, cada uno con su color, aplicados a la vista activa.
- Botón **Parámetros y filtros**: escribe los mismos parámetros que el metrado automático y crea los filtros
  de colores, pero **sin crear tablas**, para poder cambiar a mano `Metrado - Material` y `Metrado - Elemento`
  (por ejemplo `ESCALERAS`); después lee los valores que haya en el modelo y crea **tablas propias** con ellos,
  aparte de las predeterminadas.
- Botón **Metrado de encofrado** (panel *Encofrado*): calcula el encofrado (m²) de vigas, columnas,
  cimentaciones, losas y muros de concreto **según el elemento y su contexto**, leyendo la geometría real:
  columnas solo caras laterales descontando las vigas que llegan y la losa que las atraviesa; vigas costados y
  fondo descontando lo que entra en columnas y la losa que apoya; losas fondo y bordes descontando las vigas;
  cimentaciones solo bordes; muros las dos caras. Escribe `Metrado - Encofrado (m²)`, crea tablas y exporta a
  Excel el detalle de cada descuento.
- Integrado con el **contrato ARBA-comun** (`external/ARBA-comun`, versión 1.0.0): comparte con los add-ins de
  armado ARBA la pestaña **ARBA** de la cinta, los ocho parámetros compartidos con GUID fijo (`ARBA - Origen`,
  `ARBA - Código`, `ARBA - Anfitrión`, `Metrado - Partida`, `Metrado - Material`, `Metrado - Peso (kg)`,
  `Metrado - Pernos (und)`, `Metrado - Elemento`), la gramática de la partición del acero
  (`CATEGORIA - PREFIJO-marca[-codigo]`), la tabla de **misceláneos** por partida (rejillas, ángulos) y el botón
  **Migrar particiones y origen** para modelos armados con versiones anteriores. Ver
  [`external/ARBA-comun/CONTRATO.md`](external/ARBA-comun/CONTRATO.md).
- Compatible con Revit 2021 a 2024 (.NET Framework 4.8), 2025 y 2026 (.NET 8) y 2027+ (.NET 10).

## Estructura

```
ExportacionMetrados.sln
NOTAS-ARBA-COMUN.md                Lo que el contrato ARBA-comun no cubre o conviene revisar (visto al integrarlo)
external/ARBA-comun/               Submódulo git: código común y contrato ARBA (parámetros, partición, cinta)
src/ExportacionMetrados/
├── App.cs                         Añade los cinco botones al panel "Metrados" y el de encofrado al panel "Encofrado" de la pestaña común "ARBA"
├── ExportarMetradosCommand.cs     Comando 1: exporta las tablas de planificación elegidas
├── MetradoAutomaticoCommand.cs    Comando 2: metrado automático de concreto y acero
├── ParametrosMetradoCommand.cs    Comando 3: parámetros y filtros sin tablas; tablas propias desde los parámetros
├── AsignarParticionCommand.cs     Comando 4: partición del acero no creado por ARBA ("VIGAS - MAN-V1")
├── MigrarParticionesCommand.cs    Comando 5: migra particiones antiguas y origen al contrato (código común)
├── MetradoEncofradoCommand.cs     Comando 6 (panel Encofrado): metrado de encofrado según el elemento y su contexto
├── ExportacionMetrados.addin      Manifiesto que Revit lee para cargar el plugin
├── Core/
│   ├── LectorTablas.cs            Lee las tablas de Revit (encabezados y cuerpo)
│   ├── ExportadorExcel.cs         Escribe el .xlsx con ClosedXML
│   ├── OpcionesExportacion.cs     Opciones y resultado de la exportación
│   └── Metrado/
│       ├── CalculadorMetrado.cs   Recorre el modelo: volúmenes de concreto, peso de perfiles, misceláneos y barras
│       ├── ClasificadorElementos.cs Parámetros del contrato (Material, Peso, Elemento...), grupos, particiones MAN
│       ├── GeneradorTablasRevit.cs Crea las tablas de planificación de metrado en el proyecto
│       ├── GeneradorFiltrosVista.cs Filtros de vista por colores para comprobar el metrado
│       ├── LectorCombinaciones.cs Lee los valores de "Metrado - Material" / "Metrado - Elemento" del modelo (tablas propias)
│       ├── Encofrado/
│       │   ├── ModelosEncofrado.cs    Reglas por elemento (caras laterales / fondo), opciones y resultados
│       │   ├── CalculadorEncofrado.cs Caras de cada sólido y contacto con los demás elementos de concreto (booleanos / muestreo)
│       │   ├── ParametroEncofrado.cs  Parámetro compartido "Metrado - Encofrado (m²)" (propio del plugin, no del contrato)
│       │   └── ExportadorEncofrado.cs Excel: resumen, detalle por elemento y hoja de contactos
│       ├── GestorSubproyectos.cs  Reserva de subproyectos en modelos compartidos
│       ├── ExportadorMetrado.cs   Escribe las hojas Resumen, Concreto, Acero estructural, Acero y detalle
│       └── ModelosMetrado.cs      Opciones, grupos (incluido Misceláneos) y resultados del metrado
├── UI/
│   ├── SeleccionTablasWindow.xaml Ventana de selección de tablas
│   ├── MetradoAutomaticoWindow.xaml Ventana de opciones del metrado automático
│   ├── ParametrosMetradoWindow.xaml Ventana de "Parámetros y filtros" (dos pestañas: parámetros / tablas propias)
│   ├── MetradoEncofradoWindow.xaml Ventana de opciones del metrado de encofrado
│   ├── AsignarParticionWindow.xaml Ventana de "Asignar partición"
│   └── TablaItem.cs               Modelo de cada fila de la lista
└── Resources/                     Iconos de los botones
```

El código común se compila **como fuente** dentro del ensamblado del plugin (`Arba.Comun.props`, clases `internal`
en el namespace `Arba.Comun`), nunca como DLL compartida: Revit carga todos los add-ins en el mismo proceso y dos
versiones de una misma DLL chocarían. No se modifica nada dentro de `external/ARBA-comun`; lo que falte se anota en
`NOTAS-ARBA-COMUN.md`.

## Requisitos

- Windows con Autodesk Revit instalado (2021 o superior).
- [SDK de .NET](https://dotnet.microsoft.com/download) acorde a la versión de Revit: .NET 10 para Revit 2027,
  .NET 8 para 2025/2026 (el SDK 10 también compila esos destinos). Para Revit 2021-2024 basta con el SDK y el
  paquete de destino de .NET Framework 4.8 que instala Visual Studio 2022.

## Compilación e instalación

El repositorio incluye el código común ARBA-comun como **submódulo git**. Clónelo con los submódulos:

```powershell
git clone --recurse-submodules https://github.com/Andy-rba30/Exportacion-metrados-excel
# o, si ya lo tenía clonado sin submódulos:
git submodule update --init
```

Para subir de versión del contrato: `git -C external/ARBA-comun checkout vX.Y.Z` y commit del puntero.

Desde una terminal en la raíz del repositorio:

```powershell
# Revit 2027 (valor por defecto, .NET 10)
dotnet build -c Release

# Otra versión de Revit
dotnet build -c Release -p:RevitVersion=2026
dotnet build -c Release -p:RevitVersion=2024
```

El framework se elige automáticamente según la versión (2021-2024: net48; 2025-2026: net8.0-windows;
2027+: net10.0-windows). Si Revit 2027 en su equipo usa otro runtime, fuércelo:

```powershell
dotnet build -c Release -p:RevitVersion=2027 -p:TargetFramework=net8.0-windows
```

Para saber qué runtime usa su Revit, mire la versión de `coreclr.dll` en la carpeta de instalación de Revit
o el valor de `Microsoft.NETCore.App` en `Revit.runtimeconfig.json` dentro de esa misma carpeta.

El proyecto busca `RevitAPI.dll` y `RevitAPIUI.dll` en `C:\Program Files\Autodesk\Revit <versión>\`.
Si Revit está en otra ruta, pase la propiedad `RevitInstallDir`:

```powershell
dotnet build -c Release -p:RevitVersion=2027 -p:RevitInstallDir="D:\Autodesk\Revit 2027"
```

Si no encuentra `RevitAPI.dll` (otro equipo, Linux, integración continua), toma la API de los paquetes NuGet
`Nice3point.Revit.Api.RevitAPI` / `RevitAPIUI` de esa versión (los mismos que usa ARBA-comun para comprobar su
compilación); se puede forzar con `-p:RevitApiDesdeNuGet=true`. Esos ensamblados no se copian a la salida.

Al terminar la compilación el plugin se copia automáticamente a la carpeta de add-ins del usuario:

```
%AppData%\Autodesk\Revit\Addins\<versión>\ExportacionMetrados.addin
%AppData%\Autodesk\Revit\Addins\<versión>\ExportacionMetrados\*.dll
```

(Se puede desactivar la copia con `-p:DeployToRevit=false`.)

Reinicie Revit. La primera vez Revit preguntará si confía en el complemento; elija **Cargar siempre**.

### Instalación manual

Si prefiere no usar la copia automática, copie `ExportacionMetrados.addin` a
`%AppData%\Autodesk\Revit\Addins\<versión>\` y el contenido de `bin\Release\` a una subcarpeta
`ExportacionMetrados` dentro de esa misma ruta.

## Uso

1. Abra el proyecto en Revit.
2. Vaya a la pestaña **ARBA** (la comparten todos los add-ins ARBA), panel **Metrados**. Hay cinco botones:
   **Exportar a Excel** (exporta tablas de planificación ya existentes), **Metrado automático** (crea las tablas
   de metrado en Revit y opcionalmente las exporta, ver más abajo), **Parámetros y filtros** (parámetros y
   filtros sin tablas, y tablas propias a partir de los parámetros), **Asignar partición** y **Migrar
   particiones y origen** (ver sus apartados). En el panel **Encofrado** de la misma pestaña está **Metrado de
   encofrado**. Pulse **Exportar a Excel**.
3. Marque las tablas que desea exportar (si la vista activa es una tabla, aparece marcada).
   Puede filtrar por nombre o categoría y usar **Todas** / **Ninguna**.
4. Ajuste las opciones:
   - Incluir el nombre de la tabla como título.
   - Incluir encabezados de columna.
   - Convertir valores numéricos (conserva la unidad como formato).
   - Aplicar formato (negrita, bordes, anchos de columna, encabezado fijo).
5. Elija el archivo de destino y pulse **Exportar**.

Cada tabla se escribe en su propia hoja. El nombre de la hoja es el de la tabla, recortado a 31 caracteres
y sin los caracteres que Excel no admite (`[ ] * ? / \ :`); si hay nombres repetidos se añade `(2)`, `(3)`, etc.

## Metrado automático de concreto y acero

El botón **Metrado automático** crea las tablas de planificación de metrado **dentro del proyecto de Revit**
y, si se marca la opción, las exporta a Excel en la misma operación. (El botón *Exportar a Excel* es una
función aparte para tablas que ya existen en el proyecto.)

**Tablas que crea en Revit** por cada tipo de elemento marcado:

| Tabla | Contenido | Agrupación |
|---|---|---|
| `Metrado concreto - Vigas` / `Losas` / `Cimentaciones` | Elemento (familia y tipo), Material, Cantidad, Longitud o Área, Espesor, Volumen. Solo elementos con material de concreto. | Por tipo; total general. **Sin niveles** (una viga puede cruzar varios, las losas se metran por tipo en todo el edificio y las cimentaciones comparten el nivel de fundación). |
| `Metrado concreto - Columnas` / `Muros` | Nivel, Elemento, Material, Cantidad, Longitud o Área, Espesor, Volumen | Por nivel (encabezado y pie con totales), luego tipo; total general |
| `Metrado acero estructural - <elemento>` | Elementos cuyo material **no** es concreto (perfiles metálicos): Elemento, Material, Cantidad, Longitud, Área de sección, **Peso (kg)**. Los perfiles no se metran por volumen sino por peso. Solo se crea si existen. `Metrado acero estructural - Conexiones y anclajes` y `... - Otros` son tablas de varias categorías filtradas por "Metrado - Elemento" (`CONEXIONES` / `OTROS`): Categoría, Elemento, Cantidad, Peso (kg) = volumen × densidad. Toda tabla de elementos filtra además por su grupo en "Metrado - Elemento", así los pernos que un IFC trae como vigas no salen en la tabla de vigas. | Igual; las de varias categorías, por categoría de Revit y luego tipo |
| `Metrado acero estructural - Misceláneos` | **Contrato ARBA**: elementos con **"Metrado - Partida"** (rejillas, ángulos y otras piezas que un add-in ARBA o el usuario metran por partida), de cualquier categoría. Tabla de varias categorías filtrada por "Metrado - Elemento" = `MISCELANEOS`: Partida, Categoría, Elemento, `ARBA - Código`, Cantidad, **Peso (kg)** y **Pernos (und)** con totales. El peso que escribió su add-in se respeta; si no lo hay, volumen × densidad. Estas piezas **no** aparecen en Vigas, Conexiones ni Otros. | Por partida (encabezado y pie con totales), luego categoría de Revit y tipo; total general |
| `Metrado acero - <elemento>` | Refuerzo cuyo anfitrión es de ese tipo: filtra por el parámetro **"Metrado - Elemento"** (`VIGAS`, `COLUMNAS`, `CIMIENTOS`, `LOSAS`, `MUROS`), que el plugin escribe en cada armadura según su anfitrión real; la partición puede tener cualquier texto (respaldo del filtro: partición que empieza por `VIGAS - `, la forma del contrato). Columnas: Partición, Tipo de barra, Diámetro, N° barras, Longitud total, Peso unitario, Peso (kg) | Por partición (encabezado y pie con totales: `CIMIENTOS - ZAP-Z1`, `VIGAS - MAN-V1`...), luego tipo de barra; total general |
| `Metrado acero - General` | Todo el refuerzo del modelo: Elemento (tipo de anfitrión), Partición, **`ARBA - Código`** (capa o familia del add-in que armó: `inferior`, `estribo`, `F1`...) y las mismas columnas | Por elemento (encabezado y pie con totales), luego partición, luego tipo de barra; total general |

- Las tablas no están desglosadas por elemento (una fila por tipo). Si quiere ver cada elemento, active
  "Desglosar cada ejemplar" en la tabla.
- **Conexiones y anclajes**: pernos, anclajes, espárragos, tuercas, arandelas, planchas, cartelas,
  rigidizadores y soldaduras. Se reconocen **por el nombre** de la familia o del tipo en cualquier categoría
  metálica (un IFC suele traerlos como Vigas o Columnas: `ESPARRAGO 5-8`, `ANCLAJE_Ø7-8`, `PL 12mm`,
  `Perno M20`...) además de por la categoría *Rigidizadores*. Van a `Metrado acero estructural - Conexiones
  y anclajes`, tabla de varias categorías agrupada por categoría de Revit y filtrada por "Metrado -
  Elemento" = `CONEXIONES`, pesados por `volumen × densidad`; salen de la tabla de vigas o columnas y se
  pintan en verde vivo en el filtro de vista. Si el grupo no está marcado, esas piezas se quedan en su
  categoría.
- **Otros**: lo que no es viga, columna, cimentación, losa, muro ni conexión. Reúne *Conexiones
  estructurales* (donde la importación de un IFC deja, por ejemplo, las chapas curvas de un techo
  parabólico), *Modelos genéricos* y *Cubiertas*. Sus piezas metálicas van a `Metrado acero estructural -
  Otros`, tabla de varias categorías filtrada por "Metrado - Elemento" = `OTROS`, pesadas por
  `volumen × densidad`; también entran en el filtro de vista amarillo y en el Excel. Si en alguna de esas
  categorías hay elementos de concreto (una cubierta de concreto, un pedestal como modelo genérico), se
  crea `Metrado concreto - Otros - <categoría>` por cada una, porque las tablas de varias categorías de
  Revit no exponen el volumen. Sin otro dato, las conexiones estructurales se clasifican como `ACERO
  ESTRUCTURAL`; modelos genéricos y cubiertas quedan como `OTRO` y no se metran.
- **Misceláneos** (contrato ARBA-comun): todo elemento con **"Metrado - Partida"** (por ejemplo las rejillas y
  ángulos que crea el add-in de bloques con foso: `ESTRUCTURAS METÁLICAS - REJILLAS`, `… - ÁNGULOS`; o una
  partida escrita a mano) recibe `MISCELANEOS` en "Metrado - Elemento" antes que cualquier otra regla, con lo
  que sale de las tablas de Vigas / Conexiones / Otros y va a `Metrado acero estructural - Misceláneos`,
  agrupada por partida y con la suma de kg y de pernos (`Metrado - Pernos (und)`). Si su add-in ya escribió
  `Metrado - Peso (kg)` (origen ARBA y peso > 0) el plugin **no lo recalcula**, ni en la tabla ni en el Excel,
  así el peso de rejillas y ángulos no cambia al repetir el metrado. Tiene su propio filtro de vista (lila).
  Si el grupo "Misceláneos" no está marcado, esas piezas se quedan en su categoría.
- **Parámetros del contrato ARBA-comun**: los parámetros que escribe el plugin son **parámetros compartidos
  de ejemplar con GUID fijo**, definidos en [`external/ARBA-comun/CONTRATO.md`](external/ARBA-comun/CONTRATO.md)
  y creados desde un archivo temporal (el archivo de parámetros compartidos del usuario no cambia). El metrado
  automático asegura los ocho (`ARBA - Origen`, `ARBA - Código`, `ARBA - Anfitrión`, `Metrado - Partida`,
  `Metrado - Material`, `Metrado - Peso (kg)`, `Metrado - Pernos (und)`, `Metrado - Elemento`) con las categorías
  que fija el contrato; aparecen en Gestionar > Parámetros de proyecto, grupo Datos. Los tres que ya creaba el
  plugin conservan su GUID, así que los modelos ya metrados no necesitan nada. Si el proyecto tenía un parámetro
  **homónimo manual** (de proyecto no compartido, o compartido con otro GUID), se sustituye por el del contrato
  copiando los valores de todos los ejemplares y el resumen lo avisa. `Metrado - Material` no se sobrescribe en
  los elementos creados por un add-in ARBA (ya lo escribió él).
- **"Metrado - Elemento"**: parámetro de texto que el plugin escribe en cada elemento metrado con su grupo
  (`VIGAS`, `COLUMNAS`, `CIMIENTOS`, `LOSAS`, `MUROS`, `CONEXIONES`, `OTROS`, `MISCELANEOS`) y en cada armadura
  con el grupo de su anfitrión. Toda tabla de elementos filtra por él, igual que las tablas de acero por
  elemento y los filtros de vista de acero estructural y de refuerzo. Los add-ins ARBA lo prerrellenan al
  armar con la categoría de la partición; el plugin lo confirma.
- **Separación concreto / metálico**: el plugin vincula el parámetro compartido **"Metrado - Material"**
  (texto, de ejemplar) a las categorías del contrato, y lo rellena con `CONCRETO`,
  `ACERO ESTRUCTURAL`, `MADERA` u `OTRO`. Para clasificar usa, en este orden: el "Material para
  comportamiento del modelo" de la familia; los materiales asignados al elemento o a su tipo (por su clase
  o nombre, por una designación de norma en el nombre, como `A36`, `A572`, `A992`, `S275`, `S355JR`,
  `Gr 50`, que es como llega el material de los modelos exportados de Tekla, o por su activo físico de
  clase Metal); el nombre de la familia o tipo (perfiles HSS, W, C, L, IPE... y, en vigas y columnas, piezas de conexión: espárragos,
  anclajes, pernos, planchas); y el material por defecto de la categoría. Sin ningún dato útil, losas,
  muros y cimentaciones se asumen de concreto; conexiones y rigidizadores, de acero; vigas, columnas,
  modelos genéricos y cubiertas quedan como `OTRO`. Los materiales
  genéricos que crea la importación de IFC ("Material IFC (155-155-155)") y los "Por defecto" no cuentan
  como dato: un perfil importado se reconoce por su nombre (`W12X26`, `HSS2-1-2X2-1-2X3-16`, `L3X3X3-8`,
  `ESPARRAGO 5-8`, `ANCLAJE_Ø7-8`...). Las tablas de concreto filtran `= CONCRETO` y las de acero
  estructural `≠ CONCRETO`. Si un elemento quedó mal clasificado, corrija el valor del parámetro en sus
  propiedades y marque "Conservar la clasificación ya escrita" en la siguiente ejecución.
- **Respaldo del filtro**: si el parámetro "Metrado - Material" no se puede crear en el proyecto o la tabla
  no admite filtrarlo, las tablas se filtran por el material estructural: las de concreto muestran los
  elementos cuyo material contiene el texto indicado en la ventana (`Concreto` por defecto) y las de acero
  estructural, los que no lo contienen. Si ese texto no distingue los materiales del modelo, se excluyen
  los materiales uno a uno. En condiciones normales el respaldo no interviene.
- **Filtros de vista para verificar** (opción "Crear filtros de vista por colores"): el plugin crea en el
  proyecto filtros de Visibilidad/Gráficos, uno por tipo de elemento, y los aplica a la vista activa con
  color de línea y relleno sólido: `Metrado - Concreto - Vigas / Columnas / Losas / Cimentaciones / Muros`
  (regla: categoría y `Metrado - Material = CONCRETO`), `Metrado - Acero estructural - Vigas / Columnas /
  Conexiones y anclajes / Otros` (`= ACERO ESTRUCTURAL` y "Metrado - Elemento" = su grupo),
  `Metrado - Acero estructural - Misceláneos` ("Metrado - Elemento" = `MISCELANEOS`, lila) y
  `Metrado - Refuerzo - VIGAS / COLUMNAS / CIMIENTOS / LOSAS / MUROS` (armaduras y
  mallas por el parámetro **"Metrado - Elemento"**, que el plugin escribe en cada refuerzo con el tipo de su
  anfitrión real; así los filtros no dependen de cómo tenga numeradas las particiones; si ese parámetro no
  existiera, por partición "empieza por `VIGAS - `", la forma del contrato). Cada familia usa
  colores distintos (azules/rojos/verdes el concreto, celeste y magenta los perfiles, naranjas y turquesas
  el refuerzo), así se ve exactamente qué se está metrando y en qué tabla cae. Los filtros quedan en el
  proyecto: en Visibilidad/Gráficos (VV) → Filtros de cualquier vista, quitar la marca **Visibilidad** oculta
  esos elementos (por ejemplo, para ver solo el refuerzo de columnas), quitar **Habilitar filtro** deja de
  pintarlos y **Eliminar** los saca de la vista. Para ver el refuerzo coloreado en 3D la vista debe estar en
  Sombreado o Colores coherentes y las barras con "Ver como sólido"; si no, se ven como líneas de color. Si
  la vista activa no los admite (una tabla, como la que deja abierta el metrado anterior; un plano; una
  vista cuya plantilla controla los filtros), se aplican a otra vista gráfica abierta (las 3D primero) o a la
  3D predeterminada `{3D}`, y el resumen dice a cuál; solo si no hay ninguna se crean sin aplicar. Si ya
  existen se actualizan, no se duplican; si se borraron, se crean de nuevo.
- **Modelos compartidos (worksharing)**: antes de escribir, el plugin reserva los subproyectos que va a
  modificar (los estándar de parámetros compartidos, el de la vista de los filtros y los de los elementos y
  refuerzos metrados), para que Revit no muestre el aviso "You are trying to checkout a large number of
  elements". Si un subproyecto lo tiene otro usuario se avisa y Revit reserva los elementos uno a uno.
  Los subproyectos quedan reservados hasta sincronizar con central. Se puede desactivar en la ventana.
- **Partición del acero** (contrato ARBA): antes de crear las tablas, el plugin escribe en la partición vacía
  de cada armadura **que no creó un add-in ARBA** la forma del contrato `CATEGORIA - MAN-marca`: la categoría
  de su anfitrión (`VIGAS`, `COLUMNAS`, `CIMIENTOS`, `LOSAS`, `MUROS`, u `OTROS`), el prefijo `MAN` (manual) y
  la marca del anfitrión (o su Id si no tiene): `VIGAS - MAN-V1`, `CIMIENTOS - MAN-Z3`; sin anfitrión,
  `OTROS - MAN-<id>`. Además escribe `ARBA - Origen = MANUAL` y `Metrado - Elemento`. Las particiones que ya
  tienen texto se respetan salvo que marque "Sobrescribir". Las armaduras **creadas por los add-ins ARBA**
  (partición `CIMIENTOS - ZAP-Z1`, `VIGAS - VIG-V1`..., las antiguas `ZAP-Z1`, `CC-C1`, `BLQ-FT-01-F1`, o con
  `ARBA - Origen` distinto de MANUAL) **no se tocan nunca**, ni con "Sobrescribir": se cuentan como
  "respetadas" en el resumen. Las tablas de acero por elemento **no** filtran por la partición sino por
  **"Metrado - Elemento"** (tipo de anfitrión real, escrito siempre), así que funcionan aunque las particiones
  lleven textos propios como `Muro de contención` o `Bloque A`; la partición se muestra como columna y solo
  sirve de respaldo del filtro ("empieza por `VIGAS - `") si el parámetro no se pudo crear.
- **Peso del acero**: Revit no permite crear valores calculados desde la API, así que el plugin vincula el
  parámetro compartido **"Metrado - Peso (kg)"** del contrato a las armaduras, vigas, columnas, conexiones y
  "Otros" y lo rellena en cada ejecución. En las armaduras vale `Longitud total × peso por metro` (el peso por
  metro sale del parámetro del tipo de barra, por defecto `Bar Mass per Unit Length`, o de π·d²/4 × densidad si
  no existe). Las tablas muestran esa columna con totales. Si modifica armaduras después, vuelva a ejecutar el
  metrado para actualizar los pesos. El nombre del parámetro de peso por metro y las densidades se configuran
  en el grupo **Cálculo del peso** de la ventana y se aplican siempre, se exporte o no a Excel. Las piezas
  cuyo peso ya escribió su add-in ARBA (rejillas, ángulos: `ARBA - Origen` relleno y peso > 0) se respetan y se
  cuentan en el resumen; las armaduras siempre se recalculan, también las que armó un add-in ARBA.
- **Peso de los perfiles metálicos**: las vigas y columnas clasificadas como `ACERO ESTRUCTURAL` no se
  metran por volumen sino por peso: `Longitud × área de sección × densidad`. La longitud es la **de corte**
  (`Cut Length`: la pieza real, descontados los recortes en los empalmes) en vigas y arriostres, y la
  longitud del elemento en columnas. El área de sección se lee del parámetro **Área de sección** del tipo
  (perfiles con sección estructural: W, HSS, IPE, C, L...), de la definición de sección estructural de la
  familia o de un parámetro de área con nombre habitual; como último recurso se usa `Volumen / longitud`.
  La densidad es la del **acero al carbono**, material de los perfiles estructurales: 7850 kg/m³ por
  defecto, ajustable en la ventana. Los perfiles de las familias de acero de Revit (W, HSS, C...) que ya
  traen su peso (**Exact Weight** / Peso exacto o, si es 0, **Weight**, grupo Estructural) no se calculan: se
  usa ese peso. Las piezas sin longitud ni sección (conexiones, planchas, coberturas
  metálicas, o un perfil que no exponga su longitud) se pesan por `Volumen × densidad`, con el volumen del
  parámetro o, si la categoría no lo expone, el de sus sólidos. El resultado se escribe en
  "Metrado - Peso (kg)" de cada pieza y la tabla `Metrado acero estructural - <elemento>` lo suma.
- Si ya existe una tabla con el mismo nombre se reutiliza tal cual, salvo que tenga una estructura de una
  versión anterior (agrupada por nivel cuando ya no toca, sin el filtro por "Metrado - Material" o por
  "Metrado - Elemento", la general sin la columna `ARBA - Código`, la de misceláneos sin partida, peso o
  pernos): esa se crea de nuevo y se avisa en el resumen. La opción "Regenerar" borra y crea de nuevo todas (se pierden
  columnas añadidas a mano y su colocación en planos). Una tabla abierta como vista activa no se puede
  regenerar: ciérrela y vuelva a ejecutar el metrado.

**Exportación a Excel en la misma operación** (opcional): el libro contiene

| Hoja | Contenido |
|---|---|
| Resumen | Concreto (m³), acero (kg) y cuantía (kg/m³) por tipo de elemento, perfiles metálicos (longitud y kg por tipo de elemento), bloque **Misceláneos por partida** (kg, pernos, n.º de piezas y cuántas con peso de su add-in) y acero total por diámetro, calculados directamente del modelo. |
| Una hoja por tabla de Revit | El contenido de cada tabla generada (incluida `Metrado acero estructural - Misceláneos`), tal como se ve en Revit (incluidas las columnas que haya añadido a mano). |
| Concreto - Detalle / Acero estructural - Detalle / Acero - Detalle | Opcional. Una fila por elemento, perfil o conjunto de barras con Id, nivel, tipo, longitudes, área, espesor, volumen, área de sección, densidad y peso. El detalle de acero estructural añade Partida, Código (ARBA), Pernos y Origen (ARBA). |

**Cómo calcula el resumen** (independiente de las tablas, leyendo el modelo):

- Qué elementos entran: los mismos que en las tablas de Revit, es decir, los clasificados como `CONCRETO`
  en "Metrado - Material" (los `ACERO ESTRUCTURAL` van al metrado por peso; `MADERA` y `OTRO` se omiten
  con "Solo material de concreto" marcado).
- Volumen de concreto: suma del volumen de cada material del elemento que sea de concreto (clase o nombre con
  "concreto", "hormigón", "concrete", "f'c" / "fc 210" como palabra completa, o activo estructural de clase
  Concrete). En losas y muros compuestos excluye acabados y otras capas. Si el elemento no tiene materiales
  de concreto asignados, o solo genéricos ("Material IFC (r-g-b)", "Por defecto"), se usa el material
  estructural y el parámetro Volumen.
- Perfiles metálicos: vigas y columnas clasificadas como `ACERO ESTRUCTURAL`. Peso = longitud × área de
  sección × densidad del acero al carbono (7850 kg/m³ por defecto), salvo en los perfiles de Revit que ya
  traen su peso (`Exact Weight` / `Weight`), donde se usa ese. No entran en el volumen de concreto.
- Nivel: nivel de referencia (vigas), nivel base (columnas, muros) o el nivel del elemento. Vigas, losas y
  cimentaciones no se agrupan por nivel (ni en Revit ni en las hojas calculadas del Excel), solo por tipo;
  columnas y muros sí.
- Acero: barras (`Rebar`), refuerzo por área y trayectoria (`RebarInSystem`) y mallas electrosoldadas
  (`FabricSheet`) cuyo anfitrión pertenece a las categorías marcadas. La longitud se toma del parámetro
  **Longitud total de barra** (`Total Bar Length`), que suma todas las piezas del conjunto con sus ganchos y
  dobleces, y no de **Longitud de barra** (`Bar Length`), que es la de una sola pieza. Si falta, se calcula
  la longitud geométrica del eje de cada posición de barra y, como último recurso, longitud de barra ×
  cantidad.
- Peso: `Longitud total × peso por metro`. El peso por metro se lee del parámetro del tipo de barra indicado
  en la ventana (`Bar Mass per Unit Length` por defecto; se respetan sus unidades si es de disciplina
  "masa por unidad de longitud"). Si el tipo no tiene ese parámetro se calcula como π·d²/4 × densidad
  (7850 kg/m³ por defecto). Las mallas usan la masa de hoja cortada que calcula Revit.

## Parámetros y filtros, y tablas propias (tercer botón)

Para quien quiere tablas distintas de las predeterminadas: el botón **Parámetros y filtros** separa en dos
pasos lo que el metrado automático hace de una vez, sin tocar nada de ese otro botón.

**Pestaña 1 · Parámetros y filtros (sin tablas)**. Escribe en el modelo exactamente los mismos parámetros
que el metrado automático —`Metrado - Material` (CONCRETO / ACERO ESTRUCTURAL / MADERA / OTRO),
`Metrado - Elemento` (VIGAS, COLUMNAS, CIMIENTOS, LOSAS, MUROS, CONEXIONES, OTROS, MISCELANEOS; en el
refuerzo, el grupo de su anfitrión), `Metrado - Peso (kg)` de armaduras y perfiles, la partición
`CATEGORIA - MAN-marca` y `ARBA - Origen = MANUAL` del refuerzo sin origen ARBA— y crea y aplica los mismos
filtros de vista por colores, pero **no crea ninguna tabla**. Opciones propias de este paso:

- **Conservar `Metrado - Material` ya escrito** y **conservar `Metrado - Elemento` ya escrito** (marcadas por
  defecto): solo se rellenan los elementos y armaduras que tengan el parámetro vacío, así los textos que usted
  haya escrito a mano se respetan al repetir el paso (por ejemplo tras modelar elementos nuevos). Sin marcar,
  se vuelve a clasificar todo, igual que hace el metrado automático.
- Incluir o no el acero de refuerzo; rellenar o sobrescribir particiones; crear los filtros de vista; reservar
  subproyectos en modelos compartidos; parámetro de peso por metro y densidades.

Después, en Revit, cambie a mano `Metrado - Material` y/o `Metrado - Elemento` en los elementos o armaduras
que quiera separar en una tabla propia (desde sus propiedades, o en bloque desde una tabla con esas columnas):
por ejemplo `Metrado - Elemento = ESCALERAS` en los suelos y vigas de una escalera, `MUROS DE CONTENCION` en
ciertos muros, o `Metrado - Material = CONCRETO F'C 280` en las columnas de un f'c distinto.

**Pestaña 2 · Tablas desde los parámetros**. Lee todos los valores que tienen `Metrado - Material` y
`Metrado - Elemento` en los elementos de las categorías del contrato y en el refuerzo, y los muestra como
**combinaciones** (material + elemento en los elementos; solo elemento en el refuerzo) con sus categorías de
Revit y cuántos elementos tienen cada una. Las combinaciones que ya cubren las tablas predeterminadas aparecen
como "Predeterminada" y sin marcar; las que llevan un texto suyo, como "Propia" y marcadas (botones **Todas**,
**Ninguna**, **Solo propias**). Al pulsar **Crear tablas** se crea una tabla por cada combinación marcada,
filtrada por esos valores **exactos** (un valor vacío se filtra como "sin valor") y **sin reescribir ningún
parámetro**:

| Combinación | Tabla | Contenido |
|---|---|---|
| Elementos, material que habla de concreto (`CONCRETO`, `CONCRETO F'C 280`, `HORMIGÓN`...) | `Metrado concreto f'c 280 - ESCALERAS` (`Metrado <material en minúsculas> - <elemento>`) | Elemento (familia y tipo), Material, Cantidad, Longitud, Área, Espesor, Volumen; por tipo (columnas y muros, además por nivel); total general |
| Elementos, cualquier otro material | `Metrado acero estructural - BARANDAS` | Elemento, Material, Cantidad, Longitud, Área de sección, Volumen, **Peso (kg)**; igual |
| Refuerzo | `Metrado acero - ESCALERAS` | Partición (encabezado y pie con totales), Tipo de barra, Diámetro, N° barras, Longitud total, Peso unitario, Peso (kg); total general |

Si una combinación de elementos abarca varias categorías de Revit (una escalera con suelos y vigas) se crea una
tabla **por categoría**, `Metrado concreto - ESCALERAS - Suelos` y `... - Armazón estructural`, porque las tablas
de varias categorías de Revit no exponen volumen ni longitud. Las tablas existentes con el mismo nombre se
reutilizan salvo que marque "Regenerar". Estas tablas se exportan con el botón **Exportar a Excel** como
cualquier otra.

Tenga en cuenta que el **Metrado automático** siempre recalcula `Metrado - Elemento` (y `Metrado - Material`
salvo que marque "Conservar la clasificación"), así que al ejecutarlo los textos propios vuelven al grupo
estándar; para mantenerlos, use la pestaña 1 de este botón con las opciones "Conservar" marcadas.

## Asignar partición (cuarto botón)

Escribe el parámetro **Partición** del acero de refuerzo que no creó ningún add-in ARBA, sin pasar por el metrado:

- **A qué**: la selección actual (anfitriones y/o armaduras), elementos elegidos en pantalla, o todo el modelo.
  Si selecciona una viga, se asigna a todas las armaduras alojadas en ella.
- **Qué texto**: automático según el contrato ARBA, `CATEGORIA - MAN-marca` (categoría y marca, o Id, del
  anfitrión: `VIGAS - MAN-V1`, `CIMIENTOS - MAN-Z3`; sin anfitrión, `OTROS - MAN-<id>`), escribiendo además
  `ARBA - Origen = MANUAL` y `Metrado - Elemento`; o un texto propio, por ejemplo `VIGA V-101` o
  `BLOQUE A - COLUMNAS` (solo la partición).
- Opción para sobrescribir o respetar las particiones que ya tengan texto. Las armaduras creadas por los
  add-ins ARBA (prefijos `ZAP`, `CCO`, `BLQ`, `VIG`, `COL`, `LOS`, `MCO`, con o sin categoría delante, u origen
  distinto de MANUAL) **no se tocan nunca** y el resumen las cuenta como respetadas. Las `MAN` / MANUAL son
  del propio plugin y sí se reescriben con "Sobrescribir".

Las tablas de acero y la general se agrupan por este parámetro, así que basta con mantenerlo al día. Los
parámetros del contrato se crean si faltan.

## Migrar particiones y origen (quinto botón)

Para modelos armados con versiones de los add-ins ARBA anteriores al contrato. Lo aporta el código común
(`ArbaMigrateCommandBase`) y migra **sin rearmar**:

- Sin selección, todo el modelo; con selección, los anfitriones elegidos (o los de las armaduras elegidas).
- Convierte las particiones antiguas a la forma del contrato con la categoría del **anfitrión real**:
  `ZAP-Z1` → `CIMIENTOS - ZAP-Z1`, `CC-C1` → `MUROS - CCO-C1` (o `CIMIENTOS - CCO-C1`), `BLQ-FT-01-F1` →
  `CIMIENTOS - BLQ-FT-01-F1`, `LOSA-L1` → `LOSAS - LOS-L1`, `MC-M1` → `MUROS - MCO-M1`.
- Rellena `ARBA - Origen` (ZAPATAS, CIMIENTOS CORRIDOS, BLOQUES, LOSAS, MUROS DE CONTENCION...), `ARBA - Código`
  (si la partición lo llevaba: `F1`, `inferior`...) y `Metrado - Elemento`.
- No toca las particiones de solo categoría (`VIGAS`) ni las desconocidas; no crea ni borra barras; crea los
  parámetros del contrato si faltan; el número de conjuntos no cambia. Ctrl+Z lo deshace.
- Muestra un resumen por add-in (revisadas, migradas, ya conformes, sin tocar).

Después de migrar, las tablas `Metrado acero - Vigas / Columnas / Cimentaciones / Losas` agrupan por la nueva
partición (`CIMIENTOS - ZAP-Z1`...) y `Metrado acero - General` muestra la columna `ARBA - Código`.

## Metrado de encofrado (panel Encofrado)

El botón **Metrado de encofrado** calcula el encofrado en m² de los elementos de concreto **a partir de su
geometría real y de su contexto**, no con fórmulas por tipo de elemento. Para cada elemento clasificado como
`CONCRETO` (por `Metrado - Material` o, si no está escrito, por la misma clasificación que usa el metrado
automático):

1. **Clasifica cada cara** de su sólido por la dirección de su normal: **lateral** (vertical o inclinada más de
   45°), **fondo** (mira hacia abajo) o **superior** (mira hacia arriba). Las caras superiores no se encofran
   nunca: son la superficie libre o el apoyo de otro elemento.
2. **Aplica la regla del elemento** (editable en la ventana):

   | Elemento | Caras que cuentan | Qué se descuenta por contexto |
   |---|---|---|
   | Vigas | costados + fondo (+ testeros libres de volados) | lo que entra en columnas o muros, la franja del espesor de la losa que apoya en sus costados, la sección de las vigas secundarias que llegan |
   | Columnas | solo caras laterales | la sección de las vigas que llegan, la franja del espesor de la losa que las atraviesa; la cara superior y la base no se encofran |
   | Cimentaciones | solo bordes (laterales) | el contacto con zapatas o cimientos vecinos; el fondo apoya en el terreno y la cara superior queda libre |
   | Losas | fondo (sofito) + bordes libres y de vanos | el ancho de las vigas y muros bajo el sofito, el contacto de los bordes con vigas, muros y columnas |
   | Muros | las dos caras y los extremos libres | las losas y vigas que entran, las columnas embebidas, los muros que se le unen; coronación y base no se encofran |

   El **fondo de las losas** tiene su propia opción: contarlo siempre, nunca, o (por defecto) salvo en las losas
   apoyadas en el **nivel más bajo** del proyecto, que se consideran sobre terreno.
3. **Descuenta las superficies en contacto con otros elementos de concreto** (el contexto). Sobre cada cara
   plana que cuenta levanta un prisma fino (de −tolerancia a +tolerancia, 10 mm por defecto) y lo intersecta
   con cada elemento de concreto cercano, esté o no unido en Revit: el área de las caras del resultado paralelas
   a la cara original es exactamente la superficie en contacto (la sección de la viga que entra en la columna,
   la franja de losa apoyada en la viga, el ancho de la viga bajo la losa...). Funciona igual si los elementos
   se solapan, se tocan o quedan a una holgura menor que la tolerancia; una separación mayor (una junta de
   dilatación) deja ambas caras libres y las dos se encofran. Si varios vecinos tocan la misma cara, sus
   contactos se unen para no descontar dos veces la misma zona. Las caras **curvas** (columnas circulares) se
   muestrean punto a punto cada 2,5 cm con el mismo criterio y se marcan como aproximadas. Como contexto cuentan
   **todos** los elementos de concreto del modelo (también modelos genéricos, conexiones u "Otros" clasificados
   como concreto), aunque su grupo no esté marcado; los elementos de archivos vinculados no se consideran.

**Resultados:**

- Parámetro compartido de ejemplar **`Metrado - Encofrado (m²)`** en cada elemento metrado (se crea si falta, en
  las mismas categorías que `Metrado - Material`; GUID fijo propio del plugin, ver `NOTAS-ARBA-COMUN.md`).
- Tablas **`Metrado encofrado - Vigas / Columnas / Cimentaciones / Losas / Muros`**: Elemento, Material, Cantidad y
  Encofrado (m²) con total, filtradas por `Metrado - Material = CONCRETO` y `Metrado - Elemento` = grupo (ejecute
  antes *Metrado automático* o *Parámetros y filtros* para que esos parámetros estén escritos); columnas y muros
  además por nivel. Y **`Metrado encofrado - General`**: todos los elementos con encofrado calculado, de varias
  categorías, agrupados por `Metrado - Elemento`. Se exportan con *Exportar a Excel* como cualquier otra.
- Excel (opcional): hoja **Resumen** (por elemento, por elemento y tipo, por elemento y nivel, con laterales,
  fondos, descuento y neto), una hoja por tabla de Revit creada, **Encofrado - Detalle** (una fila por elemento:
  caras brutas, descuentos, neto, caras superiores no encofradas, fondo no contado, observaciones) y
  **Contactos** (qué superficie se descontó de cada elemento y con qué elemento vecino), para comprobar cada
  descuento.

Notas: el cálculo es geométrico y puede tardar algunos minutos en modelos grandes (una intersección por cara y
vecino cercano). Si una geometría no admite la operación exacta se recurre al muestreo y el elemento queda marcado
como aproximado en el detalle. Los resultados dependen de cómo esté modelado: una viga que no llega a la columna
(holgura mayor que la tolerancia) no descuenta nada, y una losa modelada sobre la viga (sin unir) descuenta igual
que una unida.

## Notas técnicas

- El comando de exportación se declara con `TransactionMode.ReadOnly`: no modifica el modelo. El metrado
  automático usa una transacción propia ("Metrado automático") solo para crear las tablas; se puede deshacer
  con Ctrl+Z.
- El resumen del metrado y el pie de la ventana muestran la versión del contrato ARBA-comun con la que se
  compiló el plugin (`ArbaContract.Version`), más los contadores del contrato: pesos respetados, particiones
  ARBA respetadas y misceláneos.
- Lo que el contrato no cubre (o conviene cambiar en él) está en `NOTAS-ARBA-COMUN.md`; el cambio se hace en
  ARBA-comun con su versión, nunca dentro de `external/ARBA-comun`.
- Las filas completamente en blanco (separadores entre grupos) se omiten.
- Si una tabla tiene desactivada la opción "Mostrar encabezados", se usan los encabezados de columna
  definidos en los campos de la tabla.
- Conversión numérica: se aceptan separadores de miles y decimales en formato español (`1.234,56`) e
  inglés (`1,234.56`). Un valor con un único separador seguido de tres dígitos (`1.250`) es ambiguo y se
  interpreta según la configuración regional de Windows.
- Los textos que empiezan por `=` se escriben como texto, nunca como fórmula.
- El archivo se escribe primero como `.tmp` y luego se renombra, para no dejar un `.xlsx` dañado si el
  destino está abierto en Excel.

## Licencia

MIT.
