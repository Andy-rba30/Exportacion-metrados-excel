using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ExportacionMetrados.Core.Metrado
{
    /// <summary>
    /// En modelos compartidos (worksharing) reserva de antemano los subproyectos que
    /// el metrado va a modificar, para que Revit no muestre el aviso "You are trying
    /// to checkout a large number of elements..." y no reserve los elementos uno a uno:
    /// los subproyectos estándar (parámetros compartidos), el de la vista activa (a la
    /// que se añaden los filtros) y los de los elementos y refuerzos que se escriben.
    /// Debe llamarse FUERA de una transacción.
    /// </summary>
    public static class GestorSubproyectos
    {
        /// <summary>Devuelve el número de subproyectos reservados (0 si el modelo no es compartido).</summary>
        public static int Reservar(Document doc, View vistaActiva, OpcionesMetrado opciones, List<string> advertencias)
        {
            if (doc == null || !doc.IsWorkshared) return 0;
            try { if (doc.IsDetached) return 0; } catch (Exception) { }

            var ids = new HashSet<WorksetId>();

            try
            {
                foreach (Workset w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.StandardWorkset))
                {
                    ids.Add(w.Id);
                }
            }
            catch (Exception) { }

            if (vistaActiva != null) AgregarDe(doc, vistaActiva.Id, ids);

            foreach (CategoriaMetrado cat in opciones.Categorias.Where(c => c.Seleccionada))
            {
                try
                {
                    foreach (ElementId id in cat.Elementos(doc).ToElementIds())
                    {
                        AgregarDe(doc, id, ids);
                    }
                }
                catch (Exception) { }
            }
            foreach (Element r in ClasificadorElementos.TodoElRefuerzo(doc)) AgregarDe(doc, r.Id, ids);

            var pendientes = new HashSet<WorksetId>(ids.Where(id => id != WorksetId.InvalidWorksetId && !EsPropio(doc, id)));
            if (pendientes.Count == 0) return 0;

            try
            {
                // La API devuelve ICollection<WorksetId> (ISet en versiones antiguas): ICollection vale para ambas.
                ICollection<WorksetId> reservados = WorksharingUtils.CheckoutWorksets(doc, pendientes);
                WorksetTable tabla = doc.GetWorksetTable();
                foreach (WorksetId id in pendientes.Where(p => !reservados.Contains(p)))
                {
                    Workset w = null;
                    try { w = tabla.GetWorkset(id); } catch (Exception) { }
                    advertencias.Add($"No se pudo reservar el subproyecto \"{w?.Name ?? id.IntegerValue.ToString()}\"" +
                                     (string.IsNullOrEmpty(w?.Owner) ? "" : $" (propietario: {w.Owner})") +
                                     "; Revit intentará reservar sus elementos uno a uno.");
                }
                return reservados.Count;
            }
            catch (Exception ex)
            {
                advertencias.Add("No se pudieron reservar los subproyectos del modelo compartido: " + ex.Message);
                return 0;
            }
        }

        private static void AgregarDe(Document doc, ElementId id, HashSet<WorksetId> ids)
        {
            try { ids.Add(doc.GetWorksetId(id)); } catch (Exception) { }
        }

        private static bool EsPropio(Document doc, WorksetId id)
        {
            // Workset.IsEditable: el subproyecto lo tiene reservado el usuario actual
            // (WorksharingUtils.GetCheckoutStatus solo admite ElementId, no WorksetId).
            try { return doc.GetWorksetTable().GetWorkset(id)?.IsEditable ?? true; }
            catch (Exception) { return true; } // sin información: no se intenta
        }
    }
}
