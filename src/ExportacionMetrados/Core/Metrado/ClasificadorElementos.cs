using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace ExportacionMetrados.Core.Metrado
{
    /// <summary>
    /// Gestiona el parámetro de proyecto "Metrado - Material" que separa los
    /// elementos de concreto de los metálicos, y rellena la partición del acero
    /// de refuerzo según la categoría del elemento anfitrión.
    /// Todos los métodos que escriben deben llamarse dentro de una transacción.
    /// </summary>
    public static class ClasificadorElementos
    {
        public const string NombreParametroMaterial = "Metrado - Material";
        public const string ValorConcreto = "CONCRETO";
        public const string ValorAceroEstructural = "ACERO ESTRUCTURAL";
        public const string ValorMadera = "MADERA";
        public const string ValorOtro = "OTRO";

        public const string NombreParametroPeso = "Metrado - Peso (kg)";
        /// <summary>
        /// Parámetro de texto con el grupo de metrado: en vigas, columnas, losas... el suyo
        /// (VIGAS, COLUMNAS, CIMIENTOS, LOSAS, MUROS, OTROS); en el refuerzo, el del
        /// anfitrión. Lo usan las tablas y los filtros de vista: no depende de la
        /// partición, que el usuario puede tener numerada a su manera.
        /// </summary>
        public const string NombreParametroElementoRefuerzo = "Metrado - Elemento";

        /// <summary>Categorías de refuerzo a las que se vinculan los parámetros y filtros.</summary>
        public static readonly BuiltInCategory[] CategoriasRefuerzo =
        {
            BuiltInCategory.OST_Rebar, BuiltInCategory.OST_FabricReinforcement,
        };

        // GUID fijos para que los parámetros compartidos sean los mismos en todos los proyectos.
        private static readonly Guid GuidParametroMaterial = new Guid("5B7E3C1A-2D4F-4A6B-9C8D-0E1F2A3B4C5D");
        private static readonly Guid GuidParametroPeso = new Guid("7D2A9F4E-6B1C-4C3D-8E5F-1A2B3C4D5E6F");
        private static readonly Guid GuidParametroElementoRefuerzo = new Guid("9A4C7E21-3B5D-4F8A-A6C2-2D3E4F5A6B7C");

        /// <summary>
        /// Id del parámetro compartido "Metrado - Material" en el proyecto (para reglas de
        /// filtro de vista), o null si aún no se ha creado.
        /// </summary>
        public static ElementId IdParametroMaterial(Document doc)
        {
            try { return SharedParameterElement.Lookup(doc, GuidParametroMaterial)?.Id; }
            catch (Exception) { return null; }
        }

        /// <summary>Perfiles y materiales metálicos reconocibles por el nombre de la familia o del tipo.</summary>
        private static readonly string[] PistasAcero =
        {
            "acero", "steel", "metal", "perfil", "hss", "shs", "rhs", "chs", "ipe", "ipn", "hea", "heb", "upn", "upe",
            "w ", "w1", "w2", "w3", "w4", "c ", "c1", "c2", "c3", "c4", "c5", "c6", "c7", "c8", "c9",
            "l ", "l1", "l2", "l3", "l4", "l5", "l6", "l7", "l8", "mc", "hp", "wt", "pl",
            "tubo", "tube", "pipe", "angle", "angulo", "ángulo", "channel", "canal",
        };

        /// <summary>
        /// Piezas de conexión metálicas (espárragos, anclajes, pernos, planchas...). Solo
        /// cuentan en vigas y columnas, las categorías que pueden ser metálicas.
        /// </summary>
        private static readonly string[] PistasPiezasMetalicas =
        {
            "esparrago", "espárrago", "stud", "anclaje", "anchor", "perno", "bolt", "tuerca", "nut", "arandela", "washer",
            "plate", "plancha", "rigidizador", "atiesador", "stiffener", "cartela", "gusset",
        };

        // ------------------------------------------------------------------
        // Parámetro "Metrado - Material"
        // ------------------------------------------------------------------

        /// <summary>
        /// Crea el parámetro de proyecto (si no existe) y lo vincula como parámetro
        /// de ejemplar a las categorías indicadas. Devuelve false si no fue posible.
        /// </summary>
        public static bool AsegurarParametroMaterial(Document doc, IEnumerable<BuiltInCategory> categorias, List<string> advertencias)
        {
            return AsegurarParametro(doc, NombreParametroMaterial, GuidParametroMaterial, true,
                "Clasificación automática para el metrado: CONCRETO, ACERO ESTRUCTURAL, MADERA u OTRO.",
                categorias, advertencias);
        }

        /// <summary>
        /// Crea y vincula "Metrado - Elemento" a las armaduras y mallas (grupo del
        /// anfitrión) y a las categorías de elementos indicadas (su propio grupo).
        /// </summary>
        public static bool AsegurarParametroElemento(Document doc, IEnumerable<BuiltInCategory> categoriasElementos,
            List<string> advertencias)
        {
            var categorias = new List<BuiltInCategory>(CategoriasRefuerzo);
            categorias.AddRange(categoriasElementos);
            return AsegurarParametro(doc, NombreParametroElementoRefuerzo, GuidParametroElementoRefuerzo, true,
                "Grupo de metrado escrito por el plugin: VIGAS, COLUMNAS, CIMIENTOS, LOSAS, MUROS u OTROS " +
                "(en el refuerzo, el de su elemento anfitrión).",
                categorias, advertencias);
        }

        /// <summary>Id del parámetro compartido "Metrado - Elemento" (null si aún no existe).</summary>
        public static ElementId IdParametroElementoRefuerzo(Document doc)
        {
            try { return SharedParameterElement.Lookup(doc, GuidParametroElementoRefuerzo)?.Id; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Crea y vincula "Metrado - Peso (kg)" a las armaduras (peso del refuerzo) y a
        /// vigas, columnas y conexiones (peso de los perfiles y piezas metálicas).
        /// </summary>
        public static bool AsegurarParametroPeso(Document doc, List<string> advertencias)
        {
            var categorias = new List<BuiltInCategory>(CategoriasRefuerzo);
            categorias.AddRange(CategoriaMetrado.Predeterminadas().Where(c => c.PuedeSerMetalica).SelectMany(c => c.Categorias));
            return AsegurarParametro(doc, NombreParametroPeso, GuidParametroPeso, false,
                "Peso en kg calculado por el plugin: armaduras = longitud total × kg/m; " +
                "perfiles metálicos = longitud × área de sección × densidad del acero al carbono; " +
                "conexiones y planchas = volumen × densidad.",
                categorias, advertencias);
        }

        private static bool AsegurarParametro(Document doc, string nombre, Guid guid, bool esTexto, string descripcion,
            IEnumerable<BuiltInCategory> categorias, List<string> advertencias)
        {
            Application app = doc.Application;
            var categoriasSet = app.Create.NewCategorySet();
            foreach (BuiltInCategory bic in categorias)
            {
                Category c = Category.GetCategory(doc, bic);
                if (c != null && c.AllowsBoundParameters) categoriasSet.Insert(c);
            }
            if (categoriasSet.IsEmpty) return false;

            try
            {
                // ¿Ya está vinculado?
                Definition existente = BuscarDefinicionVinculada(doc, nombre);
                if (existente != null)
                {
                    var binding = doc.ParameterBindings.get_Item(existente) as InstanceBinding;
                    if (binding != null)
                    {
                        bool faltan = false;
                        foreach (Category c in categoriasSet)
                        {
                            if (!binding.Categories.Contains(c)) { binding.Categories.Insert(c); faltan = true; }
                        }
                        if (faltan) doc.ParameterBindings.ReInsert(existente, binding, GrupoParametro());
                    }
                    return true;
                }

                ExternalDefinition definicion = ObtenerDefinicionCompartida(app, nombre, guid, esTexto, descripcion, advertencias);
                if (definicion == null) return false;

                InstanceBinding nuevo = app.Create.NewInstanceBinding(categoriasSet);
                bool ok = doc.ParameterBindings.Insert(definicion, nuevo, GrupoParametro());
                if (!ok)
                {
                    // Puede fallar si existe con otro vínculo: intentar reinsertar.
                    ok = doc.ParameterBindings.ReInsert(definicion, nuevo, GrupoParametro());
                }
                if (!ok) advertencias.Add("No se pudo vincular el parámetro \"" + nombre + "\" a las categorías.");
                return ok;
            }
            catch (Exception ex)
            {
                advertencias.Add("No se pudo crear el parámetro \"" + nombre + "\": " + ex.Message);
                return false;
            }
        }

        private static Definition BuscarDefinicionVinculada(Document doc, string nombre)
        {
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
            {
                Definition d = it.Key;
                if (d != null && string.Equals(d.Name, nombre, StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }
            }
            return null;
        }

        /// <summary>
        /// Obtiene la definición compartida desde un archivo temporal propio, sin
        /// alterar el archivo de parámetros compartidos del usuario.
        /// </summary>
        private static ExternalDefinition ObtenerDefinicionCompartida(Application app, string nombre, Guid guid, bool esTexto,
            string descripcion, List<string> advertencias)
        {
            string archivoOriginal = app.SharedParametersFilename;
            string carpeta = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExportacionMetrados");
            System.IO.Directory.CreateDirectory(carpeta);
            string archivo = System.IO.Path.Combine(carpeta, "ParametrosMetrado.txt");
            if (!System.IO.File.Exists(archivo)) System.IO.File.WriteAllText(archivo, string.Empty);

            try
            {
                app.SharedParametersFilename = archivo;
                DefinitionFile df = app.OpenSharedParameterFile();
                if (df == null)
                {
                    advertencias.Add("No se pudo abrir el archivo de parámetros compartidos del plugin.");
                    return null;
                }

                DefinitionGroup grupo = df.Groups.get_Item("Metrados") ?? df.Groups.Create("Metrados");
                var def = grupo.Definitions.get_Item(nombre) as ExternalDefinition;
                if (def != null) return def;

#if REVIT2021
                var opciones = new ExternalDefinitionCreationOptions(nombre, esTexto ? ParameterType.Text : ParameterType.Number)
#else
                var opciones = new ExternalDefinitionCreationOptions(nombre, esTexto ? SpecTypeId.String.Text : SpecTypeId.Number)
#endif
                {
                    GUID = guid,
                    Description = descripcion ?? string.Empty,
                    UserModifiable = true,
                    Visible = true,
                };
                return grupo.Definitions.Create(opciones) as ExternalDefinition;
            }
            finally
            {
                if (!string.IsNullOrEmpty(archivoOriginal))
                {
                    try { app.SharedParametersFilename = archivoOriginal; } catch { }
                }
            }
        }

#if REVIT2021
        private static BuiltInParameterGroup GrupoParametro() => BuiltInParameterGroup.PG_DATA;
#else
        private static ForgeTypeId GrupoParametro() => GroupTypeId.Data;
#endif

        /// <summary>
        /// Rellena "Metrado - Material" en todos los elementos de las categorías.
        /// Devuelve el número de elementos actualizados.
        /// </summary>
        public static int RellenarMaterial(Document doc, IEnumerable<BuiltInCategory> categorias, bool conservarExistente, List<string> advertencias)
        {
            int n = 0;
            foreach (BuiltInCategory bic in categorias)
            {
                var elementos = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElements();
                foreach (Element e in elementos)
                {
                    try
                    {
                        Parameter p = e.LookupParameter(NombreParametroMaterial);
                        if (p == null || p.IsReadOnly) continue;
                        string actual = p.AsString();
                        if (conservarExistente && !string.IsNullOrWhiteSpace(actual)) continue;

                        string valor = Clasificar(doc, e);
                        if (!string.Equals(actual, valor, StringComparison.Ordinal))
                        {
                            p.Set(valor);
                            n++;
                        }
                    }
                    catch (Exception ex)
                    {
                        advertencias.Add($"No se pudo clasificar el elemento {e.Id}: {ex.Message}");
                    }
                }
            }
            return n;
        }

        /// <summary>
        /// Clasifica un elemento como CONCRETO, ACERO ESTRUCTURAL, MADERA u OTRO, en este orden:
        /// tipo de material estructural de la familia; materiales asignados al elemento o a
        /// su tipo; nombre de la familia o del tipo; material por defecto de la categoría.
        /// Sin ningún dato, losas, muros y cimentaciones se asumen de concreto; vigas y
        /// columnas quedan como OTRO para no colarse en el metrado de concreto, y las
        /// conexiones estructurales se asumen de acero.
        /// </summary>
        public static string Clasificar(Document doc, Element e)
        {
            // 1. Tipo de material estructural de la familia (el más fiable).
            if (e is FamilyInstance fi)
            {
                try
                {
                    switch (fi.StructuralMaterialType)
                    {
                        case StructuralMaterialType.Concrete:
                        case StructuralMaterialType.PrecastConcrete:
                            return ValorConcreto;
                        case StructuralMaterialType.Steel:
                            return ValorAceroEstructural;
                        case StructuralMaterialType.Wood:
                            return ValorMadera;
                    }
                }
                catch { }
            }

            // 2. Materiales asignados al elemento o a su tipo (losas y muros compuestos,
            //    familias genéricas). No el material por defecto de la categoría: ese no
            //    dice nada del elemento y haría "de concreto" a cualquier perfil sin material.
            var materiales = new List<Material>();
            try
            {
                Material asignado = CalculadorMetrado.MaterialEstructuralAsignado(doc, e);
                if (asignado != null) materiales.Add(asignado);
            }
            catch { }
            try
            {
                foreach (ElementId id in e.GetMaterialIds(false))
                {
                    if (doc.GetElement(id) is Material m) materiales.Add(m);
                }
            }
            catch { }

            string porMaterial = ClasificarPorMateriales(doc, materiales);
            if (porMaterial != null) return porMaterial;

            // 3. Nombre de la familia o del tipo. Los perfiles (W12X26, HSS, L3X3...) y las
            //    piezas de conexión importados de IFC traen materiales genéricos
            //    ("Material IFC (r-g-b)") que no dicen nada, pero el nombre sí.
            CategoriaMetrado catMetrado = CategoriaMetrado.De(e);
            bool categoriaMetalica = catMetrado?.PuedeSerMetalica ?? false;
            string nombre = (NombreFamilia(doc, e) + " " + e.Name).ToLowerInvariant();
            if (ContienePista(nombre, PistasAcero)) return ValorAceroEstructural;
            if (categoriaMetalica && ContienePista(nombre, PistasPiezasMetalicas)) return ValorAceroEstructural;
            if (nombre.Contains("madera") || nombre.Contains("wood") || nombre.Contains("timber")) return ValorMadera;
            if (nombre.Contains("concreto") || nombre.Contains("hormig") || nombre.Contains("concrete")) return ValorConcreto;

            // 4. Material por defecto de la categoría (elementos "<Por categoría>").
            try
            {
                Material deCategoria = e.Category?.Material;
                if (deCategoria != null)
                {
                    string porCategoria = ClasificarPorMateriales(doc, new List<Material> { deCategoria });
                    if (porCategoria != null) return porCategoria;
                }
            }
            catch { }

            // 5. Sin ningún dato útil (sin materiales o solo genéricos): losas, muros y
            //    cimentaciones se asumen de concreto, igual que hace el resumen calculado;
            //    conexiones y rigidizadores, de acero; el resto (vigas y columnas, que
            //    pueden ser de ambos; modelos genéricos; cubiertas) queda como OTRO. Con un
            //    material real que no es concreto, acero ni madera (acabados...), OTRO.
            bool sinInformacion = materiales.Count == 0 || materiales.All(EsMaterialGenerico);
            if (!sinInformacion) return ValorOtro;
            if (EsDeCategoria(e, BuiltInCategory.OST_StructConnections, BuiltInCategory.OST_StructuralStiffener)) return ValorAceroEstructural;
            if (EsDeCategoria(e, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralFoundation)) return ValorConcreto;
            return ValorOtro;
        }

        private static bool EsDeCategoria(Element e, params BuiltInCategory[] categorias)
        {
            ElementId id = e.Category?.Id;
            return id != null && categorias.Any(c => id == new ElementId(c));
        }

        /// <summary>CONCRETO, ACERO ESTRUCTURAL o MADERA según los materiales; null si ninguno lo indica.</summary>
        private static string ClasificarPorMateriales(Document doc, List<Material> materiales)
        {
            if (materiales.Any(m => CalculadorMetrado.MaterialEsConcreto(doc, m))) return ValorConcreto;
            if (materiales.Any(EsMaterialMetalico)) return ValorAceroEstructural;
            if (materiales.Any(EsMaterialMadera)) return ValorMadera;
            return null;
        }

        /// <summary>
        /// Materiales que no aportan información: los "Material IFC (r-g-b)" que crea la
        /// importación de IFC a partir del color y los genéricos o por categoría de Revit.
        /// </summary>
        public static bool EsMaterialGenerico(Material m)
        {
            string n = (m.Name ?? string.Empty).Trim().ToLowerInvariant();
            if (n.Length == 0 || n.StartsWith("<")) return true;
            if (n.Contains("ifc")) return true;
            return n == "default" || n == "por defecto" || n == "predeterminado" ||
                   n == "generic" || n == "genérico" || n == "generico";
        }

        private static bool EsMaterialMetalico(Material m)
        {
            string t = ((m.MaterialClass ?? string.Empty) + " " + (m.Name ?? string.Empty)).ToLowerInvariant();
            return t.Contains("metal") || t.Contains("acero") || t.Contains("steel") || t.Contains("alumin");
        }

        private static bool EsMaterialMadera(Material m)
        {
            string t = ((m.MaterialClass ?? string.Empty) + " " + (m.Name ?? string.Empty)).ToLowerInvariant();
            return t.Contains("madera") || t.Contains("wood") || t.Contains("timber");
        }

        private static string NombreFamilia(Document doc, Element e)
        {
            var tipo = doc.GetElement(e.GetTypeId()) as ElementType;
            return tipo?.FamilyName ?? string.Empty;
        }

        private static bool ContienePista(string texto, string[] pistas)
        {
            foreach (string p in pistas)
            {
                // Las pistas cortas (perfiles W, C, L...) solo valen al inicio del nombre.
                if (p.Length <= 2 ? texto.StartsWith(p) : texto.Contains(p)) return true;
            }
            return false;
        }

        /// <summary>
        /// Escribe "Metrado - Peso (kg)" en cada armadura con el peso calculado.
        /// Devuelve el número de armaduras actualizadas.
        /// </summary>
        public static int RellenarPesos(Document doc, IEnumerable<BarraAcero> barras, List<string> advertencias)
        {
            return EscribirPesos(doc, barras.Select(b => new KeyValuePair<ElementId, double>(b.Id, b.PesoKg)), "de la armadura", advertencias);
        }

        /// <summary>
        /// Escribe "Metrado - Peso (kg)" en cada perfil metálico (longitud × área de
        /// sección × densidad). Devuelve el número de perfiles actualizados.
        /// </summary>
        public static int RellenarPesosPerfiles(Document doc, IEnumerable<ElementoAceroEstructural> perfiles, List<string> advertencias)
        {
            return EscribirPesos(doc, perfiles.Select(p => new KeyValuePair<ElementId, double>(p.Id, p.PesoKg)), "del perfil", advertencias);
        }

        private static int EscribirPesos(Document doc, IEnumerable<KeyValuePair<ElementId, double>> pesos, string descripcion,
            List<string> advertencias)
        {
            int n = 0;
            foreach (KeyValuePair<ElementId, double> par in pesos)
            {
                try
                {
                    Element e = doc.GetElement(par.Key);
                    Parameter p = e?.LookupParameter(NombreParametroPeso);
                    if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) continue;

                    double valor = Math.Round(par.Value, 3);
                    if (Math.Abs(p.AsDouble() - valor) > 0.0005)
                    {
                        p.Set(valor);
                        n++;
                    }
                }
                catch (Exception ex)
                {
                    advertencias.Add($"No se pudo escribir el peso {descripcion} {par.Key}: {ex.Message}");
                }
            }
            return n;
        }

        // ------------------------------------------------------------------
        // Partición del acero de refuerzo
        // ------------------------------------------------------------------

        /// <summary>Todas las barras, refuerzos de sistema y mallas del documento.</summary>
        public static List<Element> TodoElRefuerzo(Document doc)
        {
            var lista = new List<Element>();
            lista.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Rebar)).ToElements());
            lista.AddRange(new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)).ToElements());
            lista.AddRange(new FilteredElementCollector(doc).OfClass(typeof(FabricSheet)).ToElements());
            return lista;
        }

        /// <summary>Id del elemento anfitrión de una barra, malla o refuerzo de sistema.</summary>
        public static ElementId AnfitrionDe(Element refuerzo)
        {
            try
            {
                switch (refuerzo)
                {
                    case Rebar r: return r.GetHostId();
                    case RebarInSystem ris: return ris.GetHostId();
                    case FabricSheet fs: return fs.HostId;
                }
            }
            catch { }
            return ElementId.InvalidElementId;
        }

        /// <summary>
        /// A partir de una selección mixta (anfitriones y/o refuerzo) devuelve el
        /// refuerzo afectado: el seleccionado directamente más el alojado en los
        /// elementos seleccionados.
        /// </summary>
        public static List<Element> RefuerzoDeSeleccion(Document doc, ICollection<ElementId> seleccion)
        {
            var hosts = new HashSet<ElementId>();
            var resultado = new Dictionary<ElementId, Element>();

            foreach (ElementId id in seleccion)
            {
                Element e = doc.GetElement(id);
                if (e == null) continue;
                if (e is Rebar || e is RebarInSystem || e is FabricSheet) resultado[e.Id] = e;
                else hosts.Add(id);
            }

            if (hosts.Count > 0)
            {
                foreach (Element r in TodoElRefuerzo(doc))
                {
                    if (hosts.Contains(AnfitrionDe(r))) resultado[r.Id] = r;
                }
            }
            return resultado.Values.ToList();
        }

        /// <summary>
        /// Escribe "Metrado - Elemento" en cada refuerzo con el tipo de su anfitrión
        /// (VIGAS, COLUMNAS, CIMIENTOS, LOSAS, MUROS; otra categoría, su nombre en
        /// mayúsculas). Siempre se sobrescribe: es un dato calculado, no del usuario.
        /// Devuelve el número de refuerzos actualizados.
        /// </summary>
        public static int RellenarElementoRefuerzo(Document doc, IEnumerable<Element> refuerzo, IList<CategoriaMetrado> categorias,
            List<string> advertencias)
        {
            int n = 0;

            foreach (Element r in refuerzo)
            {
                try
                {
                    Parameter p = r.LookupParameter(NombreParametroElementoRefuerzo);
                    if (p == null || p.IsReadOnly || p.StorageType != StorageType.String) continue;

                    Element host = doc.GetElement(AnfitrionDe(r));
                    string valor;
                    if (host?.Category == null) valor = "(SIN ANFITRIÓN)";
                    else valor = CategoriaMetrado.DeCategoria(categorias, host.Category.Id)?.NombreParticion ?? host.Category.Name.ToUpperInvariant();

                    if (!string.Equals(p.AsString() ?? string.Empty, valor, StringComparison.Ordinal))
                    {
                        p.Set(valor);
                        n++;
                    }
                }
                catch (Exception ex)
                {
                    advertencias.Add($"No se pudo escribir \"{NombreParametroElementoRefuerzo}\" en el refuerzo {r.Id}: {ex.Message}");
                }
            }
            return n;
        }

        /// <summary>
        /// Escribe "Metrado - Elemento" en cada elemento de los grupos indicados con el
        /// nombre de su grupo (VIGAS, COLUMNAS, ..., OTROS). Es lo que filtra la tabla
        /// "Metrado acero estructural - Otros", que reúne varias categorías de Revit.
        /// Devuelve el número de elementos actualizados.
        /// </summary>
        public static int RellenarElementoEnElementos(Document doc, IEnumerable<CategoriaMetrado> categorias, List<string> advertencias)
        {
            int n = 0;
            foreach (CategoriaMetrado cat in categorias)
            {
                foreach (Element e in cat.Elementos(doc).ToElements())
                {
                    try
                    {
                        Parameter p = e.LookupParameter(NombreParametroElementoRefuerzo);
                        if (p == null || p.IsReadOnly || p.StorageType != StorageType.String) continue;
                        if (!string.Equals(p.AsString() ?? string.Empty, cat.NombreParticion, StringComparison.Ordinal))
                        {
                            p.Set(cat.NombreParticion);
                            n++;
                        }
                    }
                    catch (Exception ex)
                    {
                        advertencias.Add($"No se pudo escribir \"{NombreParametroElementoRefuerzo}\" en el elemento {e.Id}: {ex.Message}");
                    }
                }
            }
            return n;
        }

        /// <summary>
        /// Escribe la partición de cada refuerzo. Si <paramref name="textoFijo"/> es
        /// nulo se usa el nombre de partición de la categoría del anfitrión
        /// (VIGAS, COLUMNAS, CIMIENTOS, LOSAS, MUROS). Devuelve el número de cambios.
        /// </summary>
        public static int AsignarParticion(Document doc, IEnumerable<Element> refuerzo, IList<CategoriaMetrado> categorias,
            bool sobrescribir, string textoFijo, List<string> advertencias)
        {
            int n = 0;

            foreach (Element r in refuerzo)
            {
                try
                {
                    Parameter p = r.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM);
                    if (p == null || p.IsReadOnly) continue;

                    string actual = p.AsString() ?? string.Empty;
                    if (!sobrescribir && !string.IsNullOrWhiteSpace(actual)) continue;

                    string valor = textoFijo;
                    if (string.IsNullOrWhiteSpace(valor))
                    {
                        Element host = doc.GetElement(AnfitrionDe(r));
                        valor = host?.Category == null ? null : CategoriaMetrado.DeCategoria(categorias, host.Category.Id)?.NombreParticion;
                        if (valor == null) continue;
                    }

                    if (!string.Equals(actual, valor, StringComparison.Ordinal))
                    {
                        p.Set(valor);
                        n++;
                    }
                }
                catch (Exception ex)
                {
                    advertencias.Add($"No se pudo asignar partición al refuerzo {r.Id}: {ex.Message}");
                }
            }
            return n;
        }
    }
}
