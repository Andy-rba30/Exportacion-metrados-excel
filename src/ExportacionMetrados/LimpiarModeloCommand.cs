using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ExportacionMetrados.Core.Metrado;
using ExportacionMetrados.UI;

namespace ExportacionMetrados
{
    /// <summary>
    /// Botón "Limpiar modelo": quita del proyecto, a elección, lo que deja el plugin (tablas, filtros de
    /// vista, valores de los parámetros y los propios parámetros). Toda la lógica está en
    /// <see cref="LimpiadorModelo"/>; aquí solo la ventana, la transacción y el resumen.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LimpiarModeloCommand : IExternalCommand
    {
        private const string Titulo = "Limpiar modelo";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show(Titulo, "Abra un proyecto de Revit antes de ejecutar el comando.");
                return Result.Cancelled;
            }

            Document doc = uidoc.Document;

            try
            {
                var ventana = new LimpiarModeloWindow();
                _ = new System.Windows.Interop.WindowInteropHelper(ventana)
                {
                    Owner = commandData.Application.MainWindowHandle
                };
                if (ventana.ShowDialog() != true) return Result.Cancelled;

                var limpiador = new LimpiadorModelo(doc, uidoc.ActiveView?.Id);
                using (var t = new Transaction(doc, Titulo))
                {
                    t.Start();
                    limpiador.Limpiar(ventana.Opciones);
                    t.Commit();
                }

                OpcionesLimpieza op = ventana.Opciones;
                string contenido =
                    (op.EliminarTablas ? $"Tablas eliminadas: {limpiador.TablasEliminadas.Count}\n" : string.Empty) +
                    (op.EliminarFiltros ? $"Filtros de vista eliminados: {limpiador.FiltrosEliminados.Count}\n" : string.Empty) +
                    (op.LimpiarValores
                        ? $"Elementos con parámetros vaciados: {limpiador.ElementosLimpiados}\n" +
                          $"Refuerzos con parámetros vaciados: {limpiador.RefuerzosLimpiados}\n" +
                          $"Elementos y refuerzos de add-ins ARBA respetados: {limpiador.RespetadosArba}\n"
                        : string.Empty) +
                    (op.BorrarParametros ? $"Parámetros quitados del proyecto: {limpiador.ParametrosBorrados.Count}\n" : string.Empty) +
                    "\nSe puede deshacer con Ctrl+Z.";

                var dialogo = new TaskDialog(Titulo)
                {
                    MainInstruction = limpiador.Advertencias.Count == 0 ? "Modelo limpiado" : "Modelo limpiado con advertencias",
                    MainContent = contenido,
                    CommonButtons = TaskDialogCommonButtons.Close,
                };

                var detalle = new List<string>();
                Agregar(detalle, "Tablas:", limpiador.TablasEliminadas);
                Agregar(detalle, "Filtros:", limpiador.FiltrosEliminados);
                Agregar(detalle, "Parámetros:", limpiador.ParametrosBorrados);
                Agregar(detalle, "Advertencias:", limpiador.Advertencias);
                if (detalle.Count > 0) dialogo.ExpandedContent = string.Join("\n", detalle);
                dialogo.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Titulo, "Ocurrió un error:\n\n" + ex.Message);
                return Result.Failed;
            }
        }

        private static void Agregar(List<string> detalle, string encabezado, List<string> lineas)
        {
            if (lineas.Count == 0) return;
            if (detalle.Count > 0) detalle.Add("");
            detalle.Add(encabezado);
            detalle.AddRange(lineas.Select(l => "  • " + l));
        }
    }
}
