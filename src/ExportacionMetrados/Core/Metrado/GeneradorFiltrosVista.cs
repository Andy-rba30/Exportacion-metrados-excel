using System;
using System.Collections.Generic;
using System.Linq;
using Arba.Comun;
using Autodesk.Revit.DB;

namespace ExportacionMetrados.Core.Metrado
{
    /// <summary>
    /// Crea filtros de vista (Visibilidad/Gráficos) con un color por tipo de elemento
    /// metrado, para comprobar a simple vista qué entra en cada tabla:
    ///   - "Metrado - Concreto - {elemento}"           categoría + "Metrado - Material" = CONCRETO
    ///   - "Metrado - Acero estructural - {elemento}"  categoría + "Metrado - Material" = ACERO ESTRUCTURAL
    ///                                                 (+ "Metrado - Elemento" = grupo)
    ///   - "Metrado - Acero estructural - Misceláneos" "Metrado - Elemento" = MISCELANEOS (contrato ARBA:
    ///                                                 elementos con "Metrado - Partida")
    ///   - "Metrado - Refuerzo - {ELEMENTO}"           armaduras y mallas cuyo "Metrado - Elemento"
    ///                                                 (tipo de anfitrión) es ese; si el parámetro
    ///                                                 no existe, por partición "empieza por
    ///                                                 ELEMENTO - " (forma del contrato ARBA)
    /// Los filtros quedan en el proyecto (se pueden usar en cualquier vista desde
    /// Visibilidad/Gráficos) y, si se pasa una vista, se aplican a ella con color de
    /// línea y relleno sólido (<see cref="ElegirVista"/> elige una que los admita). Debe
    /// llamarse dentro de una transacción abierta, después de rellenar "Metrado - Material",
    /// "Metrado - Elemento" y las particiones.
    /// </summary>
    public class GeneradorFiltrosVista
    {
        public const string PrefijoConcreto = "Metrado - Concreto - ";
        public const string PrefijoAceroEstructural = "Metrado - Acero estructural - ";
        public const string PrefijoRefuerzo = "Metrado - Refuerzo - ";

        private readonly Document _doc;
        private readonly OpcionesMetrado _op;

        public List<string> Advertencias { get; } = new List<string>();
        public List<ParameterFilterElement> FiltrosCreados { get; } = new List<ParameterFilterElement>();
        public List<ParameterFilterElement> FiltrosReutilizados { get; } = new List<ParameterFilterElement>();
        /// <summary>Número de filtros aplicados a la vista indicada.</summary>
        public int FiltrosAplicados { get; private set; }
        /// <summary>Nombre de la vista a la que se aplicaron los filtros (null si no se aplicaron).</summary>
        public string VistaAplicada { get; private set; }

        // Paleta por tipo de elemento. Concreto, perfiles y refuerzo usan familias de
        // colores distintas para que no se confundan en la misma vista.
        private static readonly Dictionary<string, Color> ColoresConcreto = new Dictionary<string, Color>
        {
            { "Vigas",         new Color(0, 112, 192) },   // azul
            { "Columnas",      new Color(192, 0, 0) },     // rojo
            { "Cimentaciones", new Color(140, 90, 30) },   // marrón
            { "Losas",         new Color(0, 150, 70) },    // verde
            { "Muros",         new Color(112, 48, 160) },  // morado
            { "Otros",         new Color(120, 120, 200) }, // lavanda
        };
        private static readonly Dictionary<string, Color> ColoresAceroEstructural = new Dictionary<string, Color>
        {
            { "Vigas",      new Color(0, 190, 240) },      // celeste
            { "Columnas",   new Color(255, 0, 255) },      // magenta
            { "Otros",                 new Color(255, 230, 0) },  // amarillo
            { "Conexiones y anclajes", new Color(0, 255, 0) },    // verde vivo
            { "Misceláneos",           new Color(185, 110, 255) }, // lila
        };
        private static readonly Dictionary<string, Color> ColoresRefuerzo = new Dictionary<string, Color>
        {
            { "VIGAS",     new Color(255, 140, 0) },       // naranja
            { "COLUMNAS",  new Color(255, 60, 140) },      // rosado
            { "CIMIENTOS", new Color(200, 160, 0) },       // dorado
            { "LOSAS",     new Color(150, 200, 0) },       // lima
            { "MUROS",     new Color(0, 190, 190) },       // turquesa
        };
        private static readonly Color ColorPorDefecto = new Color(128, 128, 128);

