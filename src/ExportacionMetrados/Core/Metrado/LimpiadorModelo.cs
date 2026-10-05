using System;
using System.Collections.Generic;
using System.Linq;
using Arba.Comun;
using Autodesk.Revit.DB;
using ExportacionMetrados.Core.Metrado.Encofrado;

namespace ExportacionMetrados.Core.Metrado
{
    /// <summary>Qué limpiar (botón "Limpiar modelo").</summary>
    public class OpcionesLimpieza
    {
        /// <summary>Eliminar las tablas de planificación del plugin ("Metrado ...").</summary>
        public bool EliminarTablas { get; set; }
        /// <summary>Eliminar los filtros de vista del plugin ("Metrado - ...").</summary>
        public bool EliminarFiltros { get; set; }
        /// <summary>Eliminar la piel de encofrado de verificación (modelos genéricos "Piel de encofrado - ...") y sus materiales.</summary>
        public bool EliminarPiel { get; set; }
        /// <summary>Vaciar los valores que escribe el plugin en los parámetros (sin quitar los parámetros).</summary>
        public bool LimpiarValores { get; set; }
        /// <summary>Quitar del proyecto los parámetros compartidos que crea el plugin (dejan de salir en Propiedades).</summary>
        public bool BorrarParametros { get; set; }

        public bool Alguna => EliminarTablas || EliminarFiltros || EliminarPiel || LimpiarValores || BorrarParametros;
    }

    /// <summary>
    /// Deshace, a elección, lo que el plugin deja en el proyecto:
    ///   - tablas de planificación "Metrado ..." (concreto, acero estructural, acero, encofrado y las propias);
    ///   - filtros de vista "Metrado - ..." (se quitan también de las vistas que los usaban);
    ///   - piel de encofrado de verificación (modelos genéricos "Piel de encofrado - ...") y sus materiales;
    ///   - valores de "Metrado - Material", "Metrado - Elemento", "Metrado - Peso (kg)" y
    ///     "Metrado - Encofrado (m²)" en elementos y refuerzo, y la partición + "ARBA - Origen" del refuerzo
    ///     que particionó el plugin (origen MANUAL). Los elementos con origen de un add-in ARBA de armado
    ///     (rejillas, ángulos, barras ZAP/CCO/BLQ...) conservan todo lo suyo;
    ///   - los parámetros compartidos que el plugin crea (los ocho del contrato ARBA-comun y
    ///     "Metrado - Encofrado (m²)"): se quita su vínculo del proyecto y dejan de salir en Propiedades.
    /// Debe llamarse dentro de una transacción abierta; Ctrl+Z lo deshace.
    /// </summary>
    public class LimpiadorModelo
    {
        /// <summary>Prefijo común de todas las tablas que crea el plugin.</summary>
        public const string PrefijoTablas = "Metrado ";
        /// <summary>Prefijo común de todos los filtros de vista que crea el plugin.</summary>
        public const string PrefijoFiltros = "Metrado - ";

        private readonly Document _doc;
        private readonly ElementId _vistaActivaId;

        public List<string> Advertencias { get; } = new List<string>();
        public List<string> TablasEliminadas { get; } = new List<string>();
        public List<string> FiltrosEliminados { get; } = new List<string>();
        public List<string> ParametrosBorrados { get; } = new List<string>();
        /// <summary>Pieles de encofrado de verificación eliminadas.</summary>
        public int PielesEliminadas { get; private set; }
        /// <summary>Elementos (no refuerzo) a los que se vació algún valor.</summary>
        public int ElementosLimpiados { get; private set; }
        /// <summary>Refuerzos a los que se vació algún valor.</summary>
        public int RefuerzosLimpiados { get; private set; }
        /// <summary>Elementos y refuerzos con origen de un add-in ARBA, respetados.</summary>
        public int RespetadosArba { get; private set; }

        public LimpiadorModelo(Document doc, ElementId vistaActivaId)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _vistaActivaId = vistaActivaId ?? ElementId.InvalidElementId;
        }

        public void Limpiar(OpcionesLimpieza op)
        {
            if (op == null) return;
            if (op.EliminarTablas) EliminarTablas();
            if (op.EliminarFiltros) EliminarFiltros();
            if (op.EliminarPiel) EliminarPiel();
            if (op.LimpiarValores) LimpiarValores();
            if (op.BorrarParametros) BorrarParametros();
        }

        // ------------------------------------------------------------------
        // Tablas y filtros
        // ------------------------------------------------------------------

