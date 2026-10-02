using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.DB.Structure.StructuralSections;

namespace ExportacionMetrados.Core.Metrado
{
    /// <summary>
    /// Recorre el modelo y calcula volúmenes de concreto, pesos de perfiles
    /// metálicos y pesos de acero de refuerzo directamente desde los elementos,
    /// sin necesidad de tablas de planificación.
    /// </summary>
    public class CalculadorMetrado
    {
        private readonly Document _doc;
        private readonly OpcionesMetrado _opciones;
        private readonly Dictionary<ElementId, bool> _cacheMaterialConcreto = new Dictionary<ElementId, bool>();
        private readonly Dictionary<ElementId, Level> _cacheNiveles = new Dictionary<ElementId, Level>();

        /// <summary>Nombres habituales de un parámetro de área de sección en familias propias de perfiles.</summary>
        private static readonly string[] NombresParametroAreaSeccion =
        {
            "Section Area", "Área de sección", "Area de seccion", "Área de la sección", "Área sección", "Area", "Área", "A",
        };

        private static readonly string[] PalabrasConcreto = { "concret", "hormig", "f'c", "f´c", "fc=", "fc " };
        /// <summary>Nombres habituales del parámetro de peso por metro en los tipos de barra.</summary>
        public static readonly string[] NombresParametroPesoBarra =
        {
            "Bar Mass per Unit Length", "Masa de barra por unidad de longitud", "Masa por unidad de longitud",
            "Peso unitario", "Peso por metro", "Peso por unidad de longitud",
            "Bar Weight", "Unit Weight", "Weight per Length", "Mass per Unit Length",
        };

        /// <summary>Nombres habituales de un parámetro de masa total en la instancia de barra.</summary>
        private static readonly string[] NombresParametroMasaTotal =
        {
            "Total Bar Mass", "Masa total de barra", "Masa total de barras", "Peso total",
        };

        public CalculadorMetrado(Document doc, OpcionesMetrado opciones)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _opciones = opciones ?? throw new ArgumentNullException(nameof(opciones));
        }

        public ResultadoMetrado Calcular()
        {
            var resultado = new ResultadoMetrado();
            var categoriasSeleccionadas = _opciones.Categorias.Where(c => c.Seleccionada).ToList();

            foreach (CategoriaMetrado cat in categoriasSeleccionadas)
            {
                CalcularConcreto(cat, resultado);
            }

            if (_opciones.IncluirAcero)
            {
                CalcularAcero(categoriasSeleccionadas, resultado);
            }

            if (resultado.ElementosOmitidosPorMaterial > 0)
            {
                resultado.Advertencias.Add(
                    $"{resultado.ElementosOmitidosPorMaterial} elemento(s) se omitieron porque su material no es de concreto. " +
                    "Desactive \"Solo material de concreto\" para incluirlos.");
            }

            return resultado;
        }

        // ------------------------------------------------------------------
        // Concreto
        // ------------------------------------------------------------------

        private void CalcularConcreto(CategoriaMetrado cat, ResultadoMetrado resultado)
        {
            var elementos = new FilteredElementCollector(_doc)
                .OfCategory(cat.Categoria)
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (Element e in elementos)
            {
                try
                {
                    // Los perfiles metálicos no se metran por volumen sino por peso.
                    if (cat.PuedeSerMetalica && EsAceroEstructural(e))
                    {
                        ElementoAceroEstructural perfil = MedirPerfilMetalico(e, cat.Nombre, resultado);
                        if (perfil != null) resultado.AceroEstructural.Add(perfil);
                        continue;
                    }

                    ElementoConcreto medido = MedirElementoConcreto(e, cat.Nombre);
                    if (medido != null)
                    {
                        resultado.Concreto.Add(medido);
                    }
                    else
                    {
                        resultado.ElementosOmitidosPorMaterial++;
                    }
                }
                catch (Exception ex)
                {
                    resultado.Advertencias.Add($"{cat.Nombre} Id {e.Id}: {ex.Message}");
                }
            }
        }

