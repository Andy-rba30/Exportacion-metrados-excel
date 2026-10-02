using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ExportacionMetrados.Core.Metrado
{
    /// <summary>
    /// Crea filtros de vista (Visibilidad/Gráficos) con un color por tipo de elemento
    /// metrado, para comprobar a simple vista qué entra en cada tabla:
    ///   - "Metrado - Concreto - {elemento}"           categoría + "Metrado - Material" = CONCRETO
    ///   - "Metrado - Acero estructural - {elemento}"  categoría + "Metrado - Material" = ACERO ESTRUCTURAL
    ///   - "Metrado - Refuerzo - {PARTICIÓN}"          armaduras y mallas con esa partición
    /// Los filtros quedan en el proyecto (se pueden usar en cualquier vista desde
    /// Visibilidad/Gráficos) y, si se pasa una vista, se aplican a ella con color de
    /// línea y relleno sólido. Debe llamarse dentro de una transacción abierta, después
    /// de rellenar "Metrado - Material" y las particiones.
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
        };
        private static readonly Dictionary<string, Color> ColoresAceroEstructural = new Dictionary<string, Color>
        {
            { "Vigas",    new Color(0, 190, 240) },        // celeste
            { "Columnas", new Color(255, 0, 255) },        // magenta
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
            if (idMaterial == null)
            {
                Advertencias.Add("No existe el parámetro \"Metrado - Material\"; no se crearon los filtros de concreto ni de acero estructural.");
            }
            else
            {
                foreach (CategoriaMetrado cat in categorias)
                {
                    Agregar(filtros, Crear(PrefijoConcreto + cat.Nombre, new[] { cat.Categoria }, idMaterial, ClasificadorElementos.ValorConcreto),
                        ColorDe(ColoresConcreto, cat.Nombre));

                    if (cat.PuedeSerMetalica)
                    {
                        Agregar(filtros, Crear(PrefijoAceroEstructural + cat.Nombre, new[] { cat.Categoria }, idMaterial, ClasificadorElementos.ValorAceroEstructural),
                            ColorDe(ColoresAceroEstructural, cat.Nombre));
                    }
                }
            }

            if (_op.IncluirAcero)
            {
                var idParticion = new ElementId(BuiltInParameter.NUMBER_PARTITION_PARAM);
                var categoriasRefuerzo = new[] { BuiltInCategory.OST_Rebar, BuiltInCategory.OST_FabricReinforcement };
                foreach (CategoriaMetrado cat in categorias)
                {
                    Agregar(filtros, Crear(PrefijoRefuerzo + cat.NombreParticion, categoriasRefuerzo, idParticion, cat.NombreParticion),
                        ColorDe(ColoresRefuerzo, cat.NombreParticion));
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
        private ParameterFilterElement Crear(string nombre, IEnumerable<BuiltInCategory> categorias, ElementId idParametro, string valor)
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
                var filtroElementos = new ElementParameterFilter(ReglaIgual(idParametro, valor));

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

        private static FilterRule ReglaIgual(ElementId idParametro, string valor)
        {
#if REVIT2021 || REVIT2022
            return ParameterFilterRuleFactory.CreateEqualsRule(idParametro, valor, false);
#else
            return ParameterFilterRuleFactory.CreateEqualsRule(idParametro, valor);
#endif
        }

        // ------------------------------------------------------------------
        // Aplicación a la vista
        // ------------------------------------------------------------------

        private void Aplicar(View vista, List<KeyValuePair<ParameterFilterElement, Color>> filtros)
        {
            bool admite;
            try { admite = !vista.IsTemplate && vista.AreGraphicsOverridesAllowed(); }
            catch (Exception) { admite = false; }

            if (!admite)
            {
                Advertencias.Add($"La vista activa \"{vista.Name}\" no admite filtros gráficos (por ejemplo una tabla); " +
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