        private void EliminarTablas()
        {
            var tablas = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(v => !v.IsTemplate && v.Name.StartsWith(PrefijoTablas, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (ViewSchedule t in tablas)
            {
                string nombre = t.Name;
                if (t.Id == _vistaActivaId)
                {
                    Advertencias.Add($"La tabla \"{nombre}\" es la vista activa y no se puede eliminar; ciérrela y vuelva a ejecutar.");
                    continue;
                }
                try
                {
                    _doc.Delete(t.Id);
                    TablasEliminadas.Add(nombre);
                }
                catch (Exception ex)
                {
                    Advertencias.Add($"No se pudo eliminar la tabla \"{nombre}\": {ex.Message}");
                }
            }
        }

        private void EliminarFiltros()
        {
            var filtros = new FilteredElementCollector(_doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .Where(f => f.Name.StartsWith(PrefijoFiltros, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (ParameterFilterElement f in filtros)
            {
                string nombre = f.Name;
                try
                {
                    _doc.Delete(f.Id);
                    FiltrosEliminados.Add(nombre);
                }
                catch (Exception ex)
                {
                    Advertencias.Add($"No se pudo eliminar el filtro \"{nombre}\": {ex.Message}");
                }
            }
        }

        private void EliminarPiel()
        {
            try { PielesEliminadas = PielEncofrado.Eliminar(_doc, incluirMateriales: true); }
            catch (Exception ex) { Advertencias.Add("No se pudo eliminar la piel de encofrado: " + ex.Message); }
        }

        // ------------------------------------------------------------------
        // Valores
        // ------------------------------------------------------------------

        /// <summary>Parámetros cuyos valores escribe el plugin en los elementos (no refuerzo).</summary>
        private static readonly ArbaParam[] ParametrosElemento =
        {
            ArbaContract.Material, ArbaContract.Elemento, ArbaContract.Peso, ParametroEncofrado.Definicion,
        };

        private void LimpiarValores()
        {
            // 1. Elementos de las categorías a las que el contrato vincula esos parámetros (sin el refuerzo).
            var categorias = new List<BuiltInCategory>();
            foreach (string n in ParametrosElemento.SelectMany(p => p.Categories))
            {
                BuiltInCategory bic = ArbaRevit.ParseCategory(n);
                if (bic == BuiltInCategory.INVALID || categorias.Contains(bic)) continue;
                if (ClasificadorElementos.CategoriasRefuerzo.Contains(bic)) continue;
                categorias.Add(bic);
            }

            foreach (Element e in ArbaRevit.Instances(_doc, categorias).ToElements())
            {
                try
                {
                    // Lo que escribió un add-in ARBA de armado (rejillas, ángulos...) no es del plugin.
                    if (ArbaOrigin.IsArba(e)) { RespetadosArba++; continue; }
                    bool alguno = false;
                    foreach (ArbaParam p in ParametrosElemento) alguno |= Vaciar(e, p);
                    if (alguno) ElementosLimpiados++;
                }
                catch (Exception ex)
                {
                    Advertencias.Add($"No se pudieron limpiar los parámetros del elemento {e.Id}: {ex.Message}");
                }
            }

            // 2. Refuerzo: "Metrado - Elemento" y "Metrado - Peso (kg)" los calcula siempre el plugin. La
            //    partición y "ARBA - Origen" solo si los escribió el plugin (MANUAL); las de los add-ins se respetan.
            foreach (Element r in ClasificadorElementos.TodoElRefuerzo(_doc))
            {
                try
                {
                    bool alguno = Vaciar(r, ArbaContract.Elemento) | Vaciar(r, ArbaContract.Peso);
                    if (ArbaOrigin.IsArba(r))
                    {
                        if (ArbaOrigin.PrefixOf(r) == ArbaContract.Manual)
                        {
                            alguno |= ArbaRevit.SetText(ArbaPartition.PartitionParameter(r), string.Empty)
                                      && Vaciar(r, ArbaContract.Origen);
                        }
                        else RespetadosArba++;
                    }
                    if (alguno) RefuerzosLimpiados++;
                }
                catch (Exception ex)
                {
                    Advertencias.Add($"No se pudieron limpiar los parámetros del refuerzo {r.Id}: {ex.Message}");
                }
            }
        }

        /// <summary>Vacía el parámetro si tiene valor. Devuelve true si cambió algo.</summary>
        private static bool Vaciar(Element e, ArbaParam p)
        {
            Parameter par = ArbaSharedParams.Get(e, p);
            if (par == null || par.IsReadOnly || !par.HasValue) return false;
            if (par.StorageType == StorageType.String && string.IsNullOrEmpty(par.AsString())) return false;
            try
            {
                if (par.ClearValue()) return true;
            }
            catch (Exception) { }
            switch (par.StorageType)
            {
                case StorageType.String: return par.Set(string.Empty);
                case StorageType.Double: return par.Set(0.0);
                case StorageType.Integer: return par.Set(0);
                default: return false;
            }
        }

        // ------------------------------------------------------------------
        // Parámetros (vínculos del proyecto)
        // ------------------------------------------------------------------

        private void BorrarParametros()
        {
            var parametros = new List<ArbaParam>(ArbaContract.Parametros) { ParametroEncofrado.Definicion };
            foreach (ArbaParam p in parametros)
            {
                try
                {
                    Definition def = ArbaSharedParams.FindBoundByGuid(_doc, p.Guid);
                    if (def == null) continue;
                    if (_doc.ParameterBindings.Remove(def)) ParametrosBorrados.Add(p.Name);
                    else Advertencias.Add($"No se pudo quitar el parámetro \"{p.Name}\" del proyecto.");
                }
                catch (Exception ex)
                {
                    Advertencias.Add($"No se pudo quitar el parámetro \"{p.Name}\" del proyecto: {ex.Message}");
                }
            }
        }
    }
}
