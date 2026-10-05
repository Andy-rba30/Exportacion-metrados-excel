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
    /// Comando "Parámetros y filtros", en dos pasos:
    ///   1. Escribe en el modelo los mismos parámetros que el metrado automático ("Metrado - Material",
    ///      "Metrado - Elemento", "Metrado - Peso (kg)", partición MAN y "ARBA - Origen" del refuerzo) y crea
    ///      los filtros de vista por colores, pero sin crear ninguna tabla. Así el usuario puede cambiar a
    ///      mano esos parámetros (p. ej. "Metrado - Elemento" = ESCALERAS) para sus propias tablas.
    ///   2. Lee los valores de "Metrado - Material" y "Metrado - Elemento" que hay en el modelo y crea una
    ///      tabla por cada combinación elegida, filtrada por esos valores, aparte de las predeterminadas y
    ///      sin reescribir ningún parámetro. Con "Actualizar filtros", en vez de tablas crea o actualiza
    ///      los filtros de vista: los predeterminados y uno por cada combinación propia elegida.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ParametrosMetradoCommand : IExternalCommand
    {
        private const string Titulo = "Parámetros y filtros del metrado";

        /// <summary>Contadores que el paso 1 escribe en el modelo, para el resumen final.</summary>
        private sealed class Contadores
        {
            public int Subproyectos, Clasificados, MaterialesRespetados, Particionados, ParticionesArba;
            public int ElementosRefuerzo, ElementosGrupo, Miscelaneos, Pesados, PerfilesPesados, PesosRespetados, RefuerzoPropagado;
            public bool ParametrosOk;
        }

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
                // Combinaciones de valores presentes en el modelo (solo lectura), para la pestaña 2.
                bool parametrosExisten = ClasificadorElementos.IdParametroMaterial(doc) != null ||
                                         ClasificadorElementos.IdParametroElementoRefuerzo(doc) != null;
                List<CombinacionMetrado> combinaciones = LectorCombinaciones.Leer(doc, CategoriaMetrado.Predeterminadas());

                var ventana = new ParametrosMetradoWindow(combinaciones, parametrosExisten);
                _ = new System.Windows.Interop.WindowInteropHelper(ventana)
                {
                    Owner = commandData.Application.MainWindowHandle
                };
                if (ventana.ShowDialog() != true) return Result.Cancelled;

                switch (ventana.Modo)
                {
                    case ModoParametros.TablasDesdeParametros:
                        return CrearTablas(uidoc, ventana.Opciones, ventana.CombinacionesSeleccionadas);
                    case ModoParametros.ActualizarFiltros:
                        return ActualizarFiltros(uidoc, ventana.Opciones, ventana.CombinacionesSeleccionadas);
                    default:
                        return EscribirParametros(uidoc, ventana.Opciones);
                }
            }
            catch (Exception ex)
            {
                // El error se muestra aquí; no se devuelve en "message" para que Revit
                // no lo repita en su diálogo "Error - cannot be ignored".
                TaskDialog.Show(Titulo, "Ocurrió un error:\n\n" + ex.Message);
                return Result.Failed;
            }
        }

        // ------------------------------------------------------------------
        // Paso 1: parámetros y filtros, sin tablas
        // ------------------------------------------------------------------

        /// <summary>
        /// Los mismos pasos del metrado automático salvo las tablas y el Excel: cálculo del modelo
        /// (pesos), reserva de subproyectos, parámetros del contrato, "Metrado - Material",
        /// particiones MAN, "Metrado - Elemento", "Metrado - Peso (kg)" y filtros de vista.
        /// </summary>
        private static Result EscribirParametros(UIDocument uidoc, OpcionesMetrado opciones)
        {
            Document doc = uidoc.Document;
            var advertencias = new List<string>();
            var c = new Contadores();

            // 0. Cálculo directo del modelo: alimenta el peso de armaduras y perfiles.
            ResultadoMetrado resultado = new CalculadorMetrado(doc, opciones).Calcular();
            advertencias.AddRange(resultado.Advertencias);

            // 0a. Vista para los filtros de colores (la activa o, si no los admite, otra que sí).
            View vistaFiltros = null;
            if (opciones.CrearFiltrosVista)
            {
                vistaFiltros = GeneradorFiltrosVista.ElegirVista(MetradoAutomaticoCommand.VistasParaFiltros(uidoc), out string aviso);
                if (aviso != null) advertencias.Add(aviso);
            }

            // 0b. Modelos compartidos: reservar los subproyectos antes de escribir (fuera de la transacción).
            c.Subproyectos = opciones.ReservarSubproyectos
                ? GestorSubproyectos.Reservar(doc, vistaFiltros ?? uidoc.ActiveView, opciones, advertencias)
                : 0;

            GeneradorFiltrosVista filtros = null;
            using (var t = new Transaction(doc, "Parámetros y filtros del metrado"))
            {
                t.Start();

                // 1. Parámetros compartidos del contrato ARBA-comun.
                c.ParametrosOk = ClasificadorElementos.AsegurarParametrosContrato(doc, advertencias);
                doc.Regenerate();

                // 1a. Clasificación concreto / metálico en "Metrado - Material".
                var categoriasSeleccionadas = opciones.Categorias.Where(x => x.Seleccionada).ToList();
                bool conexiones = categoriasSeleccionadas.Any(x => x.EsConexiones);
                var categoriasBic = categoriasSeleccionadas
                    .Concat(conexiones ? opciones.Categorias.Where(x => x.PuedeSerMetalica) : Enumerable.Empty<CategoriaMetrado>())
                    .SelectMany(x => x.Categorias).Distinct().ToList();
                if (ClasificadorElementos.IdParametroMaterial(doc) != null)
                {
                    c.Clasificados = ClasificadorElementos.RellenarMaterial(doc, categoriasBic, opciones.ConservarClasificacionMaterial,
                        advertencias, out c.MaterialesRespetados);
                }

                // 1b. Partición del refuerzo sin origen ARBA: "CATEGORIA - MAN-marca" + ARBA - Origen = MANUAL.
                if (opciones.IncluirAcero && opciones.RellenarParticiones)
                {
                    c.Particionados = ClasificadorElementos.AsignarParticion(doc, ClasificadorElementos.TodoElRefuerzo(doc),
                        opciones.SobrescribirParticiones, null, advertencias, out c.ParticionesArba);
                }

                // 1b'. "Metrado - Elemento" en el refuerzo (grupo del anfitrión) y en los elementos (su grupo).
                //      Con "Conservar" solo se rellenan los vacíos: los textos propios del usuario se respetan.
                if (ClasificadorElementos.IdParametroElementoRefuerzo(doc) != null)
                {
                    if (opciones.IncluirAcero)
                    {
                        c.ElementosRefuerzo = ClasificadorElementos.RellenarElementoRefuerzo(doc, ClasificadorElementos.TodoElRefuerzo(doc),
                            opciones.Categorias, advertencias, opciones.ConservarElemento);
                    }
                    c.ElementosGrupo = ClasificadorElementos.RellenarElementoEnElementos(doc, categoriasBic, opciones.Categorias,
                        advertencias, out c.Miscelaneos, opciones.ConservarElemento);

                    // 1b''. El refuerzo de los elementos cuyo "Metrado - Elemento" cambió el usuario a mano
                    //       (ESCALERAS...) toma ese mismo texto; el del resto no se toca.
                    if (opciones.IncluirAcero && opciones.PropagarElementoAlRefuerzo)
                    {
                        c.RefuerzoPropagado = ClasificadorElementos.PropagarElementoDelAnfitrion(doc, ClasificadorElementos.TodoElRefuerzo(doc),
                            opciones.Categorias, advertencias);
                    }
                }

                // 1c. Peso en kg de armaduras y perfiles (los escritos por un add-in ARBA se respetan).
                if (ClasificadorElementos.IdParametroPeso(doc) != null)
                {
                    int respetadosBarras = 0;
                    if (opciones.IncluirAcero) c.Pesados = ClasificadorElementos.RellenarPesos(doc, resultado.Acero, advertencias, out respetadosBarras);
                    c.PerfilesPesados = ClasificadorElementos.RellenarPesosPerfiles(doc, resultado.AceroEstructural, advertencias, out int respetadosPerfiles);
                    c.PesosRespetados = respetadosBarras + respetadosPerfiles;
                }

                // 1e. Filtros de vista por colores (opcional). Sin tablas (1d).
                if (opciones.CrearFiltrosVista)
                {
                    filtros = new GeneradorFiltrosVista(doc, opciones);
                    filtros.Generar(vistaFiltros);
                    advertencias.AddRange(filtros.Advertencias);
                }
                t.Commit();
            }

            string contenido =
                $"Contrato ARBA-comun: {ClasificadorElementos.VersionContrato}" +
                (c.ParametrosOk ? string.Empty : " (no se pudieron asegurar todos sus parámetros; ver advertencias)") + "\n" +
                (c.Subproyectos > 0 ? $"Subproyectos reservados (modelo compartido): {c.Subproyectos}\n" : string.Empty) +
                $"Elementos clasificados (Metrado - Material): {c.Clasificados}" +
                (c.MaterialesRespetados > 0 ? $" (respetados de add-ins ARBA: {c.MaterialesRespetados})" : string.Empty) + "\n" +
                $"Elementos con grupo de metrado (Metrado - Elemento): {c.ElementosGrupo}\n" +
                $"Misceláneos (con Metrado - Partida): {c.Miscelaneos}\n" +
                (opciones.IncluirAcero
                    ? $"Refuerzos con partición asignada (CATEGORIA - MAN-marca): {c.Particionados}\n" +
                      $"Particiones de add-ins ARBA respetadas: {c.ParticionesArba}\n" +
                      $"Refuerzos con elemento anfitrión (Metrado - Elemento): {c.ElementosRefuerzo}\n" +
                      (opciones.PropagarElementoAlRefuerzo
                          ? $"Refuerzos con el \"Metrado - Elemento\" propio de su anfitrión (cambiado a mano): {c.RefuerzoPropagado}\n"
                          : string.Empty) +
                      $"Refuerzos con peso actualizado: {c.Pesados}\n"
                    : string.Empty) +
                $"Perfiles y piezas metálicas con peso actualizado: {c.PerfilesPesados}\n" +
                $"Pesos escritos por add-ins ARBA respetados: {c.PesosRespetados}\n" +
                (filtros != null
                    ? $"Filtros de vista por colores: {filtros.FiltrosCreados.Count} creados, {filtros.FiltrosReutilizados.Count} actualizados" +
                      (filtros.VistaAplicada != null ? $", aplicados a la vista \"{filtros.VistaAplicada}\"" : string.Empty) + "\n"
                    : string.Empty) +
                "\nNo se creó ninguna tabla. Ahora puede cambiar a mano \"Metrado - Material\" y \"Metrado - Elemento\" en los " +
                "elementos o armaduras que quiera y volver a este botón, pestaña 2, para crear tablas con esos valores.";

            var dialogo = new TaskDialog(Titulo)
            {
                MainInstruction = advertencias.Count == 0 ? "Parámetros y filtros escritos" : "Parámetros y filtros escritos con advertencias",
                MainContent = contenido,
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            if (advertencias.Count > 0) dialogo.ExpandedContent = "Advertencias:\n" + string.Join("\n", advertencias.Select(a => "  • " + a));
            dialogo.Show();
            return Result.Succeeded;
        }

        // ------------------------------------------------------------------
        // Paso 2: tablas a partir de los valores de los parámetros
        // ------------------------------------------------------------------

        private static Result CrearTablas(UIDocument uidoc, OpcionesMetrado opciones, List<CombinacionMetrado> combinaciones)
        {
            Document doc = uidoc.Document;
            List<ViewSchedule> tablas;
            GeneradorTablasRevit generador;

            using (var t = new Transaction(doc, "Tablas desde los parámetros del metrado"))
            {
                t.Start();
                generador = new GeneradorTablasRevit(doc, opciones, uidoc.ActiveView?.Id);
                tablas = generador.GenerarDesdeParametros(combinaciones);
                t.Commit();
            }

            if (opciones.AbrirTablaAlTerminar && tablas.Count > 0)
            {
                try { uidoc.ActiveView = tablas[0]; }
                catch (Exception) { /* no es crítico */ }
            }

            var dialogo = new TaskDialog(Titulo)
            {
                MainInstruction = generador.Advertencias.Count == 0 ? "Tablas creadas" : "Tablas creadas con advertencias",
                MainContent =
                    $"Combinaciones de \"Metrado - Material\" / \"Metrado - Elemento\" procesadas: {combinaciones.Count}\n" +
                    $"Tablas creadas en Revit: {generador.TablasCreadas.Count}\n" +
                    $"Tablas existentes reutilizadas: {generador.TablasReutilizadas.Count}\n\n" +
                    "No se modificó ningún parámetro. Puede exportar estas tablas con el botón \"Exportar a Excel\".",
                CommonButtons = TaskDialogCommonButtons.Close,
            };

            var detalle = new List<string>();
            if (tablas.Count > 0)
            {
                detalle.Add("Tablas:");
                detalle.AddRange(tablas.Select(t => "  • " + t.Name));
            }
            if (generador.Advertencias.Count > 0)
            {
                detalle.Add("");
                detalle.Add("Advertencias:");
                detalle.AddRange(generador.Advertencias.Select(a => "  • " + a));
            }
            if (detalle.Count > 0) dialogo.ExpandedContent = string.Join("\n", detalle);
            dialogo.Show();
            return Result.Succeeded;
        }

        // ------------------------------------------------------------------
        // Paso 2b: filtros de vista de los valores de los parámetros, sin tablas
        // ------------------------------------------------------------------

        /// <summary>
        /// Crea o actualiza los filtros de vista por colores predeterminados (los de los elementos marcados
        /// en la pestaña 1, como el paso 1) y crea uno por cada combinación propia marcada en la pestaña 2,
        /// con los mismos valores exactos que su tabla. No crea tablas ni reescribe ningún parámetro.
        /// </summary>
        private static Result ActualizarFiltros(UIDocument uidoc, OpcionesMetrado opciones, List<CombinacionMetrado> combinaciones)
        {
            Document doc = uidoc.Document;
            var advertencias = new List<string>();

            // Vista para los filtros (la activa o, si no los admite, otra que sí).
            View vista = GeneradorFiltrosVista.ElegirVista(MetradoAutomaticoCommand.VistasParaFiltros(uidoc), out string aviso);
            if (aviso != null) advertencias.Add(aviso);

            var propias = combinaciones.Where(c => !c.EsPredeterminada).ToList();
            int cubiertas = combinaciones.Count - propias.Count;

            GeneradorFiltrosVista filtros;
            using (var t = new Transaction(doc, "Filtros de vista del metrado"))
            {
                t.Start();
                filtros = new GeneradorFiltrosVista(doc, opciones);
                filtros.Generar(vista);
                filtros.GenerarDesdeParametros(propias, vista);
                t.Commit();
            }
            advertencias.AddRange(filtros.Advertencias);

            var dialogo = new TaskDialog(Titulo)
            {
                MainInstruction = advertencias.Count == 0 ? "Filtros de vista actualizados" : "Filtros de vista actualizados con advertencias",
                MainContent =
                    $"Filtros de vista por colores: {filtros.FiltrosCreados.Count} creados, {filtros.FiltrosReutilizados.Count} actualizados" +
                    (filtros.VistaAplicada != null ? $", aplicados a la vista \"{filtros.VistaAplicada}\"" : string.Empty) + "\n" +
                    $"Combinaciones propias marcadas (con filtro propio): {propias.Count}\n" +
                    (cubiertas > 0 ? $"Combinaciones predeterminadas marcadas (las cubren los filtros predeterminados): {cubiertas}\n" : string.Empty) +
                    "\nNo se creó ninguna tabla ni se modificó ningún parámetro.",
                CommonButtons = TaskDialogCommonButtons.Close,
            };

            var detalle = new List<string>();
            var nombres = filtros.FiltrosCreados.Concat(filtros.FiltrosReutilizados).Select(f => f.Name).ToList();
            if (nombres.Count > 0)
            {
                detalle.Add("Filtros:");
                detalle.AddRange(nombres.Select(n => "  • " + n));
            }
            if (advertencias.Count > 0)
            {
                detalle.Add("");
                detalle.Add("Advertencias:");
                detalle.AddRange(advertencias.Select(a => "  • " + a));
            }
            if (detalle.Count > 0) dialogo.ExpandedContent = string.Join("\n", detalle);
            dialogo.Show();
            return Result.Succeeded;
        }
    }
}
