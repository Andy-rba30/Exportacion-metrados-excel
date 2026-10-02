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
- Botón **Metrado automático**: calcula el concreto (m³) y el acero de refuerzo (kg) de vigas, columnas y
  otros elementos estructurales leyendo directamente el modelo, sin necesitar tablas de planificación.
- Compatible con Revit 2021 a 2024 (.NET Framework 4.8) y Revit 2025+ (.NET 8).

## Estructura

```
ExportacionMetrados.sln
src/ExportacionMetrados/
├── App.cs                         Crea la pestaña "Metrados" y el botón en la cinta
├── ExportarMetradosCommand.cs     Comando 1: exporta las tablas de planificación elegidas
├── MetradoAutomaticoCommand.cs    Comando 2: metrado automático de concreto y acero
├── ExportacionMetrados.addin      Manifiesto que Revit lee para cargar el plugin
├── Core/
│   ├── LectorTablas.cs            Lee las tablas de Revit (encabezados y cuerpo)
│   ├── ExportadorExcel.cs         Escribe el .xlsx con ClosedXML
│   ├── OpcionesExportacion.cs     Opciones y resultado de la exportación
│   └── Metrado/
│       ├── CalculadorMetrado.cs   Recorre el modelo: volúmenes de concreto y barras de acero
│       ├── ExportadorMetrado.cs   Escribe las hojas Resumen, Concreto, Acero y detalle
│       └── ModelosMetrado.cs      Opciones, categorías y resultados del metrado
├── UI/
│   ├── SeleccionTablasWindow.xaml Ventana de selección de tablas
│   ├── MetradoAutomaticoWindow.xaml Ventana de opciones del metrado automático
│   └── TablaItem.cs               Modelo de cada fila de la lista
└── Resources/                     Iconos del botón
```

## Requisitos

- Windows con Autodesk Revit instalado (2021 o superior).
- [SDK de .NET](https://dotnet.microsoft.com/download) 6 o superior (para `dotnet build`), o Visual Studio 2022
  con la carga de trabajo "Desarrollo de escritorio de .NET".

## Compilación e instalación

Desde una terminal en la raíz del repositorio:

```powershell
# Revit 2024 (valor por defecto)
dotnet build -c Release

# Otra versión de Revit
dotnet build -c Release -p:RevitVersion=2023
dotnet build -c Release -p:RevitVersion=2025
```

El proyecto busca `RevitAPI.dll` y `RevitAPIUI.dll` en `C:\Program Files\Autodesk\Revit <versión>\`.
Si Revit está en otra ruta, pase la propiedad `RevitInstallDir`:

```powershell
dotnet build -c Release -p:RevitVersion=2024 -p:RevitInstallDir="D:\Autodesk\Revit 2024"
```

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
2. Vaya a la pestaña **Metrados**. Hay dos botones: **Exportar a Excel** (tablas de planificación) y
   **Metrado automático** (concreto y acero desde el modelo, ver más abajo). Pulse **Exportar a Excel**.
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

El botón **Metrado automático** no usa tablas: recorre los elementos del modelo y genera un libro con:

| Hoja | Contenido |
|---|---|
| Resumen | Concreto (m³), acero (kg) y cuantía (kg/m³) por tipo de elemento, más acero total por diámetro. |
| Concreto | Por elemento (Vigas, Columnas, ...) → nivel → tipo: cantidad, longitud/altura (m) y volumen (m³), con subtotales por elemento y total general. |
| Acero | Por elemento anfitrión → nivel → diámetro: número de barras, longitud total (m) y peso (kg), con subtotales y total. |
| Concreto - Detalle | Una fila por elemento (Id, nivel, familia, tipo, marca, material, longitud, volumen) con autofiltro. |
| Acero - Detalle | Una fila por conjunto de barras (Id, Id del anfitrión, partición, tipo, diámetro, cantidad, longitud, peso). |

Los subtotales y totales se escriben como fórmulas de Excel, así que se recalculan si edita las filas.

**Qué se mide y cómo**

- Categorías disponibles: Vigas (Structural Framing), Columnas (Structural Columns), Cimentaciones, Losas y Muros.
  Por defecto solo Vigas y Columnas están marcadas.
- Volumen de concreto: se suma el volumen de cada material del elemento (`GetMaterialVolume`) que sea de
  concreto. Un material se reconoce como concreto si su clase o nombre contiene "concreto", "hormigón",
  "concrete" o "f'c", o si su activo estructural es de clase Concrete. En losas y muros compuestos esto
  excluye acabados, aislamiento y otras capas. Si el elemento no tiene materiales asignados se usa el
  material estructural y el parámetro Volumen.
- Opción **Solo material de concreto**: omite vigas o columnas de acero estructural, madera, etc. Los
  elementos sin material se incluyen igualmente y se avisa.
- Nivel: nivel de referencia (vigas), nivel base (columnas, muros) o el nivel del elemento.
- Acero: se leen las barras (`Rebar` y `RebarInSystem`) cuyo anfitrión pertenece a las categorías marcadas.
  La longitud se toma del parámetro **Longitud total de barra** (`Total Bar Length`), que suma todas las
  piezas del conjunto con sus ganchos y dobleces, y no del parámetro **Longitud de barra** (`Bar Length`),
  que es la de una sola pieza. Si el parámetro total no está disponible se calcula la longitud geométrica
  del eje de cada posición de barra y, como último recurso, longitud de barra × cantidad. La hoja
  "Acero - Detalle" muestra ambas longitudes y el origen usado. El peso se calcula como
  `longitud × π·d²/4 × densidad` (7850 kg/m³ por defecto, editable). Si el tipo de barra tiene un parámetro
  numérico llamado "Peso unitario", "Peso por metro", "Bar Weight", "Unit Weight" o "Weight per Length"
  (en kg/m) se usa ese valor en lugar de la fórmula.
- Las mallas electrosoldadas (Fabric Sheet) y el refuerzo por área/trayectoria sin barras generadas no se
  cuentan.

## Notas técnicas

- El comando se declara con `TransactionMode.ReadOnly`: no modifica el modelo.
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