        private ElementoConcreto MedirElementoConcreto(Element e, string nombreCategoria)
        {
            // Volumen por material: suma solo los materiales de concreto (para losas o
            // muros compuestos esto excluye acabados, aislamiento, etc.).
            double volumenPies3 = 0;
            string nombreMaterial = null;
            bool hayMateriales = false;

            ICollection<ElementId> materiales;
            try { materiales = e.GetMaterialIds(false); }
            catch { materiales = new List<ElementId>(); }

            foreach (ElementId matId in materiales)
            {
                hayMateriales = true;
                Material mat = _doc.GetElement(matId) as Material;
                if (mat == null) continue;

                bool esConcreto = EsMaterialConcreto(mat);
                if (esConcreto || !_opciones.SoloMaterialConcreto)
                {
                    double v = e.GetMaterialVolume(matId);
                    if (v > 0)
                    {
                        volumenPies3 += v;
                        if (nombreMaterial == null || esConcreto) nombreMaterial = mat.Name;
                    }
                }
            }

            if (volumenPies3 <= 0)
            {
                // Sin materiales asignados (por ejemplo "<Por categoría>"): usar el
                // material estructural o el de la categoría y el volumen total.
                Material matEstructural = ObtenerMaterialEstructural(e);
                bool esConcreto = matEstructural != null && EsMaterialConcreto(matEstructural);

                // Si se conoce el material y no es concreto, se omite. Si no hay
                // material asignado no se puede verificar y se incluye con aviso.
                if (_opciones.SoloMaterialConcreto && (hayMateriales || matEstructural != null) && !esConcreto)
                {
                    return null;
                }

                volumenPies3 = LeerDouble(e, BuiltInParameter.HOST_VOLUME_COMPUTED);
                nombreMaterial = matEstructural?.Name ?? "(sin material)";
            }

            if (volumenPies3 <= 0)
            {
                return null;
            }

            Level nivel = ObtenerNivel(e);
            var tipo = _doc.GetElement(e.GetTypeId()) as ElementType;

            return new ElementoConcreto
            {
                Id = e.Id,
                Categoria = nombreCategoria,
                Nivel = nivel?.Name ?? "(sin nivel)",
                ElevacionNivel = nivel?.Elevation ?? double.MinValue,
                Familia = tipo?.FamilyName ?? e.Category?.Name ?? string.Empty,
                Tipo = tipo?.Name ?? e.Name,
                Marca = LeerTexto(e, BuiltInParameter.ALL_MODEL_MARK),
                Material = nombreMaterial,
                LongitudM = AMetros(ObtenerLongitud(e)),
                AreaM2 = AMetrosCuadrados(LeerDouble(e, BuiltInParameter.HOST_AREA_COMPUTED)),
                EspesorM = AMetros(ObtenerEspesor(e, tipo)),
                VolumenM3 = AMetrosCubicos(volumenPies3),
            };
        }

        /// <summary>Espesor de losas, muros y zapatas (0 si no aplica).</summary>
        private static double ObtenerEspesor(Element e, ElementType tipo)
        {
            if (e is Floor)
            {
                double t = LeerDouble(e, BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM);
                if (t <= 0 && tipo != null) t = LeerDouble(tipo, BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM);
                return t;
            }
            if (e is Wall muro)
            {
                return muro.Width;
            }
            if (tipo != null)
            {
                double t = LeerDouble(tipo, BuiltInParameter.STRUCTURAL_FOUNDATION_THICKNESS);
                if (t > 0) return t;
            }
            return 0;
        }

        private Material ObtenerMaterialEstructural(Element e) => MaterialEstructuralDe(_doc, e);

        private bool EsMaterialConcreto(Material mat)
        {
            if (_cacheMaterialConcreto.TryGetValue(mat.Id, out bool cached)) return cached;
            bool resultado = MaterialEsConcreto(_doc, mat);
            _cacheMaterialConcreto[mat.Id] = resultado;
            return resultado;
        }

        /// <summary>
        /// Un material es de concreto si su clase o nombre lo indican, o si su
        /// activo estructural es de clase Concrete.
        /// </summary>
        public static bool MaterialEsConcreto(Document doc, Material mat)
        {
            if (mat == null) return false;
            if (ContienePalabraConcreto(mat.MaterialClass) || ContienePalabraConcreto(mat.Name)) return true;

            try
            {
                var activo = doc.GetElement(mat.StructuralAssetId) as PropertySetElement;
                StructuralAsset sa = activo?.GetStructuralAsset();
                if (sa != null && sa.StructuralAssetClass == StructuralAssetClass.Concrete) return true;
            }
            catch { }
            return false;
        }

