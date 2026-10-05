using System;
using System.Collections.Generic;
using System.Linq;
using Arba.Comun;
using Autodesk.Revit.DB;

namespace ExportacionMetrados.Core.Metrado
{
    /// <summary>
    /// Crea (o reutiliza) las tablas de planificación de metrado dentro del
    /// proyecto de Revit:
    ///   - "Metrado concreto - {elemento}"          elementos con material de concreto ("Otros":
    ///                                              una tabla por categoría de Revit con concreto)
    ///   - "Metrado acero estructural - {elemento}" perfiles y piezas metálicas por peso (si los hay;
    ///                                              "Otros": una tabla de varias categorías)
    ///   - "Metrado acero estructural - Misceláneos" elementos con "Metrado - Partida" (contrato ARBA):
    ///                                              varias categorías, agrupada por partida, kg y pernos
    ///   - "Metrado acero - {elemento}"             refuerzo cuyo anfitrión es de esa categoría
    ///   - "Metrado acero - General"                todo el refuerzo, por elemento y partición, con "ARBA - Código"
    /// Una tabla que ya existe se reutiliza, salvo que se pida regenerarla o que tenga una
    /// estructura de una versión anterior (agrupada por nivel cuando ya no toca, sin el
    /// filtro por "Metrado - Material" o filtrada por partición en vez de por
    /// "Metrado - Elemento", sin las columnas del contrato): entonces se crea de nuevo y se avisa.
    /// Todos los métodos deben llamarse dentro de una transacción abierta.
    /// </summary>
    public class GeneradorTablasRevit
    {
        public const string PrefijoConcreto = "Metrado concreto - ";
        public const string PrefijoAceroEstructural = "Metrado acero estructural - ";
        public const string PrefijoAcero = "Metrado acero - ";
        public const string NombreAceroGeneral = "Metrado acero - General";
        /// <summary>Tabla de misceláneos del contrato ARBA (elementos con "Metrado - Partida").</summary>
        public const string NombreMiscelaneos = PrefijoAceroEstructural + "Misceláneos";

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
        /// <summary>Elementos de cada grupo (según <see cref="ClasificadorElementos.GrupoDe"/>), calculados una sola vez.</summary>
        private readonly Dictionary<CategoriaMetrado, List<Element>> _elementosPorGrupo = new Dictionary<CategoriaMetrado, List<Element>>();

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

                if (!cat.TablaMulticategoria)
                {
                    ViewSchedule t = CrearOReutilizar(PrefijoConcreto + cat.Nombre,
                        () => CrearTablaElementos(cat, new ElementId(cat.Categoria), mats, concreto: true),
                        existente => MotivoTablaElementosDesactualizada(existente, cat));
                    if (t != null) tablas.Add(t);
                }
                else
                {
                    // "Otros" y conexiones: las tablas de varias categorías no exponen el volumen, así que
                    // el concreto va en una tabla por cada categoría de Revit que tenga elementos de concreto.
                    foreach (BuiltInCategory bic in cat.Categorias)
                    {
                        if (!HayElementosDelGrupo(cat, bic, ClasificadorElementos.ValorConcreto)) continue;
                        string nombreCategoria = Category.GetCategory(_doc, bic)?.Name ?? bic.ToString();
                        ViewSchedule t = CrearOReutilizar(PrefijoConcreto + cat.Nombre + " - " + nombreCategoria,
                            () => CrearTablaElementos(cat, new ElementId(bic), mats, concreto: true),
                            existente => MotivoTablaElementosDesactualizada(existente, cat));
                        if (t != null) tablas.Add(t);
                    }
                }

                if (cat.EsMiscelaneos)
                {
                    // Misceláneos (contrato ARBA): tabla de varias categorías agrupada por partida, con kg y pernos.
                    if (HayMiscelaneos(cat))
                    {
                        ViewSchedule m = CrearOReutilizar(PrefijoAceroEstructural + cat.Nombre,
                            () => CrearTablaMiscelaneos(cat),
                            existente => MotivoTablaMiscelaneosDesactualizada(existente, cat));
                        if (m != null) tablas.Add(m);
                    }
                    continue;
                }

