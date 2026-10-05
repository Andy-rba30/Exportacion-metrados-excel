using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ExportacionMetrados.Core;
using ExportacionMetrados.UI;

namespace ExportacionMetrados
{
    /// <summary>
    /// Comando que muestra la ventana de selección de tablas y genera el Excel. No modifica el modelo:
    /// las tablas se leen dentro de una transacción que se deshace al terminar, porque leer las celdas de
    /// una tabla desactualizada (p. ej. una de varias categorías recién creada) obliga a Revit a
    /// regenerarla y en modo solo lectura eso fallaba con "Changes are disabled for the active document".
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExportarMetradosCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("Exportación de Metrados", "Abra un proyecto de Revit antes de ejecutar el comando.");
                return Result.Cancelled;
            }

            Document doc = uidoc.Document;

            try
            {
                List<ViewSchedule> tablas = LectorTablas.ObtenerTablasExportables(doc);
                if (tablas.Count == 0)
                {
                    TaskDialog.Show("Exportación de Metrados",
                        "El proyecto no contiene tablas de planificación exportables.");
                    return Result.Cancelled;
                }

                // Si la vista activa es una tabla, se preselecciona.
                var activaId = uidoc.ActiveView is ViewSchedule vs ? vs.Id : ElementId.InvalidElementId;

                var ventana = new SeleccionTablasWindow(tablas, activaId, SugerirNombreArchivo(doc));
                _ = new System.Windows.Interop.WindowInteropHelper(ventana)
                {
                    Owner = commandData.Application.MainWindowHandle
                };

                if (ventana.ShowDialog() != true)
                {
                    return Result.Cancelled;
                }

                var opciones = ventana.Opciones;
                var seleccionadas = ventana.TablasSeleccionadas;
                if (seleccionadas.Count == 0)
                {
                    return Result.Cancelled;
                }

                var exportador = new ExportadorExcel(opciones);
                ResultadoExportacion resultado;
                using (var t = new Transaction(doc, "Exportar tablas a Excel"))
                {
                    t.Start();
                    foreach (ViewSchedule tabla in seleccionadas)
                    {
                        try { tabla.RefreshData(); }
                        catch (Exception) { /* se lee tal cual está */ }
                    }
                    try
                    {
                        resultado = exportador.Exportar(seleccionadas, opciones.RutaArchivo);
                    }
                    finally
                    {
                        // Nada que conservar: solo se leyó.
                        t.RollBack();
                    }
                }

                MostrarResumen(resultado, opciones);
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                // El error se muestra aquí; no se devuelve en "message" para que Revit
                // no lo repita en su diálogo "Error - cannot be ignored".
                TaskDialog.Show("Exportación de Metrados",
                    "Ocurrió un error durante la exportación:\n\n" + ex.Message);
                return Result.Failed;
            }
        }

        private static string SugerirNombreArchivo(Document doc)
        {
            string nombre = string.IsNullOrWhiteSpace(doc.Title) ? "Metrados" : doc.Title;
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                nombre = nombre.Replace(c, '_');
            }
            return nombre + " - Metrados.xlsx";
        }

        private static void MostrarResumen(ResultadoExportacion resultado, OpcionesExportacion opciones)
        {
            var dialogo = new TaskDialog("Exportación de Metrados")
            {
                MainInstruction = resultado.Errores.Count == 0
                    ? "Exportación completada"
                    : "Exportación completada con advertencias",
                MainContent =
                    $"Tablas exportadas: {resultado.TablasExportadas}\n" +
                    $"Filas escritas: {resultado.FilasEscritas}\n\n" +
                    $"Archivo:\n{opciones.RutaArchivo}",
                CommonButtons = TaskDialogCommonButtons.Close,
            };

            if (resultado.Errores.Count > 0)
            {
                dialogo.ExpandedContent = string.Join("\n", resultado.Errores);
            }

            if (opciones.AbrirAlTerminar)
            {
                dialogo.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Abrir el archivo");
            }

            TaskDialogResult r = dialogo.Show();
            if (r == TaskDialogResult.CommandLink1)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(opciones.RutaArchivo)
                    {
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Exportación de Metrados", "No se pudo abrir el archivo:\n" + ex.Message);
                }
            }
        }
    }
}
