using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ExportacionMetrados.Core.Metrado;
using ExportacionMetrados.UI;

namespace ExportacionMetrados
{
    /// <summary>
    /// Escribe el parámetro Partición del acero de refuerzo, a un elemento, a una
    /// selección o a todo el modelo, con el nombre de la categoría del anfitrión
    /// o con un texto propio.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AsignarParticionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("Asignar partición", "Abra un proyecto de Revit antes de ejecutar el comando.");
                return Result.Cancelled;
            }
            Document doc = uidoc.Document;

            try
            {
                ICollection<ElementId> seleccion = uidoc.Selection.GetElementIds();

                var ventana = new AsignarParticionWindow(seleccion.Count);
                _ = new System.Windows.Interop.WindowInteropHelper(ventana) { Owner = commandData.Application.MainWindowHandle };
                if (ventana.ShowDialog() != true) return Result.Cancelled;

                List<Element> refuerzo;
                switch (ventana.Modo)
                {
                    case ModoSeleccion.TodoElModelo:
                        refuerzo = ClasificadorElementos.TodoElRefuerzo(doc);
                        break;

                    case ModoSeleccion.ElegirEnPantalla:
                        IList<Reference> refs;
                        try
                        {
                            refs = uidoc.Selection.PickObjects(ObjectType.Element,
                                "Seleccione vigas, columnas, losas, cimientos o armaduras y pulse Finalizar");
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            return Result.Cancelled;
                        }
                        refuerzo = ClasificadorElementos.RefuerzoDeSeleccion(doc, refs.Select(r => r.ElementId).ToList());
                        break;

                    default:
                        refuerzo = ClasificadorElementos.RefuerzoDeSeleccion(doc, seleccion);
                        break;
                }

                if (refuerzo.Count == 0)
                {
                    TaskDialog.Show("Asignar partición", "No se encontró acero de refuerzo en los elementos indicados.");
                    return Result.Cancelled;
                }

                var advertencias = new List<string>();
                int cambios;
                using (var t = new Transaction(doc, "Asignar partición al refuerzo"))
                {
                    t.Start();
                    cambios = ClasificadorElementos.AsignarParticion(doc, refuerzo, CategoriaMetrado.Predeterminadas(),
                        ventana.Sobrescribir, ventana.TextoPersonalizado, advertencias);
                    t.Commit();
                }

                var dialogo = new TaskDialog("Asignar partición")
                {
                    MainInstruction = "Partición asignada",
                    MainContent = $"Armaduras revisadas: {refuerzo.Count}\nArmaduras modificadas: {cambios}",
                    CommonButtons = TaskDialogCommonButtons.Close,
                };
                if (advertencias.Count > 0) dialogo.ExpandedContent = string.Join("\n", advertencias);
                dialogo.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Asignar partición", "Ocurrió un error:\n\n" + ex.Message);
                return Result.Failed;
            }
        }
    }
}
