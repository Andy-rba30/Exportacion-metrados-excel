using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ExportacionMetrados.Core.Metrado
{
    /// <summary>
    /// Crea (o reutiliza) las tablas de planificación de metrado dentro del
    /// proyecto de Revit:
    ///   - "Metrado concreto - {elemento}"          elementos con material de concreto
    ///   - "Metrado acero estructural - {elemento}" perfiles y piezas metálicas por peso (si los hay)
    ///   - "Metrado acero - {elemento}"             refuerzo cuyo anfitrión es de esa categoría
    ///   - "Metrado acero - General"                todo el refuerzo, por elemento y partición
    /// Una tabla que ya existe se reutiliza, salvo que se pida regenerarla o que tenga una
    /// estructura de una versión anterior (agrupada por nivel cuando ya no toca, sin el
    /// filtro por "Metrado - Material" o filtrada por partición en vez de por
    /// "Metrado - Elemento"): entonces se crea de nuevo y se avisa.
    /// Todos los métodos deben llamarse dentro de una transacción abierta.
    /// </summary>
    public class GeneradorTablasRevit
    {
        public const string PrefijoConcreto = "Metrado concreto - ";
        public const string PrefijoAceroEstructural = "Metrado acero estructural - ";
        public const string PrefijoAcero = "Metrado acero - ";
        public const string NombreAceroGeneral = "Metrado acero - General";

        private const int MaxFiltros = 8;

        /// <summary>Parámetros de nivel por los que se agrupan (o se agruparon) las tablas de elementos.</summary>
        private static readonly BuiltInParameter[] ParametrosNivel =
        {
            BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,          // columnas
            BuiltInParameter.WALL_BASE_CONSTRAINT,             // muros
            BuiltInParameter.LEVEL_PARAM,                      // losas, cimentaciones
            BuiltInParameter.SCHEDULE_LEVEL_PARAM,
            BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,   // vigas (versiones anteriores)
            BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM,
        };

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

        public List<ViewSchedule> Generar()
        {
            var tablas = new List<ViewSchedule>();
            var categorias = _op.Categorias.Where(c => c.Seleccionada).ToList();

            foreach (CategoriaMetrado cat in categorias)
            {
                MaterialesCategoria mats = ClasificarMateriales(cat);

                if (!cat.SoloMetalica)
                {
                    ViewSchedule t = CrearOReutilizar(PrefijoConcreto + cat.Nombre, () => CrearTablaElementos(cat, mats, concreto: true),
                        existente => MotivoTablaElementosDesactualizada(existente, cat));
                    if (t != null) tablas.Add(t);
                }

                // Las conexiones (solo metálicas) siempre van a su tabla de acero estructural.
                if ((_op.TablasAceroEstructural || cat.SoloMetalica) && cat.PuedeSerMetalica && HayElementosNoConcreto(cat, mats))
                {
                    ViewSchedule m = CrearOReutilizar(PrefijoAceroEstructural + cat.Nombre, () => CrearTablaElementos(cat, mats, concreto: false),
                        existente => MotivoTablaElementosDesactualizada(existente, cat));
                    if (m != null) tablas.Add(m);
                }
            }

            if (_op.IncluirAcero)
            {
                bool filtroDisponible = true;
                foreach (CategoriaMetrado cat in categorias)
                {
                    if (!_op.TablasAceroPorElemento || !filtroDisponible) break;
                    if (cat.SoloMetalica) continue;   // las conexiones no alojan refuerzo

                    bool filtrada = true;
                    ViewSchedule t = CrearOReutilizar(PrefijoAcero + cat.Nombre, () => CrearTablaRefuerzo(cat, out filtrada),
                        existente => MotivoTablaRefuerzoDesactualizada(existente, cat));
                    if (t == null) continue;

                    if (!filtrada && TablasCreadas.Contains(t))
                    {
                        // Sin filtro la tabla por categoría no tiene sentido.
                        filtroDisponible = false;
                        _doc.Delete(t.Id);
                        TablasCreadas.Remove(t);
                        Advertencias.Add("No fue posible filtrar el acero por partición ni por categoría del anfitrión; " +
                                         "solo se creó la tabla general de acero.");
                        continue;
                    }
                    tablas.Add(t);
                }

                if (_op.TablaAceroGeneral || !filtroDisponible)
                {
                    ViewSchedule g = CrearOReutilizar(NombreAceroGeneral, () => CrearTablaRefuerzo(null, out _),
                        existente => MotivoTablaRefuerzoDesactualizada(existente, null));
                    if (g != null) tablas.Add(g);
                }
            }

            return tablas;
        }

        // ------------------------------------------------------------------
        // Reutilización
        // ------------------------------------------------------------------

        /// <param name="nombre">Nombre de la tabla.</param>
        /// <param name="crear">Crea la tabla nueva.</param>
        /// <param name="motivoDesactualizada">
        /// Dada la tabla existente, devuelve por qué su estructura es de una versión anterior
        /// (se regenera aunque no se haya pedido) o null si sirve tal cual.
        /// </param>
        private ViewSchedule CrearOReutilizar(string nombre, Func<ViewSchedule> crear, Func<ViewSchedule, string> motivoDesactualizada = null)
        {
            ViewSchedule existente = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, nombre, StringComparison.OrdinalIgnoreCase));

            if (existente != null)
            {
                string motivo = null;
                if (!_op.RegenerarTablasExistentes && motivoDesactualizada != null)
                {
                    try { motivo = motivoDesactualizada(existente); }
                    catch (Exception) { motivo = null; }
                }

                if (!_op.RegenerarTablasExistentes && motivo == null)
                {
                    TablasReutilizadas.Add(existente);
                    return existente;
                }
                if (existente.Id == _vistaActivaId)
                {
                    Advertencias.Add($"La tabla \"{nombre}\" es la vista activa y no se puede regenerar; se reutilizó." +
                                     (motivo != null ? $" Tiene una estructura antigua ({motivo}): ciérrela y vuelva a ejecutar el metrado." : string.Empty));
                    TablasReutilizadas.Add(existente);
                    return existente;
                }
                if (motivo != null)
                {
                    Advertencias.Add($"La tabla \"{nombre}\" tenía una estructura antigua ({motivo}); se creó de nuevo.");
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

        /// <summary>
        /// Motivo por el que una tabla de elementos creada por una versión anterior ya no
        /// corresponde a la estructura actual, o null si sirve.
        /// </summary>
        private string MotivoTablaElementosDesactualizada(ViewSchedule tabla, CategoriaMetrado cat)
        {
            ScheduleDefinition def = tabla.Definition;

            // Agrupada por nivel cuando la categoría ya no se agrupa así (vigas, losas, cimentaciones).
            if (!cat.AgruparPorNivel)
            {
                int n = def.GetSortGroupFieldCount();
                for (int i = 0; i < n; i++)
                {
                    ScheduleField f = def.GetField(def.GetSortGroupField(i).FieldId);
                    if (f != null && ParametrosNivel.Any(bip => f.ParameterId == new ElementId(bip)))
                    {
                        return "agrupada por nivel";
                    }
                }
            }

            // Sin filtro por "Metrado - Material" aunque el parámetro ya existe en el proyecto
            // (tablas creadas cuando solo se filtraba por el nombre del material).
            ElementId idMaterial = ClasificadorElementos.IdParametroMaterial(_doc);
            if (idMaterial != null && !TieneFiltroPorParametro(def, idMaterial))
            {
                return "sin filtro por \"" + ClasificadorElementos.NombreParametroMaterial + "\"";
            }

            return null;
        }

        /// <summary>
        /// Motivo por el que una tabla de refuerzo creada por una versión anterior ya no
        /// corresponde a la estructura actual, o null si sirve. Las tablas por elemento deben
        /// filtrar por "Metrado - Elemento" (tipo de anfitrión real) y no por la partición,
        /// que el usuario puede tener con sus propios textos; la general debe mostrarlo.
        /// </summary>
        private string MotivoTablaRefuerzoDesactualizada(ViewSchedule tabla, CategoriaMetrado cat)
        {
            ElementId idElemento = ClasificadorElementos.IdParametroElementoRefuerzo(_doc);
            if (idElemento == null) return null;
            ScheduleDefinition def = tabla.Definition;
            string campo = ClasificadorElementos.NombreParametroElementoRefuerzo;

            if (cat != null && !TieneFiltroPorParametro(def, idElemento))
            {
                return "filtrada por partición y no por \"" + campo + "\"";
            }
            if (cat == null && !TieneCampoDeParametro(def, idElemento))
            {
                return "sin la columna de elemento (\"" + campo + "\")";
            }
            return null;
        }

        /// <summary>True si algún filtro de la tabla actúa sobre el parámetro (compartido) indicado.</summary>
        private static bool TieneFiltroPorParametro(ScheduleDefinition def, ElementId idParametro)
        {
            int n = def.GetFilterCount();
            for (int i = 0; i < n; i++)
            {
                ScheduleField f = def.GetField(def.GetFilter(i).FieldId);
                if (f != null && f.ParameterId == idParametro) return true;
            }
            return false;
        }

        /// <summary>True si la tabla tiene un campo del parámetro (compartido) indicado.</summary>
        private static bool TieneCampoDeParametro(ScheduleDefinition def, ElementId idParametro)
        {
            int n = def.GetFieldCount();
            for (int i = 0; i < n; i++)
            {
                ScheduleField f = def.GetField(i);
                if (f != null && f.ParameterId == idParametro) return true;
            }
            return false;
        }

        private static void Renombrar(ViewSchedule tabla, string nombre)
        {
            try { tabla.Name = nombre; }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                tabla.Name = nombre + " (" + DateTime.Now.ToString("HHmmss") + ")";
            }
        }

        // ------------------------------------------------------------------
        // Materiales usados por la categoría (para separar concreto de metálico)
        // ------------------------------------------------------------------

        private class MaterialesCategoria
        {
            public HashSet<ElementId> Concreto { get; } = new HashSet<ElementId>();
            public HashSet<ElementId> NoConcreto { get; } = new HashSet<ElementId>();
            public Dictionary<ElementId, string> Nombres { get; } = new Dictionary<ElementId, string>();
        }

        private MaterialesCategoria ClasificarMateriales(CategoriaMetrado cat)
        {
            var r = new MaterialesCategoria();
            var elementos = new FilteredElementCollector(_doc)
                .OfCategory(cat.Categoria)
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (Element e in elementos)
            {
                Material m;
                try { m = CalculadorMetrado.MaterialEstructuralDe(_doc, e); }
                catch { continue; }
                if (m == null || r.Nombres.ContainsKey(m.Id)) continue;

                r.Nombres[m.Id] = m.Name;
                if (CalculadorMetrado.MaterialEsConcreto(_doc, m)) r.Concreto.Add(m.Id);
                else r.NoConcreto.Add(m.Id);
            }
            return r;
        }

        // ------------------------------------------------------------------
        // Tablas de elementos (concreto / acero estructural)
        // ------------------------------------------------------------------

        private ViewSchedule CrearTablaElementos(CategoriaMetrado cat, MaterialesCategoria mats, bool concreto)
        {
            ViewSchedule vs = ViewSchedule.CreateSchedule(_doc, new ElementId(cat.Categoria));
            ScheduleDefinition def = vs.Definition;
            IList<SchedulableField> campos = def.GetSchedulableFields();

            // En vigas, losas y cimentaciones no se agrupa por nivel (una viga puede cruzar
            // varios, las losas se metran por tipo y las cimentaciones comparten el nivel de
            // fundación): solo por tipo. Columnas y muros sí.
            ScheduleField nivel = !cat.AgruparPorNivel ? null : Agregar(def, campos, "Nivel",
                BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,          // columnas
                BuiltInParameter.WALL_BASE_CONSTRAINT,             // muros
                BuiltInParameter.LEVEL_PARAM,
                BuiltInParameter.SCHEDULE_LEVEL_PARAM);

            ScheduleField tipo = Agregar(def, campos, "Elemento", BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM);
            ScheduleField material = Agregar(def, campos, "Material", BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);

            try
            {
                ScheduleField cantidad = def.AddField(ScheduleFieldType.Count);
                cantidad.ColumnHeading = "Cantidad";
            }
            catch (Exception ex) { Advertencias.Add($"{cat.Nombre}: sin campo Cantidad ({ex.Message})"); }

            // En los perfiles metálicos la longitud es la de corte (la pieza real, la misma
            // con la que el plugin calcula el peso); si la categoría no la expone (columnas),
            // la longitud del elemento. Las piezas pesadas por volumen no tienen longitud.
            ScheduleField longitud = null;
            if (!cat.PesoPorVolumen)
            {
                longitud = concreto
                    ? Agregar(def, campos, "Longitud", BuiltInParameter.INSTANCE_LENGTH_PARAM, BuiltInParameter.CURVE_ELEM_LENGTH)
                    : Agregar(def, campos, "Longitud", BuiltInParameter.STRUCTURAL_FRAME_CUT_LENGTH,
                        BuiltInParameter.INSTANCE_LENGTH_PARAM, BuiltInParameter.CURVE_ELEM_LENGTH);
            }

            if (concreto)
            {
                ScheduleField area = Agregar(def, campos, "Área", BuiltInParameter.HOST_AREA_COMPUTED);
                Agregar(def, campos, "Espesor",
                    BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM,
                    BuiltInParameter.WALL_ATTR_WIDTH_PARAM,
                    BuiltInParameter.STRUCTURAL_FOUNDATION_THICKNESS);
                ScheduleField volumen = Agregar(def, campos, "Volumen", BuiltInParameter.HOST_VOLUME_COMPUTED);
                Totales(longitud, area, volumen);
            }
            else if (cat.PesoPorVolumen)
            {
                // Conexiones, planchas y coberturas: sin longitud ni sección; el plugin escribe
                // en "Metrado - Peso (kg)" el volumen × densidad del acero.
                ScheduleField volumen = Agregar(def, campos, "Volumen", BuiltInParameter.HOST_VOLUME_COMPUTED);
                ScheduleField peso = AgregarPorNombre(def, campos, "Peso (kg)",
                    new string[0], new[] { ClasificadorElementos.NombreParametroPeso });
                Totales(volumen, peso);
                if (peso == null)
                {
                    Advertencias.Add($"Acero estructural {cat.Nombre}: no se encontró el parámetro \"" +
                                     ClasificadorElementos.NombreParametroPeso + "\"; la tabla no incluye la columna de peso.");
                }
            }
            else
            {
                // Los perfiles metálicos no se metran por volumen sino por peso:
                // longitud × área de sección × densidad, que el plugin escribe en "Metrado - Peso (kg)".
                ScheduleField areaSeccion = Agregar(def, campos, "Área de sección", BuiltInParameter.STRUCTURAL_SECTION_AREA)
                    ?? AgregarPorNombre(def, campos, "Área de sección", new string[0],
                        new[] { "Section Area", "Área de sección", "Area de seccion" });
                ScheduleField peso = AgregarPorNombre(def, campos, "Peso (kg)",
                    new string[0], new[] { ClasificadorElementos.NombreParametroPeso });
                Totales(longitud, peso);

                if (areaSeccion == null)
                {
                    Advertencias.Add($"Acero estructural {cat.Nombre}: la categoría no expone el área de sección en tablas; " +
                                     "la tabla solo muestra longitud y peso.");
                }
                if (peso == null)
                {
                    Advertencias.Add($"Acero estructural {cat.Nombre}: no se encontró el parámetro \"" +
                                     ClasificadorElementos.NombreParametroPeso + "\"; la tabla no incluye la columna de peso.");
                }
            }

            if (nivel != null)
            {
                def.AddSortGroupField(new ScheduleSortGroupField(nivel.FieldId)
                {
                    ShowHeader = true, ShowFooter = true, ShowFooterTitle = true, ShowBlankLine = true,
                });
            }
            if (tipo != null) def.AddSortGroupField(new ScheduleSortGroupField(tipo.FieldId));

            def.IsItemized = false;
            def.ShowGrandTotal = true;
            def.ShowGrandTotalTitle = true;
            def.ShowGrandTotalCount = true;
            def.GrandTotalTitle = (concreto ? "Total concreto " : "Total acero estructural ") + cat.Nombre;

            // Filtro principal: parámetro "Metrado - Material" (rellenado por el plugin).
            ScheduleField clasificacion = AgregarPorNombre(def, campos, "Clasificación",
                new string[0], new[] { ClasificadorElementos.NombreParametroMaterial });
            bool filtrado = false;
            if (clasificacion != null)
            {
                try
                {
                    def.AddFilter(new ScheduleFilter(clasificacion.FieldId,
                        concreto ? ScheduleFilterType.Equal : ScheduleFilterType.NotEqual,
                        ClasificadorElementos.ValorConcreto));
                    clasificacion.IsHidden = true;
                    filtrado = true;
                }
                catch (Exception ex)
                {
                    Advertencias.Add($"{cat.Nombre}: no se pudo filtrar por \"{ClasificadorElementos.NombreParametroMaterial}\" ({ex.Message}).");
                }
            }

            // Respaldo: filtros sobre el material estructural.
            if (!filtrado && material != null) AplicarFiltroMaterial(def, material, mats, concreto, cat.Nombre);

            return vs;
        }

        private bool HayElementosNoConcreto(CategoriaMetrado cat, MaterialesCategoria mats)
        {
            if (mats.NoConcreto.Count > 0) return true;
            var elementos = new FilteredElementCollector(_doc).OfCategory(cat.Categoria).WhereElementIsNotElementType().ToElements();
            foreach (Element e in elementos)
            {
                string v = e.LookupParameter(ClasificadorElementos.NombreParametroMaterial)?.AsString();
                if (!string.IsNullOrEmpty(v) && v != ClasificadorElementos.ValorConcreto) return true;
            }
            return false;
        }

        /// <summary>
        /// Deja en la tabla solo los elementos de concreto (o solo los que no lo son).
        /// Primero intenta un filtro de texto; si la API no lo admite, excluye uno a
        /// uno los materiales del otro grupo (hasta el máximo de filtros de Revit).
        /// </summary>
        private void AplicarFiltroMaterial(ScheduleDefinition def, ScheduleField material, MaterialesCategoria mats,
            bool concreto, string nombreCategoria)
        {
            string texto = (_op.TextoMaterialConcreto ?? string.Empty).Trim();

            if (_op.FiltrarPorMaterial && texto.Length > 0)
            {
                // Comprobar que el texto realmente distingue los materiales del modelo;
                // si no, se usa la clasificación por material directamente.
                bool textoSirve =
                    mats.Concreto.All(id => mats.Nombres[id].IndexOf(texto, StringComparison.OrdinalIgnoreCase) >= 0) &&
                    mats.NoConcreto.All(id => mats.Nombres[id].IndexOf(texto, StringComparison.OrdinalIgnoreCase) < 0);

                if (textoSirve)
                {
                    try
                    {
                        def.AddFilter(new ScheduleFilter(material.FieldId,
                            concreto ? ScheduleFilterType.Contains : ScheduleFilterType.NotContains, texto));
                        return;
                    }
                    catch (Exception)
                    {
                        // La API no admite el filtro de texto en este campo: usar ids.
                    }
                }
            }

            HashSet<ElementId> excluir = concreto ? mats.NoConcreto : mats.Concreto;
            if (excluir.Count == 0) return;

            int agregados = 0;
            foreach (ElementId id in excluir)
            {
                if (agregados >= MaxFiltros)
                {
                    Advertencias.Add($"{nombreCategoria}: hay más de {MaxFiltros} materiales distintos; " +
                                     "la tabla puede mezclar algunos materiales.");
                    break;
                }
                try
                {
                    def.AddFilter(new ScheduleFilter(material.FieldId, ScheduleFilterType.NotEqual, id));
                    agregados++;
                }
                catch (Exception ex)
                {
                    Advertencias.Add($"{nombreCategoria}: no se pudo excluir el material \"{mats.Nombres[id]}\" ({ex.Message}).");
                }
            }
        }

        // ------------------------------------------------------------------
        // Tablas de acero de refuerzo
        // ------------------------------------------------------------------

        /// <param name="cat">Categoría del anfitrión, o null para la tabla general.</param>
        private ViewSchedule CrearTablaRefuerzo(CategoriaMetrado cat, out bool filtrada)
        {
            filtrada = cat == null;

            ViewSchedule vs = ViewSchedule.CreateSchedule(_doc, new ElementId(BuiltInCategory.OST_Rebar));
            ScheduleDefinition def = vs.Definition;
            IList<SchedulableField> campos = def.GetSchedulableFields();

            // "Metrado - Elemento": tipo de anfitrión real (VIGAS, COLUMNAS, CIMIENTOS...) que el
            // plugin escribe en cada refuerzo. Es el filtro de las tablas por elemento y la
            // primera agrupación de la general: no depende de la partición, que el usuario
            // puede tener con sus propios textos ("Muro de contención", "Bloque A"...).
            ScheduleField elemento = AgregarPorNombre(def, campos, "Elemento",
                new string[0], new[] { ClasificadorElementos.NombreParametroElementoRefuerzo });

            ScheduleField particion = Agregar(def, campos, "Partición", BuiltInParameter.NUMBER_PARTITION_PARAM);

            // La categoría del anfitrión solo se usa como respaldo del filtro; no se muestra.
            ScheduleField hostCategoria = AgregarPorNombre(def, campos, "Categoría de anfitrión",
                new[] { "REBAR_HOST_CATEGORY", "REBAR_ELEM_HOST_CATEGORY" },
                new[] { "Host Category", "Categoría de anfitrión", "Categoría del anfitrión", "Categoría de host" });
            if (hostCategoria != null)
            {
                try { hostCategoria.IsHidden = true; } catch (Exception) { }
            }

            ScheduleField tipo = Agregar(def, campos, "Tipo de barra", BuiltInParameter.ELEM_TYPE_PARAM);
            ScheduleField diametro = Agregar(def, campos, "Diámetro", BuiltInParameter.REBAR_BAR_DIAMETER);
            ScheduleField cantidad = Agregar(def, campos, "N° barras", BuiltInParameter.REBAR_ELEM_QUANTITY_OF_BARS);
            ScheduleField longTotal = Agregar(def, campos, "Longitud total", BuiltInParameter.REBAR_ELEM_TOTAL_LENGTH);

            var nombresPeso = new List<string>();
            if (!string.IsNullOrWhiteSpace(_op.NombreParametroPeso)) nombresPeso.Add(_op.NombreParametroPeso.Trim());
            nombresPeso.AddRange(CalculadorMetrado.NombresParametroPesoBarra);
            ScheduleField pesoUnitario = AgregarPorNombre(def, campos, "Peso unitario",
                new[] { "REBAR_BAR_MASS_PER_UNIT_LENGTH" }, nombresPeso.ToArray());

            // Peso en kg: parámetro que el plugin rellena (longitud total × kg/m).
            ScheduleField peso = AgregarPorNombre(def, campos, "Peso (kg)",
                new string[0], new[] { ClasificadorElementos.NombreParametroPeso });

            Totales(cantidad, longTotal, peso);

            // Orden: elemento (solo en la general), partición (encabezado y pie con
            // totales), luego tipo de barra.
            if (cat == null && elemento != null)
            {
                def.AddSortGroupField(new ScheduleSortGroupField(elemento.FieldId)
                {
                    ShowHeader = true, ShowFooter = true, ShowFooterTitle = true, ShowBlankLine = true,
                });
            }
            if (particion != null)
            {
                def.AddSortGroupField(new ScheduleSortGroupField(particion.FieldId)
                {
                    ShowHeader = true, ShowFooter = true, ShowFooterTitle = true, ShowBlankLine = true,
                });
            }
            if (tipo != null) def.AddSortGroupField(new ScheduleSortGroupField(tipo.FieldId));

            def.IsItemized = false;
            def.ShowGrandTotal = true;
            def.ShowGrandTotalTitle = true;
            def.GrandTotalTitle = cat == null ? "Total acero" : "Total acero " + cat.Nombre;

            if (cat != null)
            {
                // 1) "Metrado - Elemento" = tipo de anfitrión (el plugin lo escribe siempre).
                if (elemento != null)
                {
                    try
                    {
                        def.AddFilter(new ScheduleFilter(elemento.FieldId, ScheduleFilterType.Equal, cat.NombreParticion));
                        filtrada = true;
                    }
                    catch (Exception) { }
                    try { elemento.IsHidden = true; } catch (Exception) { }
                }

                // 2) Partición = nombre de la categoría (el plugin la rellena si estaba vacía);
                //    si falla, por categoría del anfitrión.
                if (!filtrada && particion != null && _op.RellenarParticiones)
                {
                    try
                    {
                        def.AddFilter(new ScheduleFilter(particion.FieldId, ScheduleFilterType.Equal, cat.NombreParticion));
                        filtrada = true;
                    }
                    catch (Exception) { }
                }
                if (!filtrada && hostCategoria != null)
                {
                    filtrada = FiltrarPorCategoriaAnfitrion(def, hostCategoria, cat);
                }
            }

            if (pesoUnitario == null && cat == null)
            {
                Advertencias.Add("No se encontró el parámetro de peso unitario \"" + _op.NombreParametroPeso +
                                 "\" en los tipos de barra; el peso se calculó por diámetro y densidad.");
            }
            if (peso == null && cat == null)
            {
                Advertencias.Add("No se encontró el parámetro \"" + ClasificadorElementos.NombreParametroPeso +
                                 "\"; las tablas de acero no incluyen la columna de peso.");
            }

            return vs;
        }

        private bool FiltrarPorCategoriaAnfitrion(ScheduleDefinition def, ScheduleField hostCategoria, CategoriaMetrado cat)
        {
            Category categoria = Category.GetCategory(_doc, cat.Categoria);
            string nombre = categoria?.Name;

            // 1) El campo se comporta como texto.
            if (!string.IsNullOrEmpty(nombre))
            {
                try
                {
                    def.AddFilter(new ScheduleFilter(hostCategoria.FieldId, ScheduleFilterType.Equal, nombre));
                    return true;
                }
                catch (Exception) { }
            }

            // 2) El campo guarda el id de la categoría.
            try
            {
                def.AddFilter(new ScheduleFilter(hostCategoria.FieldId, ScheduleFilterType.Equal, new ElementId(cat.Categoria)));
                return true;
            }
            catch (Exception) { }

            // 3) Último intento: "contiene" con el nombre.
            if (!string.IsNullOrEmpty(nombre))
            {
                try
                {
                    def.AddFilter(new ScheduleFilter(hostCategoria.FieldId, ScheduleFilterType.Contains, nombre));
                    return true;
                }
                catch (Exception ex)
                {
                    Advertencias.Add($"Acero {cat.Nombre}: no se pudo filtrar por categoría del anfitrión ({ex.Message}).");
                }
            }
            return false;
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
                catch (Exception) { }
            }
            return null;
        }

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