        /// <summary>Material estructural de un elemento (instancia, tipo o categoría).</summary>
        public static Material MaterialEstructuralDe(Document doc, Element e)
        {
            Parameter p = e.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
            if (p == null || p.AsElementId() == ElementId.InvalidElementId)
            {
                var tipo = doc.GetElement(e.GetTypeId());
                p = tipo?.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
            }
            if (p != null && p.AsElementId() != ElementId.InvalidElementId)
            {
                return doc.GetElement(p.AsElementId()) as Material;
            }
            return e.Category?.Material;
        }

        private static bool ContienePalabraConcreto(string texto)
        {
            if (string.IsNullOrEmpty(texto)) return false;
            string t = texto.ToLowerInvariant();
            foreach (string palabra in PalabrasConcreto)
            {
                if (t.Contains(palabra)) return true;
            }
            return false;
        }

        private static double ObtenerLongitud(Element e)
        {
            double l = LeerDouble(e, BuiltInParameter.INSTANCE_LENGTH_PARAM);
            if (l > 0) return l;

            l = LeerDouble(e, BuiltInParameter.CURVE_ELEM_LENGTH);
            if (l > 0) return l;

            if (e.Location is LocationCurve lc && lc.Curve != null)
            {
                return lc.Curve.Length;
            }
            return 0;
        }

        // ------------------------------------------------------------------
        // Acero estructural (perfiles metálicos)
        // ------------------------------------------------------------------

        /// <summary>
        /// True si el elemento está (o quedará) clasificado como ACERO ESTRUCTURAL en
        /// "Metrado - Material": se respeta el valor ya escrito si se pidió conservarlo;
        /// si no, se clasifica igual que lo hará el plugin al rellenar el parámetro.
        /// </summary>
        private bool EsAceroEstructural(Element e)
        {
            if (_opciones.ConservarClasificacionMaterial)
            {
                string actual = null;
                try { actual = e.LookupParameter(ClasificadorElementos.NombreParametroMaterial)?.AsString(); } catch { }
                if (!string.IsNullOrWhiteSpace(actual))
                {
                    return string.Equals(actual.Trim(), ClasificadorElementos.ValorAceroEstructural, StringComparison.OrdinalIgnoreCase);
                }
            }
            return ClasificadorElementos.Clasificar(_doc, e) == ClasificadorElementos.ValorAceroEstructural;
        }

        /// <summary>
        /// Mide un perfil metálico: peso = longitud × área de la sección × densidad.
        /// Devuelve null (con advertencia) si no hay longitud ni área de sección.
        /// </summary>
        private ElementoAceroEstructural MedirPerfilMetalico(Element e, string nombreCategoria, ResultadoMetrado resultado)
        {
            var tipo = _doc.GetElement(e.GetTypeId()) as ElementType;
            double longitudM = AMetros(ObtenerLongitud(e));
            if (longitudM <= 0)
            {
                resultado.Advertencias.Add($"{nombreCategoria} Id {e.Id}: perfil metálico sin longitud; no se pudo calcular su peso.");
                return null;
            }

            double volumenM3 = AMetrosCubicos(LeerDouble(e, BuiltInParameter.HOST_VOLUME_COMPUTED));
            double areaM2 = ObtenerAreaSeccion(e, tipo, out string fuenteArea);
            if (areaM2 <= 0 && volumenM3 > 0)
            {
                // Último recurso: la sección media que resulta del volumen que informa Revit.
                areaM2 = volumenM3 / longitudM;
                fuenteArea = "Volumen / longitud";
            }
            if (areaM2 <= 0)
            {
                resultado.Advertencias.Add(
                    $"{nombreCategoria} Id {e.Id}: el tipo \"{tipo?.Name ?? e.Name}\" no tiene área de sección; no se pudo calcular su peso.");
                return null;
            }

            // Los perfiles estructurales son de acero al carbono: densidad única (7850 kg/m³ por defecto).
            Material material = ObtenerMaterialEstructural(e);
            double densidad = _opciones.DensidadAceroEstructural;
            Level nivel = ObtenerNivel(e);

            return new ElementoAceroEstructural
            {
                Id = e.Id,
                Categoria = nombreCategoria,
                Nivel = nivel?.Name ?? "(sin nivel)",
                ElevacionNivel = nivel?.Elevation ?? double.MinValue,
                Familia = tipo?.FamilyName ?? e.Category?.Name ?? string.Empty,
                Tipo = tipo?.Name ?? e.Name,
                Marca = LeerTexto(e, BuiltInParameter.ALL_MODEL_MARK),
                Material = material?.Name ?? "(sin material)",
                LongitudM = longitudM,
                AreaSeccionCm2 = areaM2 * 10000.0,
                FuenteArea = fuenteArea,
                DensidadKgM3 = densidad,
                PesoKg = longitudM * areaM2 * densidad,
                VolumenM3 = volumenM3,
            };
        }

