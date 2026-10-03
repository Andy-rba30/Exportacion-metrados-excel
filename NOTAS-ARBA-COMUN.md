# Notas para ARBA-comun (integración en Exportacion-metrados-excel)

Lo que el contrato / código común `v1.0.0` no cubre o conviene revisar, encontrado al integrar este plugin. Nada de
esto se ha cambiado en `external/ARBA-comun`: el cambio, si procede, va en ARBA-comun con su versión.

## 1. `ArbaMigrateCommandBase` es `internal`: un comando público de Revit no puede heredar de ella

`INTEGRACION.md` §7 y el comentario de la clase proponen `public class MigrarCommand : ArbaMigrateCommandBase { }`,
pero C# no permite que una clase pública derive de una clase `internal` (CS0060), y Revit exige que la clase del
comando (`IExternalCommand`) sea pública. El plugin lo resuelve con un envoltorio:

```csharp
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class MigrarParticionesCommand : IExternalCommand
{
    private sealed class Migrador : ArbaMigrateCommandBase { }
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e) => new Migrador().Execute(c, ref m, e);
}
```

Propuesta (PATCH, solo documentación): poner este patrón en `INTEGRACION.md` §7 y en el comentario de la clase. O bien
(MINOR) exponer un `public static Result ArbaMigrateCommandBase.Run(ExternalCommandData, string title, ArbaPrefix only)`.

## 2. `ArbaMetrado.PesoProtegido` aplicado a armaduras congelaría su peso

El contrato dice: *"El plugin no sobrescribe un valor > 0 si `ARBA - Origen` no está vacío"*. En las armaduras el
único que escribe `Metrado - Peso (kg)` es este plugin (los add-ins de armado escriben origen, código y elemento, no
peso). Si el plugin respetara su propio peso anterior, tras el primer metrado las barras de ZAPATAS, VIGAS, etc. (y las
MAN) no se actualizarían nunca al modificarlas. Por eso el plugin aplica la regla solo a lo que **no** es armadura
(`ClasificadorElementos.PesoProtegido = !ArbaPartition.IsRebar(e) && ArbaMetrado.PesoProtegido(e)`): rejillas, ángulos
y cualquier `FamilyInstance` con origen ARBA y peso > 0.

Propuesta: aclararlo en `CONTRATO.md` §1 (fila *Metrado - Peso (kg)*) y, si se quiere, que `ArbaMetrado.PesoProtegido`
devuelva `false` para armaduras.

## 3. Particiones `MAN` / origen `MANUAL`: el plugin sí las reescribe con "Sobrescribir"

El prompt de integración dice que toda partición con `info.IsArba` se respeta "ni con Sobrescribir". Como `MAN` es un
prefijo del contrato, al pie de la letra ninguna armadura podría volver a cambiar de partición después del primer
"Asignar partición" automático, y el texto propio dejaría de tener efecto. El plugin considera **suyas** las particiones
`MAN` y el origen `MANUAL`: se respetan si "Sobrescribir" no está marcado y se reescriben si lo está. Las de los demás
prefijos (ZAP, CCO, BLQ, VIG, COL, LOS, MCO, MUR), las antiguas (`ZAP-Z1`, `CC-C1`...) y cualquier origen distinto de
MANUAL (incluido uno que este contrato no conozca) no se tocan nunca. Ver `ClasificadorElementos.EsParticionProtegida`.

## 4. Misceláneos de concreto

`ArbaMetrado.EsMiscelaneo` es "tiene `Metrado - Partida`", sin mirar el material. Un elemento de concreto al que el
usuario escriba una partida pasa a `Metrado - Elemento = MISCELANEOS` y sale de la tabla de concreto de su categoría.
El plugin lo recoge en `Metrado concreto - Misceláneos - <categoría>` (misma solución que "Otros"), pero conviene decir
en el contrato que `Metrado - Partida` está pensada para piezas metálicas metradas por peso.

## 5. Categorías de `Metrado - Partida` / `Pernos`

`Partida` se vincula a armazón, pilares, modelos genéricos, conexiones, rigidizadores y cubiertas; `Pernos` no incluye
cubiertas. El grupo "Misceláneos" del plugin usa las categorías de `Partida`. Si en el futuro un add-in metra por partida
cimentaciones, suelos o muros, habrá que añadirlos al contrato (MINOR) y el plugin los tomará solos.

## 6. Texto de `INTEGRACION.md` §7

Nombra la clase `MigrarCommand`; el prompt `PROMPTS/01-…` y este plugin usan `MigrarParticionesCommand`. Es solo el
nombre de la clase (el botón es `ARBA_Metrados_Migrar` en ambos), pero conviene unificar el documento.

## 7. `Arba.Comun.props` repite `REVIT$(RevitVersion)` en `DefineConstants`

El csproj del plugin ya lo define; el `.props` lo vuelve a añadir (`REVIT2027;…;REVIT2027`). Es inocuo para el
compilador. Si se quiere evitar, el `.props` puede añadirlo con `Condition="!$(DefineConstants.Contains('REVIT$(RevitVersion)'))"`.

## 8. Compilar sin Revit instalado

Para comprobar la compilación en Linux / CI, el csproj del plugin toma `Nice3point.Revit.Api.RevitAPI/RevitAPIUI`
(`$(RevitVersion).*`, `ExcludeAssets="runtime"`) cuando no existe `RevitAPI.dll` en la carpeta de Revit, igual que
`build/Arba.Comun.Check.csproj`. Con esos paquetes (2024.3.60, 2026.4.10 y 2027.3.0) el `main` del plugin anterior a
esta rama no compilaba por cinco usos de API inexistentes (`FABRIC_PARAM_TOTAL_LENGTH/WIDTH`, `FABRIC_PARAM_SHEET_MASS`,
`WorksharingUtils.GetCheckoutStatus(doc, WorksetId)`, `CheckoutWorksets` devuelve `ICollection`); se corrigieron en
esta rama. No afecta a ARBA-comun; se anota para que el patrón de "API desde NuGet" pueda pasar a `INTEGRACION.md` §2.
