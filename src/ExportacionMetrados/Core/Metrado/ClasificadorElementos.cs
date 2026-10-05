using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Arba.Comun;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace ExportacionMetrados.Core.Metrado
{
    /// <summary>
    /// Gestiona los parámetros compartidos del contrato ARBA-comun que usa el metrado
    /// ("Metrado - Material", "Metrado - Peso (kg)", "Metrado - Elemento"...), clasifica los
    /// elementos en concreto / metálico, los reparte en grupos de metrado y escribe la partición
    /// del acero de refuerzo que no creó ningún add-in ARBA ("CATEGORIA - MAN-marca").
    /// Las definiciones, GUID y categorías de los parámetros los trae el contrato
    /// (<see cref="ArbaContract"/>); aquí solo se consumen.
    /// Todos los métodos que escriben deben llamarse dentro de una transacción.
    /// </summary>
    public static class ClasificadorElementos
    {
        /// <summary>Versión del contrato ARBA-comun con la que se compiló el plugin.</summary>
        public const string VersionContrato = ArbaContract.Version;

        // Nombres de los parámetros: alias del contrato (los GUID y categorías viven en ArbaContract).
        public static readonly string NombreParametroMaterial = ArbaContract.Material.Name;
        public static readonly string NombreParametroPeso = ArbaContract.Peso.Name;
        /// <summary>
        /// Parámetro de texto con el grupo de metrado: en vigas, columnas, losas... el suyo
        /// (VIGAS, COLUMNAS, CIMIENTOS, LOSAS, MUROS, CONEXIONES, OTROS, MISCELANEOS); en el
        /// refuerzo, el del anfitrión. Lo usan las tablas y los filtros de vista: no depende de
        /// la partición, que el usuario puede tener numerada a su manera.
        /// </summary>
        public static readonly string NombreParametroElementoRefuerzo = ArbaContract.Elemento.Name;
        public static readonly string NombreParametroPartida = ArbaContract.Partida.Name;
        public static readonly string NombreParametroPernos = ArbaContract.Pernos.Name;
        public static readonly string NombreParametroCodigo = ArbaContract.Codigo.Name;
        public static readonly string NombreParametroOrigen = ArbaContract.Origen.Name;

        public const string ValorConcreto = ArbaContract.MaterialConcreto;
        public const string ValorAceroEstructural = ArbaContract.MaterialAceroEstructural;
        public const string ValorMadera = ArbaContract.MaterialMadera;
        public const string ValorOtro = ArbaContract.MaterialOtro;

        /// <summary>Categorías de refuerzo a las que se vinculan los parámetros y filtros.</summary>
        public static readonly BuiltInCategory[] CategoriasRefuerzo =
        {
            BuiltInCategory.OST_Rebar, BuiltInCategory.OST_FabricReinforcement,
        };

        // ------------------------------------------------------------------
        // Ids de los parámetros compartidos (para reglas de filtro y campos de tabla)
        // ------------------------------------------------------------------

        /// <summary>Id del parámetro compartido "Metrado - Material" en el proyecto, o null si aún no se ha creado.</summary>
        public static ElementId IdParametroMaterial(Document doc) => ArbaSharedParams.IdOf(doc, ArbaContract.Material);

        /// <summary>Id del parámetro compartido "Metrado - Elemento" (null si aún no existe).</summary>
        public static ElementId IdParametroElementoRefuerzo(Document doc) => ArbaSharedParams.IdOf(doc, ArbaContract.Elemento);

        /// <summary>Id del parámetro compartido "Metrado - Peso (kg)" (null si aún no existe).</summary>
        public static ElementId IdParametroPeso(Document doc) => ArbaSharedParams.IdOf(doc, ArbaContract.Peso);

        /// <summary>Id del parámetro compartido "Metrado - Partida" (null si aún no existe).</summary>
        public static ElementId IdParametroPartida(Document doc) => ArbaSharedParams.IdOf(doc, ArbaContract.Partida);

        /// <summary>Id del parámetro compartido "Metrado - Pernos (und)" (null si aún no existe).</summary>
        public static ElementId IdParametroPernos(Document doc) => ArbaSharedParams.IdOf(doc, ArbaContract.Pernos);

        /// <summary>Id del parámetro compartido "ARBA - Código" (null si aún no existe).</summary>
        public static ElementId IdParametroCodigo(Document doc) => ArbaSharedParams.IdOf(doc, ArbaContract.Codigo);

        /// <summary>Perfiles y materiales metálicos reconocibles por el nombre de la familia o del tipo.</summary>
        private static readonly string[] PistasAcero =
        {
            "acero", "steel", "metal", "perfil", "hss", "shs", "rhs", "chs", "ipe", "ipn", "hea", "heb", "upn", "upe",
            "w ", "w1", "w2", "w3", "w4", "c ", "c1", "c2", "c3", "c4", "c5", "c6", "c7", "c8", "c9",
            "l ", "l1", "l2", "l3", "l4", "l5", "l6", "l7", "l8", "mc", "hp", "wt", "pl",
            "tubo", "tube", "pipe", "angle", "angulo", "ángulo", "channel", "canal",
        };

        /// <summary>
        /// Piezas de conexión y anclaje (espárragos, anclajes, pernos, planchas, cartelas...).
        /// Marcan la pieza como acero y la llevan al grupo "Conexiones y anclajes". Solo
        /// cuentan en las categorías que pueden ser metálicas.
        /// </summary>
        private static readonly string[] PistasConexion =
        {
            "esparrago", "espárrago", "stud", "anclaje", "anchor", "perno", "bolt", "tuerca", "nut", "arandela", "washer",
            "plate", "plancha", "rigidizador", "atiesador", "stiffener", "cartela", "gusset",
            "conexion", "conexión", "connection", "soldadura", "weld",
        };

        // ------------------------------------------------------------------
        // Parámetros compartidos del contrato
        // ------------------------------------------------------------------

        /// <summary>
        /// Asegura los ocho parámetros compartidos del contrato ARBA-comun (ARBA - Origen /
        /// Código / Anfitrión, Metrado - Partida / Material / Peso (kg) / Pernos (und) /
        /// Elemento): definición con GUID fijo y vínculo de ejemplar a sus categorías, desde un
        /// archivo temporal (el archivo de parámetros compartidos del usuario no cambia). Si el
        /// proyecto tenía un parámetro homónimo de proyecto o con otro GUID, lo sustituye
        /// conservando los valores y lo avisa. Devuelve false si alguno falló (ver advertencias).
        /// </summary>
        public static bool AsegurarParametrosContrato(Document doc, List<string> advertencias)
        {
            return ArbaSharedParams.EnsureAll(doc, advertencias);
        }

        /// <summary>Asegura "Metrado - Material" (categorías del contrato). Devuelve false si no fue posible.</summary>
        public static bool AsegurarParametroMaterial(Document doc, List<string> advertencias)
        {
            return ArbaSharedParams.Ensure(doc, ArbaContract.Material, advertencias);
        }

        /// <summary>Asegura "Metrado - Elemento" en armaduras, mallas y categorías de anfitrión (del contrato).</summary>
        public static bool AsegurarParametroElemento(Document doc, List<string> advertencias)
        {
            return ArbaSharedParams.Ensure(doc, ArbaContract.Elemento, advertencias);
        }

        /// <summary>Asegura "Metrado - Peso (kg)" en armaduras, mallas, perfiles y piezas metálicas (del contrato).</summary>
        public static bool AsegurarParametroPeso(Document doc, List<string> advertencias)
        {
            return ArbaSharedParams.Ensure(doc, ArbaContract.Peso, advertencias);
        }

        /// <summary>
        /// Rellena "Metrado - Material" en todos los elementos de las categorías. No toca el
        /// valor ya escrito por un add-in ARBA (elementos con "ARBA - Origen": rejillas, ángulos),
        /// que se cuenta en <paramref name="respetadosArba"/>. Devuelve el número de elementos
        /// actualizados.
        /// </summary>
        public static int RellenarMaterial(Document doc, IEnumerable<BuiltInCategory> categorias, bool conservarExistente,
            List<string> advertencias, out int respetadosArba)
        {
            int n = 0;
            respetadosArba = 0;
            foreach (BuiltInCategory bic in categorias)
            {
                var elementos = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElements();
                foreach (Element e in elementos)
                {
                    try
                    {
                        Parameter p = ArbaSharedParams.Get(e, ArbaContract.Material);
                        if (p == null || p.IsReadOnly) continue;
                        string actual = p.AsString();
                        bool tieneValor = !string.IsNullOrWhiteSpace(actual);
                        if (tieneValor && ArbaOrigin.IsArba(e)) { respetadosArba++; continue; }
                        if (conservarExistente && tieneValor) continue;

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
        /// su tipo (por clase, nombre, designación de norma como "A36" o "S355", o activo
        /// físico); nombre de la familia o del tipo; material por defecto de la categoría.
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
            if (categoriaMetalica && ContienePista(nombre, PistasConexion)) return ValorAceroEstructural;
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

        /// <summary>
        /// Clasificación vigente del elemento: la escrita en "Metrado - Material" o, si
        /// está vacía, la calculada.
        /// </summary>
        public static string ClasificacionActual(Document doc, Element e)
        {
            string v = ArbaSharedParams.GetText(e, ArbaContract.Material);
            return string.IsNullOrWhiteSpace(v) ? Clasificar(doc, e) : v.Trim().ToUpperInvariant();
        }

        /// <summary>True si el nombre de la familia o del tipo indica una pieza de conexión o anclaje.</summary>
        public static bool EsPiezaDeConexion(Document doc, Element e)
        {
            string nombre = (NombreFamilia(doc, e) + " " + e.Name).ToLowerInvariant();
            return ContienePista(nombre, PistasConexion);
        }

        /// <summary>True si el elemento es un misceláneo del contrato ARBA: tiene "Metrado - Partida".</summary>
        public static bool EsMiscelaneo(Element e) => e != null && ArbaMetrado.EsMiscelaneo(e);

        /// <summary>
        /// True si el plugin NO debe sobrescribir "Metrado - Peso (kg)" del elemento: lo creó un
        /// add-in ARBA ("ARBA - Origen" relleno) y ese add-in ya escribió un peso mayor que cero
        /// (rejillas, ángulos). Las armaduras no se protegen: su peso lo calcula siempre este
        /// plugin, también en las que armó un add-in ARBA (ver NOTAS-ARBA-COMUN.md).
        /// </summary>
        public static bool PesoProtegido(Element e) => e != null && !ArbaPartition.IsRebar(e) && ArbaMetrado.PesoProtegido(e);

        /// <summary>
        /// Grupo de metrado del elemento. Primero, los misceláneos del contrato (elementos con
        /// "Metrado - Partida") si ese grupo está marcado; después, el de su categoría, salvo
        /// que sea una pieza de conexión o anclaje de acero (por nombre) en una categoría
        /// metálica y el grupo "Conexiones y anclajes" esté marcado. Null si no se metra.
        /// </summary>
        public static CategoriaMetrado GrupoDe(Document doc, Element e, IList<CategoriaMetrado> grupos)
        {
            ElementId idCategoria = e.Category?.Id;

            CategoriaMetrado miscelaneos = grupos.FirstOrDefault(g => g.EsMiscelaneos && g.Seleccionada);
            if (miscelaneos != null && miscelaneos.Contiene(idCategoria) && EsMiscelaneo(e)) return miscelaneos;

            CategoriaMetrado porCategoria = CategoriaMetrado.DeCategoria(grupos, idCategoria);
            if (porCategoria == null || porCategoria.EsMiscelaneos) return null;

            CategoriaMetrado conexiones = grupos.FirstOrDefault(g => g.EsConexiones && g.Seleccionada);
            if (conexiones == null || conexiones == porCategoria || !porCategoria.PuedeSerMetalica) return porCategoria;

            return EsPiezaDeConexion(doc, e) && ClasificacionActual(doc, e) == ValorAceroEstructural ? conexiones : porCategoria;
        }

        /// <summary>
        /// Ejemplares que pertenecen al grupo según <see cref="GrupoDe"/>: los de sus
        /// categorías menos las piezas de conexión absorbidas por "Conexiones y anclajes" (que
        /// a su vez recoge las de todas las categorías metálicas) y los misceláneos (que van a
        /// su grupo).
        /// </summary>
        public static List<Element> ElementosDelGrupo(Document doc, CategoriaMetrado cat, IList<CategoriaMetrado> grupos)
        {
            IEnumerable<BuiltInCategory> categorias = cat.EsConexiones
                ? grupos.Where(g => g.PuedeSerMetalica).SelectMany(g => g.Categorias).Distinct()
                : cat.Categorias;

            var lista = new List<Element>();
            foreach (Element e in CategoriaMetrado.Colector(doc, categorias).ToElements())
            {
                if (GrupoDe(doc, e, grupos) == cat) lista.Add(e);
            }
            return lista;
        }

        /// <summary>CONCRETO, ACERO ESTRUCTURAL o MADERA según los materiales; null si ninguno lo indica.</summary>
        private static string ClasificarPorMateriales(Document doc, List<Material> materiales)
        {
            if (materiales.Any(m => CalculadorMetrado.MaterialEsConcreto(doc, m))) return ValorConcreto;
            if (materiales.Any(m => EsMaterialMetalico(doc, m))) return ValorAceroEstructural;
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

        /// <summary>
        /// Designaciones de norma del acero estructural en el nombre del material, como
        /// palabra completa: ASTM (A36, A-36, A36M, A53, A500, A501, A529, A572, A588, A709,
        /// A913, A992, A1011, A1018), EN 10025 (S235, S275, S355, S420, S460, con o sin
        /// calidad JR/J0/J2/K2/M/N/ML/NL) y grado ("Gr 50", "Grade 50", "Grado 36"). Los modelos
        /// exportados de Tekla traen el material así ("A36", "S355JR") sin la palabra "acero"
        /// ni la clase "Metal", y sin esto se clasificaban como OTRO.
        /// </summary>
        private static readonly Regex DesignacionAcero = new Regex(
            @"(?<![a-z0-9])(?:" +
            @"a-?(?:36|53|500|501|529|572|588|709|913|992|1011|1018)m?" +
            @"|s-?(?:235|275|355|420|460)(?:jr|j0|j2|k2|ml|nl|m|n)?" +
            @"|gr(?:ado|ade)?\.?\s*-?(?:36|42|50|55|60|65)" +
            @")(?![a-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// Material metálico: por la clase o el nombre ("metal", "acero", "steel", "alumin"),
        /// por una designación de norma en el nombre (<see cref="DesignacionAcero"/>) o porque
        /// su activo físico estructural es de clase Metal (mismo criterio que usa
        /// <see cref="CalculadorMetrado.MaterialEsConcreto"/> con el concreto).
        /// </summary>
        private static bool EsMaterialMetalico(Document doc, Material m)
        {
            string t = ((m.MaterialClass ?? string.Empty) + " " + (m.Name ?? string.Empty)).ToLowerInvariant();
            if (t.Contains("metal") || t.Contains("acero") || t.Contains("steel") || t.Contains("alumin")) return true;
            if (DesignacionAcero.IsMatch(m.Name ?? string.Empty)) return true;

            try
            {
                var activo = doc.GetElement(m.StructuralAssetId) as PropertySetElement;
                StructuralAsset sa = activo?.GetStructuralAsset();
                if (sa != null && sa.StructuralAssetClass == StructuralAssetClass.Metal) return true;
            }
            catch { }
            return false;
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

        // ------------------------------------------------------------------
        // Peso en kg
        // ------------------------------------------------------------------

        /// <summary>
        /// Escribe "Metrado - Peso (kg)" en cada armadura con el peso calculado.
        /// Devuelve el número de armaduras actualizadas.
        /// </summary>
        public static int RellenarPesos(Document doc, IEnumerable<BarraAcero> barras, List<string> advertencias, out int respetados)
        {
            return EscribirPesos(doc, barras.Select(b => new KeyValuePair<ElementId, double>(b.Id, b.PesoKg)), "de la armadura",
                advertencias, out respetados);
        }

        /// <summary>
        /// Escribe "Metrado - Peso (kg)" en cada perfil o pieza metálica (longitud × área de
        /// sección × densidad, o volumen × densidad). Las piezas cuyo peso ya escribió su add-in
        /// ARBA (rejillas, ángulos) no se tocan y se cuentan en <paramref name="respetados"/>.
        /// Devuelve el número de perfiles actualizados.
        /// </summary>
        public static int RellenarPesosPerfiles(Document doc, IEnumerable<ElementoAceroEstructural> perfiles, List<string> advertencias,
            out int respetados)
        {
            return EscribirPesos(doc, perfiles.Select(p => new KeyValuePair<ElementId, double>(p.Id, p.PesoKg)), "del perfil",
                advertencias, out respetados);
        }

        private static int EscribirPesos(Document doc, IEnumerable<KeyValuePair<ElementId, double>> pesos, string descripcion,
            List<string> advertencias, out int respetados)
        {
            int n = 0;
            respetados = 0;
            foreach (KeyValuePair<ElementId, double> par in pesos)
            {
                try
                {
                    Element e = doc.GetElement(par.Key);
                    if (e == null) continue;
                    if (PesoProtegido(e)) { respetados++; continue; }

                    Parameter p = ArbaSharedParams.Get(e, ArbaContract.Peso);
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
        public static ElementId AnfitrionDe(Element refuerzo) => ArbaPartition.RebarHostId(refuerzo);

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
                if (ArbaPartition.IsRebar(e)) resultado[e.Id] = e;
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
        /// Escribe "Metrado - Elemento" en cada refuerzo con la categoría que declara su
        /// partición (contrato 1.0.4: la fija de su prefijo, ZAP/CCO/BLQ → CIMIENTOS aunque
        /// el anfitrión sea un suelo, o la del texto) y, si no declara ninguna, el grupo de
        /// su anfitrión (VIGAS, COLUMNAS, CIMIENTOS, LOSAS, MUROS, CONEXIONES, OTROS).
        /// Siempre se sobrescribe: es un dato calculado, no del usuario; salvo con
        /// <paramref name="conservarExistente"/>, que solo rellena los vacíos (para respetar los
        /// textos propios que el usuario haya escrito para sus propias tablas). Devuelve el número
        /// de refuerzos actualizados.
        /// </summary>
        public static int RellenarElementoRefuerzo(Document doc, IEnumerable<Element> refuerzo, IList<CategoriaMetrado> categorias,
            List<string> advertencias, bool conservarExistente = false)
        {
            int n = 0;

            foreach (Element r in refuerzo)
            {
                try
                {
                    Parameter p = ArbaSharedParams.Get(r, ArbaContract.Elemento);
                    if (p == null || p.IsReadOnly || p.StorageType != StorageType.String) continue;
                    if (conservarExistente && !string.IsNullOrWhiteSpace(p.AsString())) continue;

                    Element host = doc.GetElement(AnfitrionDe(r));
                    string valor;
                    if (host?.Category == null) valor = "(SIN ANFITRIÓN)";
                    else valor = ArbaMetrado.ElementoFor(r, host, CategoriaMetrado.DeCategoria(categorias, host.Category.Id)?.NombreParticion);

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
        /// nombre de su grupo (VIGAS, COLUMNAS, ..., OTROS; MISCELANEOS en los elementos con
        /// "Metrado - Partida"). Es lo que filtra las tablas de varias categorías ("Otros",
        /// "Conexiones y anclajes", "Misceláneos") y saca a los misceláneos de las demás.
        /// Devuelve el número de elementos actualizados; <paramref name="miscelaneos"/> cuenta
        /// los que quedaron en ese grupo. Con <paramref name="conservarExistente"/> solo se
        /// rellenan los vacíos: los textos propios del usuario (para sus propias tablas) se respetan.
        /// </summary>
        public static int RellenarElementoEnElementos(Document doc, IEnumerable<BuiltInCategory> categorias,
            IList<CategoriaMetrado> grupos, List<string> advertencias, out int miscelaneos, bool conservarExistente = false)
        {
            int n = 0;
            miscelaneos = 0;
            foreach (Element e in CategoriaMetrado.Colector(doc, categorias).ToElements())
            {
                try
                {
                    CategoriaMetrado grupo = GrupoDe(doc, e, grupos);
                    string valor = grupo?.NombreParticion;
                    if (valor == null) continue;
                    if (grupo.EsMiscelaneos) miscelaneos++;
                    Parameter p = ArbaSharedParams.Get(e, ArbaContract.Elemento);
                    if (p == null || p.IsReadOnly || p.StorageType != StorageType.String) continue;
                    if (conservarExistente && !string.IsNullOrWhiteSpace(p.AsString())) continue;
                    if (!string.Equals(p.AsString() ?? string.Empty, valor, StringComparison.Ordinal))
                    {
                        p.Set(valor);
                        n++;
                    }
                }
                catch (Exception ex)
                {
                    advertencias.Add($"No se pudo escribir \"{NombreParametroElementoRefuerzo}\" en el elemento {e.Id}: {ex.Message}");
                }
            }
            return n;
        }

        /// <summary>
        /// True si la partición del refuerzo la escribió un add-in ARBA de armado (forma del
        /// contrato "CIMIENTOS - ZAP-Z1" o antigua "ZAP-Z1", "CC-C1", "BLQ-FT-01-F1"...) o el
        /// refuerzo lleva un "ARBA - Origen" distinto de MANUAL: el plugin no la toca nunca, ni
        /// con "Sobrescribir". Las particiones MAN y el origen MANUAL son del propio plugin y
        /// sí se pueden reescribir.
        /// </summary>
        public static bool EsParticionProtegida(Element refuerzo, string particion = null)
        {
            ArbaPartitionInfo info = ArbaPartition.Parse(particion ?? ArbaPartition.Read(refuerzo));
            if (info.IsArba && info.PrefixInfo != ArbaContract.Manual) return true;

            if (!ArbaOrigin.IsArba(refuerzo)) return false;
            ArbaPrefix origen = ArbaOrigin.PrefixOf(refuerzo);
            // Origen de otro add-in, o un origen que este contrato no conoce (versión más nueva): se respeta.
            return origen == null || origen != ArbaContract.Manual;
        }

        /// <summary>
        /// Escribe la partición de cada refuerzo que no creó un add-in ARBA. Con
        /// <paramref name="textoFijo"/> se escribe ese texto tal cual; sin él, la forma del
        /// contrato "CATEGORIA - MAN-marca" (categoría y marca, o Id, del anfitrión; p. ej.
        /// "VIGAS - MAN-V1"; sin anfitrión, "OTROS - MAN-&lt;id&gt;") y además "ARBA - Origen" =
        /// MANUAL y "Metrado - Elemento" con la categoría. Las particiones que ya tienen texto
        /// se respetan salvo <paramref name="sobrescribir"/>; las de los add-ins ARBA se
        /// respetan siempre y se cuentan en <paramref name="respetadasArba"/>.
        /// Los parámetros del contrato deben existir (<see cref="AsegurarParametrosContrato"/>).
        /// Devuelve el número de particiones cambiadas.
        /// </summary>
        public static int AsignarParticion(Document doc, IEnumerable<Element> refuerzo, bool sobrescribir, string textoFijo,
            List<string> advertencias, out int respetadasArba)
        {
            int n = 0;
            respetadasArba = 0;
            string fijo = string.IsNullOrWhiteSpace(textoFijo) ? null : textoFijo.Trim();

            foreach (Element r in refuerzo)
            {
                try
                {
                    Parameter p = ArbaPartition.PartitionParameter(r);
                    if (p == null || p.IsReadOnly) continue;

                    string actual = (p.AsString() ?? string.Empty).Trim();
                    if (EsParticionProtegida(r, actual)) { respetadasArba++; continue; }
                    if (!sobrescribir && actual.Length > 0) continue;

                    Element host = doc.GetElement(AnfitrionDe(r));
                    string valor;
                    if (fijo != null) valor = fijo;
                    else if (host != null) valor = ArbaPartition.BuildFor(host, ArbaContract.Manual);
                    else valor = ArbaPartition.Build(ArbaContract.CatOtros, ArbaContract.Manual.Prefix, string.Empty, ArbaRevit.IdValue(r.Id).ToString());

                    bool cambiada = !string.Equals(actual, valor, StringComparison.Ordinal) && ArbaPartition.Write(r, valor);

                    if (fijo == null)
                    {
                        // Origen MANUAL (sin código) y "Metrado - Elemento" con la categoría del anfitrión.
                        if (host != null) ArbaOrigin.WriteFor(r, host, ArbaContract.Manual, string.Empty);
                        else ArbaOrigin.Write(r, ArbaContract.Manual.Origin, string.Empty, null, ArbaContract.CatOtros);
                    }

                    if (cambiada) n++;
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