        /// <summary>
        /// Área de la sección transversal del perfil en m². Se busca, en orden: el
        /// parámetro "Área de sección" del tipo (perfiles con sección estructural),
        /// la definición de sección estructural de la familia, y un parámetro con
        /// nombre habitual en familias propias.
        /// </summary>
        private static double ObtenerAreaSeccion(Element e, ElementType tipo, out string fuente)
        {
            double area = tipo != null ? LeerDouble(tipo, BuiltInParameter.STRUCTURAL_SECTION_AREA) : 0;
            if (area <= 0) area = LeerDouble(e, BuiltInParameter.STRUCTURAL_SECTION_AREA);
            if (area > 0)
            {
                fuente = "Área de sección del tipo";
                return AMetrosCuadrados(area);
            }

            if (tipo is FamilySymbol simbolo)
            {
                try
                {
                    StructuralSection seccion = simbolo.GetStructuralSection();
                    if (seccion != null && seccion.SectionArea > 0)
                    {
                        fuente = "Sección estructural de la familia";
                        return AMetrosCuadrados(seccion.SectionArea);
                    }
                }
                catch (Exception)
                {
                    // La familia no define sección estructural.
                }
            }

            foreach (Element portador in new[] { (Element)tipo, e })
            {
                if (portador == null) continue;
                foreach (string nombre in NombresParametroAreaSeccion)
                {
                    Parameter p = portador.LookupParameter(nombre);
                    if (p != null && p.StorageType == StorageType.Double && p.HasValue && p.AsDouble() > 0 && EsParametroDeArea(p))
                    {
                        fuente = "Parámetro \"" + nombre + "\"";
                        return AMetrosCuadrados(p.AsDouble());
                    }
                }
            }

            fuente = null;
            return 0;
        }

