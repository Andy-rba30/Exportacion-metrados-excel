using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ExportacionMetrados.Core.Metrado
{
    /// <summary>
    /// Crea (o reutiliza) las tablas de planificación de metrado dentro del
    /// proyecto de Revit: una de concreto y una de acero por cada categoría.
    /// Todos los métodos deben llamarse dentro de una transacción abierta.
    /// </summary>
    public class GeneradorTablasRevit
    {
        public const string PrefijoConcreto = "Metrado concreto - ";
        public const string PrefijoAcero = "Metrado acero - ";
        public const string NombreAceroGeneral = "Metrado acero";

        private readonly Document _doc;
        private readonly OpcionesMetrado _op;
        private readonly ElementId _vistaActivaId;

        public List<string> Advertencias { get; } = new List<string>();
        public List<ViewSchedule> TablasCreadas { get; } = new List<ViewSchedule>();
        public List<ViewSchedule> TablasReutilizadas { get; } = new List<ViewSchedule>();

        public GeneradorTablasRevit(Document doc, OpcionesMetrado opciones, ElementId vistaActivaId)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _op = opciones ?? throw new ArgumentNullException(nameof(opciones));
            _vistaActivaId = vistaActivaId ?? ElementId.InvalidElementId;
        }

        /// <summary>
        /// Genera las tablas y devuelve todas (creadas y reutilizadas) en orden.
        /// </summary>
        public List<ViewSchedule> Generar()
        {
            var tablas = new List<ViewSchedule>();
            var categorias = _op.Categorias.Where(c => c.Seleccionada).ToList();

            foreach (CategoriaMetrado cat in categorias)
            {
                ViewSchedule t = CrearOReutilizar(PrefijoConcreto + cat.Nombre, () => CrearTablaConcreto(cat));
                if (t != null) tablas.Add(t);
            }

            if (_op.IncluirAcero)
            {
                bool filtroDisponible = true;
                foreach (CategoriaMetrado cat in categorias)
                {
                    if (!filtroDisponible) break;

                    bool filtrada = true;
                    ViewSchedule t = CrearOReutilizar(PrefijoAcero + cat.Nombre, () => CrearTablaAcero(cat, out filtrada));
                    if (t == null) continue;

                    if (!filtrada && TablasCreadas.Contains(t))
                    {
                        // No se pudo filtrar por categoría del anfitrión: se deja una
                        // sola tabla general de acero agrupada por partición.
                        filtroDisponible = false;
                        Renombrar(t, NombreAceroGeneral);
                        Advertencias.Add("No fue posible filtrar el acero por categoría del anfitrión; " +
                                         "se creó una sola tabla \"" + NombreAceroGeneral + "\" agrupada por partición.");
                    }
                    tablas.Add(t);
                }
            }

            return tablas;
        }

        // ------------------------------------------------------------------

        private ViewSchedule CrearOReutilizar(string nombre, Func<ViewSchedule> crear)
        {
            ViewSchedule existente = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, nombre, StringComparison.OrdinalIgnoreCase));

            if (existente != null)
            {
                if (!_op.RegenerarTablasExistentes)
                {
                    TablasReutilizadas.Add(existente);
                    return existente;
                }
                if (existente.Id == _vistaActivaId)
                {
                    Advertencias.Add($"La tabla \"{nombre}\" es la vista activa y no se puede regenerar; se reutilizó.");
                    TablasReutilizadas.Add(existente);
                    return existente;
                }
                _doc.Delete(existente.Id);
            }

            try
            {
                ViewSchedule nueva = crear();
                Renombrar(nueva, nombre);
                TablasCreadas.Add(nueva);
                return nueva;
            }
            catch (Exception ex)
            {
                Advertencias.Add($"No se pudo crear la tabla \"{nombre}\": {ex.Message}");
                return null;
            }
        }

        private void Renombrar(ViewSchedule tabla, string nombre)
        {
            try { tabla.Name = nombre; }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                tabla.Name = nombre + " (" + DateTime.Now.ToString("HHmmss") + ")";
            }
        }

        // ------------------------------------------------------------------
        // Concreto
        // ------------------------------------------------------------------

        private ViewSchedule CrearTablaConcreto(CategoriaMetrado cat)
        {
            ViewSchedule vs = ViewSchedule.CreateSchedule(_doc, new ElementId(cat.Categoria));
            ScheduleDefinition def = vs.Definition;
            IList<SchedulableField> campos = def.GetSchedulableFields();

            ScheduleField nivel = Agregar(def, campos, "Nivel",
                BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,   // vigas
                BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,          // columnas
                BuiltInParameter.WALL_BASE_CONSTRAINT,             // muros
                BuiltInParameter.LEVEL_PARAM,                      // losas, cimentaciones
                BuiltInParameter.SCHEDULE_LEVEL_PARAM);

            ScheduleField tipo = Agregar(def, campos, "Elemento", BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM);
            ScheduleField material = Agregar(def, campos, "Material", BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);

            ScheduleField cantidad = null;
            try
            {
                cantidad = def.AddField(ScheduleFieldType.Count);
                cantidad.ColumnHeading = "Cantidad";
            }
            catch (Exception ex) { Advertencias.Add($"{cat.Nombre}: sin campo Cantidad ({ex.Message})"); }

            ScheduleField longitud = Agregar(def, campos, "Longitud",
                BuiltInParameter.INSTANCE_LENGTH_PARAM, BuiltInParameter.CURVE_ELEM_LENGTH);
            ScheduleField area = Agregar(def, campos, "Área", BuiltInParameter.HOST_AREA_COMPUTED);
            ScheduleField espesor = Agregar(def, campos, "Espesor",
                BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM,
                BuiltInParameter.WALL_ATTR_WIDTH_PARAM,
                BuiltInParameter.STRUCTURAL_FOUNDATION_THICKNESS);
            ScheduleField volumen = Agregar(def, campos, "Volumen", BuiltInParameter.HOST_VOLUME_COMPUTED);

            Totales(longitud, area, volumen);

            if (nivel != null)
            {
                def.AddSortGroupField(new ScheduleSortGroupField(nivel.FieldId)
                {
                    ShowHeader = true,
                    ShowFooter = true,
                    ShowFooterTitle = true,
                    ShowBlankLine = true,
                });
            }
            if (tipo != null)
            {
                def.AddSortGroupField(new ScheduleSortGroupField(tipo.FieldId));
            }

            def.IsItemized = false;
            def.ShowGrandTotal = true;
            def.ShowGrandTotalTitle = true;
            def.ShowGrandTotalCount = true;
            def.GrandTotalTitle = "Total " + cat.Nombre;

            if (_op.FiltrarPorMaterial && material != null && !string.IsNullOrWhiteSpace(_op.TextoMaterialConcreto))
            {
                try
                {
                    def.AddFilter(new ScheduleFilter(material.FieldId, ScheduleFilterType.Contains, _op.TextoMaterialConcreto.Trim()));
                }
                catch (Exception ex)
                {
                    Advertencias.Add($"{cat.Nombre}: no se pudo aplicar el filtro de material ({ex.Message}).");
                }
            }

            return vs;
        }

        // ------------------------------------------------------------------
        // Acero
        // ------------------------------------------------------------------

        private ViewSchedule CrearTablaAcero(CategoriaMetrado cat, out bool filtrada)
        {
            filtrada = false;

            ViewSchedule vs = ViewSchedule.CreateSchedule(_doc, new ElementId(BuiltInCategory.OST_Rebar));
            ScheduleDefinition def = vs.Definition;
            IList<SchedulableField> campos = def.GetSchedulableFields();

            ScheduleField particion = Agregar(def, campos, "Partición", BuiltInParameter.NUMBER_PARTITION_PARAM);

            ScheduleField hostCategoria = AgregarPorNombre(def, campos, "Elemento anfitrión",
                new[] { "REBAR_HOST_CATEGORY", "REBAR_ELEM_HOST_CATEGORY" },
                new[] { "Host Category", "Categoría de anfitrión", "Categoría del anfitrión", "Categoría de host" });

            ScheduleField hostMarca = Agregar(def, campos, "Marca anfitrión", BuiltInParameter.REBAR_ELEM_HOST_MARK);
            ScheduleField tipo = Agregar(def, campos, "Tipo de barra", BuiltInParameter.ELEM_TYPE_PARAM);
            ScheduleField diametro = Agregar(def, campos, "Diámetro", BuiltInParameter.REBAR_BAR_DIAMETER);
            ScheduleField cantidad = Agregar(def, campos, "N° barras", BuiltInParameter.REBAR_ELEM_QUANTITY_OF_BARS);
            ScheduleField longBarra = Agregar(def, campos, "Longitud de barra", BuiltInParameter.REBAR_ELEM_LENGTH);
            ScheduleField longTotal = Agregar(def, campos, "Longitud total", BuiltInParameter.REBAR_ELEM_TOTAL_LENGTH);

            // Peso unitario (kg/m): parámetro del tipo de barra indicado por el usuario.
            var nombresPeso = new List<string>();
            if (!string.IsNullOrWhiteSpace(_op.NombreParametroPeso)) nombresPeso.Add(_op.NombreParametroPeso.Trim());
            nombresPeso.AddRange(CalculadorMetrado.NombresParametroPesoBarra);
            ScheduleField pesoUnitario = AgregarPorNombre(def, campos, "Peso unitario",
                new[] { "REBAR_BAR_MASS_PER_UNIT_LENGTH" }, nombresPeso.ToArray());

            // Masa total por barra, si la versión de Revit la ofrece.
            ScheduleField masaTotal = AgregarPorNombre(def, campos, "Peso total",
                new[] { "REBAR_ELEM_TOTAL_MASS", "REBAR_ELEM_TOTAL_BAR_MASS" },
                new[] { "Total Bar Mass", "Masa total de barra", "Masa total de barras", "Peso total" });

            Totales(cantidad, longTotal, masaTotal);

            if (particion != null)
            {
                def.AddSortGroupField(new ScheduleSortGroupField(particion.FieldId)
                {
                    ShowHeader = true,
                    ShowFooter = true,
                    ShowFooterTitle = true,
                    ShowBlankLine = true,
                });
            }
            if (diametro != null)
            {
                def.AddSortGroupField(new ScheduleSortGroupField(diametro.FieldId) { ShowFooter = true });
            }
            if (tipo != null)
            {
                def.AddSortGroupField(new ScheduleSortGroupField(tipo.FieldId));
            }

            def.IsItemized = false;
            def.ShowGrandTotal = true;
            def.ShowGrandTotalTitle = true;
            def.GrandTotalTitle = "Total acero " + cat.Nombre;

            if (hostCategoria != null)
            {
                string nombreCategoria = Category.GetCategory(_doc, cat.Categoria)?.Name;
                if (!string.IsNullOrEmpty(nombreCategoria))
                {
                    try
                    {
                        def.AddFilter(new ScheduleFilter(hostCategoria.FieldId, ScheduleFilterType.Equal, nombreCategoria));
                        filtrada = true;
                    }
                    catch (Exception ex)
                    {
                        Advertencias.Add($"Acero {cat.Nombre}: no se pudo filtrar por categoría del anfitrión ({ex.Message}).");
                    }
                }
            }

            if (pesoUnitario == null)
            {
                Advertencias.Add("No se encontró el parámetro de peso unitario \"" + _op.NombreParametroPeso +
                                 "\" en los tipos de barra; la tabla de acero de Revit no incluye esa columna.");
            }

            return vs;
        }

        // ------------------------------------------------------------------
        // Utilidades
        // ------------------------------------------------------------------

        private static ScheduleField Agregar(ScheduleDefinition def, IList<SchedulableField> campos, string encabezado,
            params BuiltInParameter[] candidatos)
        {
            foreach (BuiltInParameter bip in candidatos)
            {
                var id = new ElementId(bip);
                SchedulableField sf = campos.FirstOrDefault(c =>
                    c.ParameterId == id &&
                    (c.FieldType == ScheduleFieldType.Instance || c.FieldType == ScheduleFieldType.ElementType));
                if (sf == null) continue;

                try
                {
                    ScheduleField f = def.AddField(sf);
                    f.ColumnHeading = encabezado;
                    return f;
                }
                catch (Exception)
                {
                    // Campo no admitido en esta tabla: probar el siguiente candidato.
                }
            }
            return null;
        }

        /// <summary>
        /// Busca un campo por nombre de BuiltInParameter (resuelto en tiempo de ejecución,
        /// por si no existe en la versión de Revit compilada) o por el nombre visible.
        /// </summary>
        private ScheduleField AgregarPorNombre(ScheduleDefinition def, IList<SchedulableField> campos, string encabezado,
            string[] nombresBuiltIn, string[] nombresVisibles)
        {
            foreach (string n in nombresBuiltIn)
            {
                if (Enum.TryParse(n, out BuiltInParameter bip))
                {
                    ScheduleField f = Agregar(def, campos, encabezado, bip);
                    if (f != null) return f;
                }
            }

            foreach (SchedulableField sf in campos)
            {
                string nombre;
                try { nombre = sf.GetName(_doc); }
                catch { continue; }

                if (!nombresVisibles.Any(n => string.Equals(n, nombre, StringComparison.OrdinalIgnoreCase))) continue;

                try
                {
                    ScheduleField f = def.AddField(sf);
                    f.ColumnHeading = encabezado;
                    return f;
                }
                catch (Exception) { }
            }
            return null;
        }

        private static void Totales(params ScheduleField[] campos)
        {
            foreach (ScheduleField f in campos)
            {
                if (f == null) continue;
                try { f.DisplayType = ScheduleFieldDisplayType.Totals; }
                catch (Exception) { }
            }
        }
    }
}