        public GeneradorFiltrosVista(Document doc, OpcionesMetrado opciones)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _op = opciones ?? throw new ArgumentNullException(nameof(opciones));
        }

        /// <summary>
        /// Crea o actualiza los filtros de las categorías seleccionadas y, si
        /// <paramref name="vista"/> admite sustituciones gráficas, los aplica a ella.
        /// </summary>
        public void Generar(View vista)
        {
            var categorias = _op.Categorias.Where(c => c.Seleccionada).ToList();
            var filtros = new List<KeyValuePair<ParameterFilterElement, Color>>();

            ElementId idMaterial = ClasificadorElementos.IdParametroMaterial(_doc);
            // "Metrado - Elemento" = grupo: las piezas de conexión que vienen como vigas se pintan
            // como conexiones y los misceláneos del contrato ARBA tienen su propio filtro.
            ElementId idGrupo = ClasificadorElementos.IdParametroElementoRefuerzo(_doc);

            if (idMaterial == null)
            {
                Advertencias.Add("No existe el parámetro \"Metrado - Material\"; no se crearon los filtros de concreto ni de acero estructural.");
            }
            else
            {
                var categoriasMetalicas = categorias.Where(c => c.PuedeSerMetalica).SelectMany(c => c.Categorias).Distinct().ToList();

                foreach (CategoriaMetrado cat in categorias)
                {
                    if (cat.EsMiscelaneos) continue;

                    if (!cat.EsConexiones)
                    {
                        Agregar(filtros, Crear(PrefijoConcreto + cat.Nombre, cat.Categorias, idMaterial, ClasificadorElementos.ValorConcreto),
                            ColorDe(ColoresConcreto, cat.Nombre));
                    }

                    if (cat.PuedeSerMetalica)
                    {
                        IEnumerable<BuiltInCategory> cats = cat.EsConexiones ? categoriasMetalicas : cat.Categorias;
                        Agregar(filtros, Crear(PrefijoAceroEstructural + cat.Nombre, cats, idMaterial, ClasificadorElementos.ValorAceroEstructural,
                                idGrupo, cat.NombreParticion),
                            ColorDe(ColoresAceroEstructural, cat.Nombre));
                    }
                }
            }

            // Misceláneos (contrato ARBA): solo "Metrado - Elemento" = MISCELANEOS, sea cual sea su material.
            foreach (CategoriaMetrado cat in categorias.Where(c => c.EsMiscelaneos))
            {
                if (idGrupo == null)
                {
                    Advertencias.Add("No existe el parámetro \"" + ClasificadorElementos.NombreParametroElementoRefuerzo +
                                     "\"; no se creó el filtro de misceláneos.");
                    continue;
                }
                Agregar(filtros, Crear(PrefijoAceroEstructural + cat.Nombre, cat.Categorias, idGrupo, cat.NombreParticion),
                    ColorDe(ColoresAceroEstructural, cat.Nombre));
            }

            if (_op.IncluirAcero)
            {
                // "Metrado - Elemento" lo escribe el plugin según el anfitrión real de cada
                // barra; la partición solo se usa si ese parámetro no se pudo crear: entonces
                // por la forma del contrato ARBA, "empieza por 'ELEMENTO - '" (VIGAS - MAN-V1,
                // VIGAS - VIG-V1...).
                ElementId idRefuerzo = ClasificadorElementos.IdParametroElementoRefuerzo(_doc);
                bool porParticion = idRefuerzo == null;
                if (porParticion)
                {
                    idRefuerzo = new ElementId(BuiltInParameter.NUMBER_PARTITION_PARAM);
                    Advertencias.Add("No existe el parámetro \"" + ClasificadorElementos.NombreParametroElementoRefuerzo +
                                     "\"; los filtros de refuerzo se crearon por partición (\"empieza por ELEMENTO - \").");
                }
                foreach (CategoriaMetrado cat in categorias)
                {
                    if (!cat.AlojaRefuerzo) continue;
                    ParameterFilterElement filtro = porParticion
                        ? Crear(PrefijoRefuerzo + cat.NombreParticion, ClasificadorElementos.CategoriasRefuerzo, idRefuerzo,
                            new List<FilterRule> { ArbaPartition.CategoryRule(cat.NombreParticion) })
                        : Crear(PrefijoRefuerzo + cat.NombreParticion, ClasificadorElementos.CategoriasRefuerzo, idRefuerzo, cat.NombreParticion);
                    Agregar(filtros, filtro, ColorDe(ColoresRefuerzo, cat.NombreParticion));
                }
            }

            if (vista != null && filtros.Count > 0) Aplicar(vista, filtros);
        }