        /// <summary>True si el parámetro es de disciplina área (evita confundir "A" con otra magnitud).</summary>
        private static bool EsParametroDeArea(Parameter p)
        {
            try
            {
#if REVIT2021
                ForgeTypeId espec = p.Definition.GetSpecTypeId();
#else
                ForgeTypeId espec = p.Definition.GetDataType();
#endif
                return espec != null && espec.Equals(SpecTypeId.Area);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Acero de refuerzo
        // ------------------------------------------------------------------

        private void CalcularAcero(List<CategoriaMetrado> categorias, ResultadoMetrado resultado)
        {
            // El acero se mide en TODAS las armaduras del modelo (el peso se escribe
            // en cada una); las categorías marcadas solo ordenan el resumen.
            var mapaCategorias = CategoriaMetrado.Predeterminadas().ToDictionary(c => new ElementId(c.Categoria), c => c.Nombre);

            var barras = new List<Element>();
            barras.AddRange(new FilteredElementCollector(_doc).OfClass(typeof(Rebar)).ToElements());
            barras.AddRange(new FilteredElementCollector(_doc).OfClass(typeof(RebarInSystem)).ToElements());

            foreach (Element barra in barras)
            {
                try
                {
                    ElementId hostId = barra is Rebar r ? r.GetHostId()
                                     : barra is RebarInSystem ris ? ris.GetHostId()
                                     : ElementId.InvalidElementId;

                    Element host = hostId != ElementId.InvalidElementId ? _doc.GetElement(hostId) : null;
                    if (host?.Category == null) continue;

                    if (!mapaCategorias.TryGetValue(host.Category.Id, out string nombreCategoria))
                    {
                        nombreCategoria = host.Category.Name; // anfitrión de otra categoría
                    }

                    BarraAcero medida = MedirBarra(barra, host, nombreCategoria);
                    if (medida != null) resultado.Acero.Add(medida);
                }
                catch (Exception ex)
                {
                    resultado.Advertencias.Add($"Acero Id {barra.Id}: {ex.Message}");
                }
            }

            // Mallas electrosoldadas (habituales en losas)
            var mallas = new FilteredElementCollector(_doc).OfClass(typeof(FabricSheet)).ToElements();
            foreach (Element elemento in mallas)
            {
                try
                {
                    var malla = (FabricSheet)elemento;
                    Element host = malla.HostId != ElementId.InvalidElementId ? _doc.GetElement(malla.HostId) : null;
                    if (host?.Category == null) continue;
                    if (!mapaCategorias.TryGetValue(host.Category.Id, out string nombreCategoria))
                    {
                        nombreCategoria = host.Category.Name;
                    }

                    BarraAcero medida = MedirMalla(malla, host, nombreCategoria);
                    if (medida != null) resultado.Acero.Add(medida);
                }
                catch (Exception ex)
                {
                    resultado.Advertencias.Add($"Malla Id {elemento.Id}: {ex.Message}");
                }
            }

            if (_opciones.IncluirAcero && barras.Count == 0 && mallas.Count == 0)
            {
                resultado.Advertencias.Add("El modelo no contiene barras de refuerzo ni mallas electrosoldadas.");
            }
        }

        /// <summary>
        /// Mide una malla electrosoldada: usa la masa de la hoja cortada que calcula
        /// Revit a partir del tipo de malla (masa por m²) y del área cortada.
        /// </summary>
        private BarraAcero MedirMalla(FabricSheet malla, Element host, string nombreCategoria)
        {
            var tipoMalla = _doc.GetElement(malla.GetTypeId()) as FabricSheetType;

            double largoPies = LeerDouble(malla, BuiltInParameter.FABRIC_PARAM_CUT_OVERALL_LENGTH);
            double anchoPies = LeerDouble(malla, BuiltInParameter.FABRIC_PARAM_CUT_OVERALL_WIDTH);
            if (largoPies <= 0) largoPies = LeerDouble(malla, BuiltInParameter.FABRIC_PARAM_TOTAL_LENGTH);
            if (anchoPies <= 0) anchoPies = LeerDouble(malla, BuiltInParameter.FABRIC_PARAM_TOTAL_WIDTH);

            double largoM = AMetros(largoPies);
            double anchoM = AMetros(anchoPies);
            double areaM2 = largoM * anchoM;

            // Masa de la hoja cortada (kg). Revit la calcula como masa unitaria × área cortada.
            double masaKg = AKilogramos(LeerDouble(malla, BuiltInParameter.FABRIC_PARAM_CUT_SHEET_MASS));
            string fuente = "Masa de hoja cortada";

            if (masaKg <= 0)
            {
                masaKg = AKilogramos(LeerDouble(malla, BuiltInParameter.FABRIC_PARAM_SHEET_MASS));
                fuente = "Masa de hoja";
            }
            if (masaKg <= 0 && tipoMalla != null)
            {
                // Masa unitaria del tipo (kg/m²) × área
                double masaUnit = LeerDouble(tipoMalla, BuiltInParameter.FABRIC_SHEET_MASSUNIT);
                masaKg = AKilogramosPorM2(masaUnit) * areaM2;
                fuente = "Masa unitaria × área";
            }

            Level nivel = ObtenerNivel(host);

            return new BarraAcero
            {
                Id = malla.Id,
                HostId = host.Id,
                CategoriaHost = nombreCategoria,
                Nivel = nivel?.Name ?? "(sin nivel)",
                ElevacionNivel = nivel?.Elevation ?? double.MinValue,
                TipoBarra = "Malla: " + (tipoMalla?.Name ?? malla.Name),
                DiametroMm = 0,
                Cantidad = 1,
                LongitudUnaBarraM = largoM,
                LongitudTotalM = largoM,
                AreaM2 = areaM2,
                PesoKg = masaKg,
                FuenteLongitud = fuente,
                Particion = LeerTexto(malla, BuiltInParameter.NUMBER_PARTITION_PARAM),
                EsMalla = true,
            };
        }

        private BarraAcero MedirBarra(Element barra, Element host, string nombreCategoria)
        {
            var tipoBarra = _doc.GetElement(barra.GetTypeId()) as RebarBarType;

            double diametroPies = 0;
            if (tipoBarra != null)
            {
                diametroPies = LeerDouble(tipoBarra, BuiltInParameter.REBAR_BAR_DIAMETER);
                if (diametroPies <= 0)
                {
                    Parameter p = tipoBarra.LookupParameter("Bar Diameter") ?? tipoBarra.LookupParameter("Diámetro de barra");
                    if (p != null) diametroPies = p.AsDouble();
                }
            }

            int cantidad = (int)Math.Round(LeerDouble(barra, BuiltInParameter.REBAR_ELEM_QUANTITY_OF_BARS));
            if (cantidad <= 0 && barra is Rebar rb) cantidad = rb.NumberOfBarPositions;
            if (cantidad <= 0) cantidad = 1;

            // "Longitud de barra" (REBAR_ELEM_LENGTH) es la de UNA pieza del conjunto.
            // Para el metrado se necesita "Longitud total de barra"
            // (REBAR_ELEM_TOTAL_LENGTH): suma de todas las piezas, con ganchos y
            // dobleces, y correcta en conjuntos de longitud variable.
            double longitudUnaBarraPies = LeerDouble(barra, BuiltInParameter.REBAR_ELEM_LENGTH);
            double longitudTotalPies = LeerDouble(barra, BuiltInParameter.REBAR_ELEM_TOTAL_LENGTH);
            string fuenteLongitud = "Longitud total de barra";

            if (longitudTotalPies <= 0 && barra is Rebar rebar)
            {
                // Respaldo 1: longitud geométrica del eje de cada posición de barra
                // (incluye ganchos y radios de doblado).
                longitudTotalPies = LongitudGeometrica(rebar);
                fuenteLongitud = "Geometría del eje";
            }

            if (longitudTotalPies <= 0)
            {
                // Respaldo 2: longitud de una pieza × cantidad.
                longitudTotalPies = longitudUnaBarraPies * cantidad;
                fuenteLongitud = "Longitud de barra × cantidad";
            }

            double longitudM = AMetros(longitudTotalPies);
            double diametroMm = AMilimetros(diametroPies);

            double pesoKg = ObtenerMasaTotal(barra);
            if (pesoKg <= 0)
            {
                double pesoPorMetro = ObtenerPesoPorMetro(tipoBarra, diametroMm);
                pesoKg = longitudM * pesoPorMetro;
            }

            Level nivel = ObtenerNivel(host);

            return new BarraAcero
            {
                Id = barra.Id,
                HostId = host.Id,
                CategoriaHost = nombreCategoria,
                Nivel = nivel?.Name ?? "(sin nivel)",
                ElevacionNivel = nivel?.Elevation ?? double.MinValue,
                TipoBarra = tipoBarra?.Name ?? barra.Name,
                DiametroMm = diametroMm,
                Cantidad = cantidad,
                LongitudUnaBarraM = AMetros(longitudUnaBarraPies),
                LongitudTotalM = longitudM,
                FuenteLongitud = fuenteLongitud,
                PesoKg = pesoKg,
                Particion = LeerTexto(barra, BuiltInParameter.NUMBER_PARTITION_PARAM),
            };
        }

        /// <summary>
        /// Suma la longitud del eje de todas las posiciones de barra del conjunto.
        /// Se usa solo si el parámetro "Longitud total de barra" no está disponible.
        /// </summary>
        private static double LongitudGeometrica(Rebar rebar)
        {
            double total = 0;
            int posiciones = Math.Max(1, rebar.NumberOfBarPositions);
            for (int i = 0; i < posiciones; i++)
            {
                if (!rebar.IncludeFirstBar && i == 0) continue;
                if (!rebar.IncludeLastBar && i == posiciones - 1) continue;
                try
                {
                    IList<Curve> curvas = rebar.GetCenterlineCurves(
                        adjustForSelfIntersection: false,
                        suppressHooks: false,
                        suppressBendRadius: false,
                        multiplanarOption: MultiplanarOption.IncludeAllMultiplanarCurves,
                        barPositionIndex: i);
                    foreach (Curve c in curvas) total += c.Length;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Posición no válida: se ignora.
                }
            }
            return total;
        }

        /// <summary>
        /// Peso por metro lineal (kg/m). Si el tipo de barra tiene un parámetro con
        /// el peso unitario se usa ese; si no, se calcula por densidad y diámetro.
        /// </summary>
        private double ObtenerPesoPorMetro(RebarBarType tipoBarra, double diametroMm)
        {
            if (tipoBarra != null)
            {
                var nombres = new List<string>();
                if (!string.IsNullOrWhiteSpace(_opciones.NombreParametroPeso)) nombres.Add(_opciones.NombreParametroPeso.Trim());
                nombres.AddRange(NombresParametroPesoBarra);

                foreach (string nombre in nombres)
                {
                    Parameter p = tipoBarra.LookupParameter(nombre);
                    if (p != null && p.StorageType == StorageType.Double && p.HasValue && p.AsDouble() > 0)
                    {
                        return AKilogramosPorMetro(p);
                    }
                }
            }

            double dM = diametroMm / 1000.0;
            double areaM2 = Math.PI * dM * dM / 4.0;
            return areaM2 * _opciones.DensidadAcero;
        }

        /// <summary>Masa total del conjunto de barras si Revit la expone como parámetro (kg).</summary>
        private static double ObtenerMasaTotal(Element barra)
        {
            foreach (string nombre in NombresParametroMasaTotal)
            {
                Parameter p = barra.LookupParameter(nombre);
                if (p != null && p.StorageType == StorageType.Double && p.HasValue && p.AsDouble() > 0)
                {
                    return AKilogramos(p.AsDouble());
                }
            }
            return 0;
        }

        /// <summary>
        /// Convierte un parámetro de peso por metro a kg/m. Si el parámetro tiene
        /// disciplina "masa por unidad de longitud" se convierte desde las unidades
        /// internas; si es un número sin unidades se asume que ya está en kg/m.
        /// </summary>
        private static double AKilogramosPorMetro(Parameter p)
        {
            try
            {
#if REVIT2021
                ForgeTypeId espec = p.Definition.GetSpecTypeId();
#else
                ForgeTypeId espec = p.Definition.GetDataType();
#endif
                if (espec != null && espec.Equals(SpecTypeId.MassPerUnitLength))
                {
                    return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.KilogramsPerMeter);
                }
            }
            catch (Exception)
            {
                // Sin información de unidades: se asume kg/m.
            }
            return p.AsDouble();
        }

        // ------------------------------------------------------------------
        // Utilidades
        // ------------------------------------------------------------------

        private Level ObtenerNivel(Element e)
        {
            ElementId id = ElementId.InvalidElementId;

            // Vigas: nivel de referencia; columnas: nivel base; resto: LevelId.
            foreach (BuiltInParameter bip in new[]
            {
                BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
                BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
                BuiltInParameter.SCHEDULE_LEVEL_PARAM,
                BuiltInParameter.WALL_BASE_CONSTRAINT,
                BuiltInParameter.LEVEL_PARAM,
            })
            {
                Parameter p = e.get_Parameter(bip);
                if (p != null && p.StorageType == StorageType.ElementId && p.AsElementId() != ElementId.InvalidElementId)
                {
                    id = p.AsElementId();
                    break;
                }
            }

            if (id == ElementId.InvalidElementId) id = e.LevelId;
            if (id == null || id == ElementId.InvalidElementId) return null;

            if (!_cacheNiveles.TryGetValue(id, out Level nivel))
            {
                nivel = _doc.GetElement(id) as Level;
                _cacheNiveles[id] = nivel;
            }
            return nivel;
        }

        private static double LeerDouble(Element e, BuiltInParameter bip)
        {
            Parameter p = e.get_Parameter(bip);
            return p != null && p.StorageType == StorageType.Double && p.HasValue ? p.AsDouble() : 0;
        }

        private static string LeerTexto(Element e, BuiltInParameter bip)
        {
            Parameter p = e.get_Parameter(bip);
            return p != null && p.HasValue ? (p.AsString() ?? string.Empty) : string.Empty;
        }

        private static double AMetros(double pies) => UnitUtils.ConvertFromInternalUnits(pies, UnitTypeId.Meters);
        private static double AMetrosCuadrados(double pies2) => UnitUtils.ConvertFromInternalUnits(pies2, UnitTypeId.SquareMeters);
        private static double AKilogramos(double interno) => UnitUtils.ConvertFromInternalUnits(interno, UnitTypeId.Kilograms);
        private static double AKilogramosPorM2(double interno) => UnitUtils.ConvertFromInternalUnits(interno, UnitTypeId.KilogramsPerSquareMeter);
        private static double AMilimetros(double pies) => UnitUtils.ConvertFromInternalUnits(pies, UnitTypeId.Millimeters);
        private static double AMetrosCubicos(double pies3) => UnitUtils.ConvertFromInternalUnits(pies3, UnitTypeId.CubicMeters);
    }
}
