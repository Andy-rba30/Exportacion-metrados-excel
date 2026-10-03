using Arba.Comun;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ExportacionMetrados
{
    /// <summary>
    /// Botón "Migrar particiones y origen" (contrato ARBA-comun, INTEGRACION.md §7): convierte las
    /// particiones antiguas de los add-ins ARBA (ZAP-Z1, CC-C1, BLQ-FT-01-F1, LOSA-L1, MC-M1...) a la
    /// forma "CATEGORIA - PREFIJO-marca[-codigo]" con la categoría del anfitrión real, y rellena
    /// "ARBA - Origen", "ARBA - Código" y "Metrado - Elemento", sin crear ni borrar barras. Sin
    /// selección migra todo el modelo; con selección, los anfitriones elegidos. Ctrl+Z lo deshace.
    /// <para>
    /// Toda la lógica está en <see cref="ArbaMigrateCommandBase"/> (código común). Revit exige que la
    /// clase del comando sea pública y la base común es <c>internal</c>, así que esta clase pública
    /// delega en una subclase privada en lugar de heredar directamente (ver NOTAS-ARBA-COMUN.md).
    /// </para>
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class MigrarParticionesCommand : IExternalCommand
    {
        /// <summary>Subclase del comando base común: migra todos los prefijos del contrato.</summary>
        private sealed class Migrador : ArbaMigrateCommandBase
        {
            protected override string Title => "Migrar particiones y origen";
            protected override ArbaPrefix OnlyPrefix => null;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return new Migrador().Execute(commandData, ref message, elements);
        }
    }
}