        private static void Agregar(List<KeyValuePair<ParameterFilterElement, Color>> lista, ParameterFilterElement filtro, Color color)
        {
            if (filtro != null) lista.Add(new KeyValuePair<ParameterFilterElement, Color>(filtro, color));
        }

        private static Color ColorDe(Dictionary<string, Color> paleta, string clave) =>
            clave != null && paleta.TryGetValue(clave, out Color c) ? c : ColorPorDefecto;

        // ------------------------------------------------------------------
        // Filtros
        // ------------------------------------------------------------------

        /// <summary>
        /// Crea el filtro "{nombre}" (o actualiza el existente con ese nombre) con la
        /// regla parámetro = valor sobre las categorías dadas. Devuelve null si el
        /// parámetro no admite filtros en ninguna de ellas o Revit rechaza el filtro.
        /// </summary>
        /// <param name="idParametro2">Segunda regla (opcional, se omite si es null): parámetro igual a <paramref name="valor2"/>.</param>
        private ParameterFilterElement Crear(string nombre, IEnumerable<BuiltInCategory> categorias, ElementId idParametro, string valor,
            ElementId idParametro2 = null, string valor2 = null)
        {
            var reglas = new List<FilterRule> { ArbaRevit.EqualsRule(idParametro, valor) };
            if (idParametro2 != null && valor2 != null) reglas.Add(ArbaRevit.EqualsRule(idParametro2, valor2));
            return Crear(nombre, categorias, idParametro, reglas);
        }

        /// <summary>
        /// Crea el filtro "{nombre}" (o actualiza el existente) con las reglas dadas sobre las
        /// categorías en las que <paramref name="idParametro"/> admite filtros.
        /// </summary>
        private ParameterFilterElement Crear(string nombre, IEnumerable<BuiltInCategory> categorias, ElementId idParametro,
            IList<FilterRule> reglas)
        {
            var ids = new List<ElementId>();
            foreach (BuiltInCategory bic in categorias)
            {
                Category c = Category.GetCategory(_doc, bic);
                if (c != null) ids.Add(c.Id);
            }

            // Dejar solo las categorías en las que el parámetro admite filtro (una malla,
            // por ejemplo, puede no exponer la partición).
            ids = ids.Where(id => ParametroFiltrable(id, idParametro)).ToList();
            if (ids.Count == 0)
            {
                Advertencias.Add($"Filtro \"{nombre}\": el parámetro no admite filtros en esa categoría; no se creó.");
                return null;
            }

            try
            {
                var filtroElementos = new ElementParameterFilter(reglas);

                ParameterFilterElement existente = new FilteredElementCollector(_doc)
                    .OfClass(typeof(ParameterFilterElement))
                    .Cast<ParameterFilterElement>()
                    .FirstOrDefault(f => string.Equals(f.Name, nombre, StringComparison.OrdinalIgnoreCase));

                if (existente != null)
                {
                    existente.SetCategories(ids);
                    existente.SetElementFilter(filtroElementos);
                    FiltrosReutilizados.Add(existente);
                    return existente;
                }

                ParameterFilterElement nuevo = ParameterFilterElement.Create(_doc, nombre, ids, filtroElementos);
                FiltrosCreados.Add(nuevo);
                return nuevo;
            }
            catch (Exception ex)
            {
                Advertencias.Add($"No se pudo crear el filtro \"{nombre}\": {ex.Message}");
                return null;
            }
        }

        private bool ParametroFiltrable(ElementId categoria, ElementId parametro)
        {
            try
            {
                return ParameterFilterUtilities.GetFilterableParametersInCommon(_doc, new[] { categoria }).Contains(parametro);
            }
            catch (Exception)
            {
                return true; // se intenta igualmente; si falla, Create lo informa
            }
        }

        // ------------------------------------------------------------------
        // Aplicación a la vista
        // ------------------------------------------------------------------