                // "Otros" y "Conexiones y anclajes" (piezas sin longitud ni sección) siempre van a su
                // tabla de acero estructural, de varias categorías, filtrada por "Metrado - Elemento".
                if ((_op.TablasAceroEstructural || cat.TablaMulticategoria) && cat.PuedeSerMetalica && HayElementosNoConcreto(cat, mats))
                {
                    ElementId categoriaTabla = cat.TablaMulticategoria ? ElementId.InvalidElementId : new ElementId(cat.Categoria);
                    ViewSchedule m = CrearOReutilizar(PrefijoAceroEstructural + cat.Nombre,
                        () => CrearTablaElementos(cat, categoriaTabla, mats, concreto: false),
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
                    if (!cat.AlojaRefuerzo) continue;

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

            // Toda tabla de elementos filtra por su grupo en "Metrado - Elemento": así las piezas
            // de conexión que vienen como vigas no salen en la tabla de vigas, y las tablas de
            // varias categorías no mezclan los perfiles de vigas y columnas.
            ElementId idElemento = ClasificadorElementos.IdParametroElementoRefuerzo(_doc);
            if (idElemento != null && !TieneFiltroPorParametro(def, idElemento))
            {
                return "sin filtro por \"" + ClasificadorElementos.NombreParametroElementoRefuerzo + "\"";
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
            if (cat == null)
            {
                // La general muestra "ARBA - Código" (capa / familia del add-in que armó) desde el contrato ARBA.
                ElementId idCodigo = ClasificadorElementos.IdParametroCodigo(_doc);
                if (idCodigo != null && !TieneCampoDeParametro(def, idCodigo))
                {
                    return "sin la columna \"" + ClasificadorElementos.NombreParametroCodigo + "\"";
                }
            }
            return null;
        }

        /// <summary>
        /// Motivo por el que la tabla de misceláneos ya no corresponde a la estructura actual
        /// (sin el filtro por "Metrado - Elemento" o sin las columnas del contrato), o null si sirve.
        /// </summary>
        private string MotivoTablaMiscelaneosDesactualizada(ViewSchedule tabla, CategoriaMetrado cat)
        {
            ScheduleDefinition def = tabla.Definition;

            ElementId idElemento = ClasificadorElementos.IdParametroElementoRefuerzo(_doc);
            if (idElemento != null && !TieneFiltroPorParametro(def, idElemento))
            {
                return "sin filtro por \"" + ClasificadorElementos.NombreParametroElementoRefuerzo + "\"";
            }
            ElementId idPartida = ClasificadorElementos.IdParametroPartida(_doc);
            if (idPartida != null && !TieneCampoDeParametro(def, idPartida))
            {
                return "sin la columna \"" + ClasificadorElementos.NombreParametroPartida + "\"";
            }
            ElementId idPernos = ClasificadorElementos.IdParametroPernos(_doc);
            if (idPernos != null && !TieneCampoDeParametro(def, idPernos))
            {
                return "sin la columna \"" + ClasificadorElementos.NombreParametroPernos + "\"";
            }
            ElementId idPeso = ClasificadorElementos.IdParametroPeso(_doc);
            if (idPeso != null && !TieneCampoDeParametro(def, idPeso))
            {
                return "sin la columna \"" + ClasificadorElementos.NombreParametroPeso + "\"";
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

        /// <summary>Elementos del grupo (una sola vez por grupo).</summary>
        private List<Element> ElementosDe(CategoriaMetrado cat)
        {
            if (!_elementosPorGrupo.TryGetValue(cat, out List<Element> lista))
            {
                lista = ClasificadorElementos.ElementosDelGrupo(_doc, cat, _op.Categorias);
                _elementosPorGrupo[cat] = lista;
            }
            return lista;
        }

        private MaterialesCategoria ClasificarMateriales(CategoriaMetrado cat)
        {
            var r = new MaterialesCategoria();
            List<Element> elementos = ElementosDe(cat);

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

        /// <param name="categoriaTabla">Categoría de la tabla, o InvalidElementId para una de varias categorías ("Otros").</param>
        private ViewSchedule CrearTablaElementos(CategoriaMetrado cat, ElementId categoriaTabla, MaterialesCategoria mats, bool concreto)
        {
            bool variasCategorias = categoriaTabla == ElementId.InvalidElementId;
            ViewSchedule vs = ViewSchedule.CreateSchedule(_doc, categoriaTabla);
            ScheduleDefinition def = vs.Definition;
            IList<SchedulableField> campos = def.GetSchedulableFields();

            // En la tabla de varias categorías, la categoría de Revit es la primera agrupación.
            ScheduleField categoria = variasCategorias ? Agregar(def, campos, "Categoría", BuiltInParameter.ELEM_CATEGORY_PARAM) : null;

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
                ScheduleField peso = AgregarCompartido(def, campos, "Peso (kg)", ArbaContract.Peso);
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
                ScheduleField peso = AgregarCompartido(def, campos, "Peso (kg)", ArbaContract.Peso);
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

            if (categoria != null)
            {
                def.AddSortGroupField(new ScheduleSortGroupField(categoria.FieldId)
                {
                    ShowHeader = true, ShowFooter = true, ShowFooterTitle = true, ShowBlankLine = true,
                });
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
            ScheduleField clasificacion = AgregarCompartido(def, campos, "Clasificación", ArbaContract.Material);
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

            // Solo los elementos del grupo ("Metrado - Elemento" = VIGAS, CONEXIONES, OTROS...):
            // las piezas de conexión que vienen como vigas no salen en la tabla de vigas, y las
            // tablas de varias categorías no mezclan los perfiles de vigas y columnas.
            ScheduleField elemento = AgregarCompartido(def, campos, "Grupo", ArbaContract.Elemento);
            bool porGrupo = false;
            if (elemento != null)
            {
                try
                {
                    def.AddFilter(new ScheduleFilter(elemento.FieldId, ScheduleFilterType.Equal, cat.NombreParticion));
                    elemento.IsHidden = true;
                    porGrupo = true;
                }
                catch (Exception) { }
            }
            if (!porGrupo)
            {
                Advertencias.Add($"{cat.Nombre}: no se pudo filtrar por \"{ClasificadorElementos.NombreParametroElementoRefuerzo}\"; " +
                                 (variasCategorias ? "la tabla puede incluir perfiles de vigas y columnas."
                                                   : "las piezas de conexión de esa categoría saldrán en esta tabla."));
            }

            return vs;
        }

        /// <summary>True si algún elemento del grupo, de esa categoría de Revit, tiene "Metrado - Material" igual al valor.</summary>
        private bool HayElementosDelGrupo(CategoriaMetrado cat, BuiltInCategory bic, string valor)
        {
            var idCategoria = new ElementId(bic);
            try
            {
                foreach (Element e in ElementosDe(cat))
                {
                    if (e.Category == null || e.Category.Id != idCategoria) continue;
                    string v = ArbaSharedParams.GetText(e, ArbaContract.Material);
                    if (string.Equals(v, valor, StringComparison.Ordinal)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// True si el grupo tiene algo que mostrar en su tabla de acero estructural: algún
        /// elemento clasificado distinto de CONCRETO (en "Otros", clasificado ACERO
        /// ESTRUCTURAL: los modelos genéricos sin datos quedan como OTRO y no cuentan).
        /// </summary>
        private bool HayElementosNoConcreto(CategoriaMetrado cat, MaterialesCategoria mats)
        {
            foreach (Element e in ElementosDe(cat))
            {
                string v = ArbaSharedParams.GetText(e, ArbaContract.Material);
                if (string.IsNullOrEmpty(v)) continue;
                if (cat.TablaMulticategoria ? v == ClasificadorElementos.ValorAceroEstructural : v != ClasificadorElementos.ValorConcreto) return true;
            }
            return !cat.TablaMulticategoria && mats.NoConcreto.Count > 0;
        }

        /// <summary>
        /// True si el grupo "Misceláneos" tiene algo que mostrar: algún elemento con "Metrado - Partida"
        /// que no sea de concreto o cuyo peso ya escribió su add-in ARBA.
        /// </summary>
        private bool HayMiscelaneos(CategoriaMetrado cat)
        {
            foreach (Element e in ElementosDe(cat))
            {
                if (ClasificadorElementos.PesoProtegido(e)) return true;
                if (ClasificadorElementos.ClasificacionActual(_doc, e) != ClasificadorElementos.ValorConcreto) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // Tabla de misceláneos (contrato ARBA-comun)
        // ------------------------------------------------------------------

        /// <summary>
        /// "Metrado acero estructural - Misceláneos": tabla de varias categorías con los elementos
        /// cuyo "Metrado - Elemento" es MISCELANEOS (los que tienen "Metrado - Partida": rejillas,
        /// ángulos...), agrupada por partida (encabezado y pie con totales), luego categoría de
        /// Revit y tipo. Columnas: Partida, Categoría, Elemento, ARBA - Código, Cantidad,
        /// Peso (kg) y Pernos (und) con totales.
        /// </summary>
        private ViewSchedule CrearTablaMiscelaneos(CategoriaMetrado cat)
        {
            ViewSchedule vs = ViewSchedule.CreateSchedule(_doc, ElementId.InvalidElementId);
            ScheduleDefinition def = vs.Definition;
            IList<SchedulableField> campos = def.GetSchedulableFields();

            ScheduleField partida = AgregarCompartido(def, campos, "Partida", ArbaContract.Partida);
            ScheduleField categoria = Agregar(def, campos, "Categoría", BuiltInParameter.ELEM_CATEGORY_PARAM);
            ScheduleField tipo = Agregar(def, campos, "Elemento", BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM);
            ScheduleField codigo = AgregarCompartido(def, campos, "Código", ArbaContract.Codigo);

            try
            {
                ScheduleField cantidad = def.AddField(ScheduleFieldType.Count);
                cantidad.ColumnHeading = "Cantidad";
            }
            catch (Exception ex) { Advertencias.Add($"{cat.Nombre}: sin campo Cantidad ({ex.Message})"); }

            ScheduleField peso = AgregarCompartido(def, campos, "Peso (kg)", ArbaContract.Peso);
            ScheduleField pernos = AgregarCompartido(def, campos, "Pernos (und)", ArbaContract.Pernos);
            Totales(peso, pernos);

            if (partida != null)
            {
                def.AddSortGroupField(new ScheduleSortGroupField(partida.FieldId)
                {
                    ShowHeader = true, ShowFooter = true, ShowFooterTitle = true, ShowBlankLine = true,
                });
            }
            else
            {
                Advertencias.Add($"{cat.Nombre}: no se encontró el parámetro \"{ClasificadorElementos.NombreParametroPartida}\"; " +
                                 "la tabla no se agrupa por partida.");
            }
            if (categoria != null) def.AddSortGroupField(new ScheduleSortGroupField(categoria.FieldId));
            if (tipo != null) def.AddSortGroupField(new ScheduleSortGroupField(tipo.FieldId));

            def.IsItemized = false;
            def.ShowGrandTotal = true;
            def.ShowGrandTotalTitle = true;
            def.ShowGrandTotalCount = true;
            def.GrandTotalTitle = "Total " + cat.Nombre.ToLowerInvariant();

            // Filtro: "Metrado - Elemento" = MISCELANEOS (lo escribe el plugin en todo elemento con partida).
            ScheduleField elemento = AgregarCompartido(def, campos, "Grupo", ArbaContract.Elemento);
            bool filtrada = false;
            if (elemento != null)
            {
                try
                {
                    def.AddFilter(new ScheduleFilter(elemento.FieldId, ScheduleFilterType.Equal, cat.NombreParticion));
                    elemento.IsHidden = true;
                    filtrada = true;
                }
                catch (Exception) { }
            }
            if (!filtrada && partida != null)
            {
                // Respaldo: cualquier elemento con partida.
                try
                {
                    def.AddFilter(new ScheduleFilter(partida.FieldId, ScheduleFilterType.HasValue));
                    filtrada = true;
                }
                catch (Exception) { }
            }
            if (!filtrada)
            {
                Advertencias.Add($"{cat.Nombre}: no se pudo filtrar por \"{ClasificadorElementos.NombreParametroElementoRefuerzo}\"; " +
                                 "la tabla puede incluir elementos de otros grupos.");
            }

            if (codigo == null) Advertencias.Add($"{cat.Nombre}: no se encontró el parámetro \"{ClasificadorElementos.NombreParametroCodigo}\".");
            if (peso == null) Advertencias.Add($"{cat.Nombre}: no se encontró el parámetro \"{ClasificadorElementos.NombreParametroPeso}\"; la tabla no incluye la columna de peso.");
            if (pernos == null) Advertencias.Add($"{cat.Nombre}: no se encontró el parámetro \"{ClasificadorElementos.NombreParametroPernos}\"; la tabla no incluye la columna de pernos.");

            return vs;
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
            ScheduleField elemento = AgregarCompartido(def, campos, "Elemento", ArbaContract.Elemento);

            ScheduleField particion = Agregar(def, campos, "Partición", BuiltInParameter.NUMBER_PARTITION_PARAM);

            // "ARBA - Código" (capa o familia propia del add-in que armó: inferior, estribo, F1...): columna
            // informativa del contrato ARBA, solo en la tabla general.
            if (cat == null) AgregarCompartido(def, campos, "Código", ArbaContract.Codigo);

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
            ScheduleField peso = AgregarCompartido(def, campos, "Peso (kg)", ArbaContract.Peso);

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

                // 2) Partición empieza por "CATEGORIA - " (forma del contrato ARBA: "VIGAS - MAN-V1",
                //    "CIMIENTOS - ZAP-Z1"...; el plugin la rellena si estaba vacía); si falla, por
                //    categoría del anfitrión.
                if (!filtrada && particion != null && _op.RellenarParticiones)
                {
                    try
                    {
                        def.AddFilter(new ScheduleFilter(particion.FieldId, ScheduleFilterType.BeginsWith,
                            ArbaPartition.FilterPrefix(cat.NombreParticion)));
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
        // Tablas desde los parámetros (valores propios de "Metrado - Material" / "Metrado - Elemento")
        // ------------------------------------------------------------------

        /// <summary>
        /// Crea una tabla por cada combinación de valores de "Metrado - Material" y "Metrado - Elemento"
        /// indicada (las lee <see cref="LectorCombinaciones"/> del modelo, incluidos los textos que el
        /// usuario haya escrito a mano, p. ej. "ESCALERAS"), filtrada por esos valores exactos y sin
        /// escribir ningún parámetro. En los elementos, una tabla por cada categoría de Revit de la
        /// combinación (una tabla de varias categorías no expone volumen ni longitud): "Metrado
        /// {material} - {elemento}" y, si hay varias categorías, "... - {categoría}". En el refuerzo,
        /// "Metrado acero - {elemento}" con la misma estructura que las tablas de acero por elemento.
        /// Las tablas existentes con el mismo nombre se reutilizan salvo que se pida regenerarlas.
        /// Debe llamarse dentro de una transacción abierta.
        /// </summary>
        public List<ViewSchedule> GenerarDesdeParametros(IEnumerable<CombinacionMetrado> combinaciones)
        {
            var tablas = new List<ViewSchedule>();
            if (combinaciones == null) return tablas;

            foreach (CombinacionMetrado c in combinaciones)
            {
                if (c.Tipo == TipoCombinacion.Refuerzo)
                {
                    if (c.Elemento.Length == 0) continue;
                    ViewSchedule r = CrearOReutilizar(c.NombreTabla, () => CrearTablaRefuerzoPorValor(c.Elemento));
                    if (r != null) tablas.Add(r);
                    continue;
                }

                bool variasCategorias = c.Categorias.Count > 1;
                foreach (BuiltInCategory bic in c.Categorias.Keys.ToList())
                {
                    string nombre = variasCategorias
                        ? c.NombreTabla + " - " + LectorCombinaciones.NombreCategoria(_doc, bic)
                        : c.NombreTabla;
                    ViewSchedule t = CrearOReutilizar(nombre, () => CrearTablaPorValores(c, bic));
                    if (t != null) tablas.Add(t);
                }
            }
            return tablas;
        }

        /// <summary>True si el texto de "Metrado - Material" habla de concreto (la tabla muestra volumen en vez de peso).</summary>
        private static bool EsTextoDeConcreto(string material)
        {
            string m = material ?? string.Empty;
            return m.IndexOf("CONCRETO", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   m.IndexOf("HORMIG", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   m.IndexOf("CONCRETE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Tabla de una categoría de Revit con los elementos cuyos "Metrado - Material" y
        /// "Metrado - Elemento" son exactamente los de la combinación (un valor vacío se filtra como
        /// "sin valor"). Columnas de concreto (Longitud, Área, Espesor, Volumen) si el material habla
        /// de concreto; si no, Longitud, Área de sección, Volumen y Peso (kg). Columnas y muros se
        /// agrupan por nivel, como en las tablas predeterminadas.
        /// </summary>
        private ViewSchedule CrearTablaPorValores(CombinacionMetrado c, BuiltInCategory bic)
        {
            bool concreto = EsTextoDeConcreto(c.Material);
            bool porNivel = bic == BuiltInCategory.OST_StructuralColumns || bic == BuiltInCategory.OST_Walls;
            string descripcion = c.NombreTabla;

            ViewSchedule vs = ViewSchedule.CreateSchedule(_doc, new ElementId(bic));
            ScheduleDefinition def = vs.Definition;
            IList<SchedulableField> campos = def.GetSchedulableFields();

            ScheduleField nivel = !porNivel ? null : Agregar(def, campos, "Nivel",
                BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
                BuiltInParameter.WALL_BASE_CONSTRAINT,
                BuiltInParameter.LEVEL_PARAM,
                BuiltInParameter.SCHEDULE_LEVEL_PARAM);
            ScheduleField tipo = Agregar(def, campos, "Elemento", BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM);
            Agregar(def, campos, "Material", BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);

            try
            {
                ScheduleField cantidad = def.AddField(ScheduleFieldType.Count);
                cantidad.ColumnHeading = "Cantidad";
            }
            catch (Exception ex) { Advertencias.Add($"{descripcion}: sin campo Cantidad ({ex.Message})"); }

            ScheduleField longitud = concreto
                ? Agregar(def, campos, "Longitud", BuiltInParameter.INSTANCE_LENGTH_PARAM, BuiltInParameter.CURVE_ELEM_LENGTH)
                : Agregar(def, campos, "Longitud", BuiltInParameter.STRUCTURAL_FRAME_CUT_LENGTH,
                    BuiltInParameter.INSTANCE_LENGTH_PARAM, BuiltInParameter.CURVE_ELEM_LENGTH);

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
            else
            {
                Agregar(def, campos, "Área de sección", BuiltInParameter.STRUCTURAL_SECTION_AREA);
                ScheduleField volumen = Agregar(def, campos, "Volumen", BuiltInParameter.HOST_VOLUME_COMPUTED);
                ScheduleField peso = AgregarCompartido(def, campos, "Peso (kg)", ArbaContract.Peso);
                Totales(longitud, volumen, peso);
                if (peso == null)
                {
                    Advertencias.Add($"{descripcion}: la categoría no tiene el parámetro \"{ClasificadorElementos.NombreParametroPeso}\"; " +
                                     "la tabla no incluye la columna de peso.");
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
            def.GrandTotalTitle = "Total " + descripcion.Substring("Metrado ".Length);

            // Filtros por los valores exactos de los dos parámetros (campos ocultos).
            bool filtrado = FiltrarPorValor(def, AgregarCompartido(def, campos, "Clasificación", ArbaContract.Material), c.Material);
            if (!filtrado)
            {
                Advertencias.Add($"{descripcion}: no se pudo filtrar por \"{ClasificadorElementos.NombreParametroMaterial}\"; " +
                                 "la tabla puede incluir elementos de otros materiales.");
            }
            filtrado = FiltrarPorValor(def, AgregarCompartido(def, campos, "Grupo", ArbaContract.Elemento), c.Elemento);
            if (!filtrado)
            {
                Advertencias.Add($"{descripcion}: no se pudo filtrar por \"{ClasificadorElementos.NombreParametroElementoRefuerzo}\"; " +
                                 "la tabla puede incluir elementos de otros grupos.");
            }

            return vs;
        }

        /// <summary>
        /// Filtra la tabla por el campo = valor (o "sin valor" si el texto está vacío) y oculta el
        /// campo. Devuelve false si el campo no existe o Revit rechaza el filtro.
        /// </summary>
        private static bool FiltrarPorValor(ScheduleDefinition def, ScheduleField campo, string valor)
        {
            if (campo == null) return false;
            try
            {
                def.AddFilter(string.IsNullOrEmpty(valor)
                    ? new ScheduleFilter(campo.FieldId, ScheduleFilterType.HasNoValue)
                    : new ScheduleFilter(campo.FieldId, ScheduleFilterType.Equal, valor));
            }
            catch (Exception)
            {
                return false;
            }
            try { campo.IsHidden = true; } catch (Exception) { }
            return true;
        }

        /// <summary>
        /// Tabla de armaduras cuyo "Metrado - Elemento" es exactamente <paramref name="elemento"/>
        /// (p. ej. un texto propio como "ESCALERAS"), con la estructura de las tablas de acero por
        /// elemento: Partición (encabezado y pie con totales), Tipo de barra, Diámetro, N° barras,
        /// Longitud total, Peso unitario y Peso (kg). Si no se puede filtrar por ese parámetro la
        /// tabla no tiene sentido: se borra y se lanza una excepción (la recoge
        /// <see cref="CrearOReutilizar"/> como advertencia).
        /// </summary>
        private ViewSchedule CrearTablaRefuerzoPorValor(string elemento)
        {
            ViewSchedule vs = ViewSchedule.CreateSchedule(_doc, new ElementId(BuiltInCategory.OST_Rebar));
            ScheduleDefinition def = vs.Definition;
            IList<SchedulableField> campos = def.GetSchedulableFields();

            ScheduleField campoElemento = AgregarCompartido(def, campos, "Elemento", ArbaContract.Elemento);
            ScheduleField particion = Agregar(def, campos, "Partición", BuiltInParameter.NUMBER_PARTITION_PARAM);
            ScheduleField tipo = Agregar(def, campos, "Tipo de barra", BuiltInParameter.ELEM_TYPE_PARAM);
            Agregar(def, campos, "Diámetro", BuiltInParameter.REBAR_BAR_DIAMETER);
            ScheduleField cantidad = Agregar(def, campos, "N° barras", BuiltInParameter.REBAR_ELEM_QUANTITY_OF_BARS);
            ScheduleField longTotal = Agregar(def, campos, "Longitud total", BuiltInParameter.REBAR_ELEM_TOTAL_LENGTH);

            var nombresPeso = new List<string>();
            if (!string.IsNullOrWhiteSpace(_op.NombreParametroPeso)) nombresPeso.Add(_op.NombreParametroPeso.Trim());
            nombresPeso.AddRange(CalculadorMetrado.NombresParametroPesoBarra);
            AgregarPorNombre(def, campos, "Peso unitario", new[] { "REBAR_BAR_MASS_PER_UNIT_LENGTH" }, nombresPeso.ToArray());

            ScheduleField peso = AgregarCompartido(def, campos, "Peso (kg)", ArbaContract.Peso);
            Totales(cantidad, longTotal, peso);

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
            def.GrandTotalTitle = "Total acero " + elemento;

            if (!FiltrarPorValor(def, campoElemento, elemento))
            {
                _doc.Delete(vs.Id);
                throw new InvalidOperationException("no se pudo filtrar por \"" + ClasificadorElementos.NombreParametroElementoRefuerzo + "\"");
            }

            if (peso == null)
            {
                Advertencias.Add($"{PrefijoAcero}{elemento}: no se encontró el parámetro \"{ClasificadorElementos.NombreParametroPeso}\"; " +
                                 "la tabla no incluye la columna de peso.");
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
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>
        /// Campo de un parámetro compartido del contrato ARBA: se busca por su Id en el proyecto
        /// (GUID fijo) y, si no aparece así, por su nombre visible.
        /// </summary>
        private ScheduleField AgregarCompartido(ScheduleDefinition def, IList<SchedulableField> campos, string encabezado, ArbaParam parametro)
        {
            ElementId id = ArbaSharedParams.IdOf(_doc, parametro);
            if (id != null)
            {
                SchedulableField sf = campos.FirstOrDefault(c => c.ParameterId == id);
                if (sf != null)
                {
                    try
                    {
                        ScheduleField f = def.AddField(sf);
                        f.ColumnHeading = encabezado;
                        return f;
                    }
                    catch (Exception) { }
                }
            }
            return AgregarPorNombre(def, campos, encabezado, new string[0], new[] { parametro.Name });
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