        /// <summary>
        /// Vista a la que se aplican los filtros: la primera de <paramref name="candidatas"/> (la activa
        /// primero) que los admita. Si la activa no los admite —una tabla, que es lo habitual al repetir
        /// el metrado porque al terminar deja abierta la primera tabla; un plano; una vista cuya plantilla
        /// controla los filtros—, la siguiente, y lo explica en <paramref name="aviso"/>. Null si ninguna.
        /// </summary>
        public static View ElegirVista(IEnumerable<View> candidatas, out string aviso)
        {
            aviso = null;
            View activa = null;
            string motivoActiva = null;
            foreach (View v in candidatas)
            {
                if (v == null) continue;
                bool admite = AdmiteFiltros(v, out string motivo);
                if (activa == null)
                {
                    activa = v;
                    motivoActiva = motivo;
                }
                if (!admite) continue;

                if (v.Id != activa.Id)
                {
                    aviso = $"La vista activa \"{activa.Name}\" {motivoActiva}; los filtros de colores se aplicaron a la vista \"{v.Name}\".";
                }
                return v;
            }

            if (activa != null)
            {
                aviso = $"La vista activa \"{activa.Name}\" {motivoActiva} y no hay otra vista abierta que los admita; " +
                        "los filtros se crearon en el proyecto y puede aplicarlos a cualquier vista desde Visibilidad/Gráficos.";
            }
            return null;
        }

        /// <summary>
        /// True si se pueden añadir filtros a la vista: admite sustituciones gráficas (no es una tabla,
        /// un plano ni una plantilla) y su plantilla, si tiene, no controla los filtros. Si no,
        /// <paramref name="motivo"/> dice por qué.
        /// </summary>
        public static bool AdmiteFiltros(View vista, out string motivo)
        {
            motivo = null;
            try
            {
                if (vista.IsTemplate || !vista.AreGraphicsOverridesAllowed())
                {
                    motivo = "no admite filtros gráficos (por ejemplo, es una tabla o un plano)";
                    return false;
                }

                if (vista.ViewTemplateId != ElementId.InvalidElementId && vista.Document.GetElement(vista.ViewTemplateId) is View plantilla)
                {
                    var idFiltros = new ElementId(BuiltInParameter.VIS_GRAPHICS_FILTERS);
                    if (plantilla.GetTemplateParameterIds().Contains(idFiltros) &&
                        !plantilla.GetNonControlledTemplateParameterIds().Contains(idFiltros))
                    {
                        motivo = $"tiene la plantilla de vista \"{plantilla.Name}\", que controla los filtros";
                        return false;
                    }
                }
                return true;
            }
            catch (Exception)
            {
                motivo = "no admite filtros gráficos";
                return false;
            }
        }

        private void Aplicar(View vista, List<KeyValuePair<ParameterFilterElement, Color>> filtros)
        {
            if (!AdmiteFiltros(vista, out string motivo))
            {
                Advertencias.Add($"La vista \"{vista.Name}\" {motivo}; " +
                                 "los filtros se crearon en el proyecto y puede aplicarlos a cualquier vista desde Visibilidad/Gráficos.");
                return;
            }

            ElementId solido = PatronSolido();
            ICollection<ElementId> enVista;
            try { enVista = vista.GetFilters(); }
            catch (Exception) { enVista = new List<ElementId>(); }

            foreach (KeyValuePair<ParameterFilterElement, Color> par in filtros)
            {
                try
                {
                    if (!enVista.Contains(par.Key.Id)) vista.AddFilter(par.Key.Id);

                    var ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(par.Value);
                    ogs.SetCutLineColor(par.Value);
                    if (solido != null)
                    {
                        ogs.SetSurfaceForegroundPatternId(solido);
                        ogs.SetSurfaceForegroundPatternColor(par.Value);
                        ogs.SetCutForegroundPatternId(solido);
                        ogs.SetCutForegroundPatternColor(par.Value);
                    }
                    vista.SetFilterOverrides(par.Key.Id, ogs);
                    vista.SetFilterVisibility(par.Key.Id, true);
                    FiltrosAplicados++;
                }
                catch (Exception ex)
                {
                    string pista = vista.ViewTemplateId != ElementId.InvalidElementId
                        ? " La vista tiene una plantilla que controla los filtros: aplíquelos en la plantilla o desactive \"Filtros\" en ella."
                        : string.Empty;
                    Advertencias.Add($"No se pudieron aplicar los filtros a la vista \"{vista.Name}\": {ex.Message}.{pista}");
                    break;
                }
            }

            if (FiltrosAplicados > 0) VistaAplicada = vista.Name;
        }

        /// <summary>Patrón de relleno sólido del proyecto (null si no se encuentra).</summary>
        private ElementId PatronSolido()
        {
            try
            {
                FillPatternElement solido = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(f =>
                    {
                        FillPattern p = f.GetFillPattern();
                        return p != null && p.IsSolidFill && p.Target == FillPatternTarget.Drafting;
                    });
                return solido?.Id;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
