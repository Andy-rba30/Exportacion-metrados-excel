using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Arba.Comun;
using Autodesk.Revit.DB;

namespace ExportacionMetrados.Core.Metrado.Encofrado
{
    /// <summary>
    /// Calcula el encofrado de los elementos de concreto a partir de su geometría real y de su
    /// contexto (los demás elementos de concreto del modelo):
    ///   1. Cada cara del sólido se clasifica por su normal: lateral (vertical o inclinada más de
    ///      45°), fondo (mira hacia abajo) o superior (mira hacia arriba; nunca se encofra).
    ///   2. La regla del grupo decide qué caras cuentan: vigas laterales + fondo; columnas, muros y
    ///      cimentaciones solo laterales; losas bordes + fondo (salvo la losa sobre terreno).
    ///   3. De cada cara que cuenta se descuenta la superficie en contacto con otro elemento de
    ///      concreto. Para una cara plana se levanta un prisma fino sobre ella (de -tolerancia a
    ///      +tolerancia) y se intersecta (operación booleana) con cada vecino cercano: el área de las
    ///      caras del resultado paralelas a la cara original es exactamente la superficie en contacto
    ///      (la sección de la viga que entra en la columna, la franja de losa que apoya en la viga, el
    ///      ancho de la viga bajo la losa...). Funciona igual si los elementos se solapan, se tocan o
    ///      quedan a una holgura menor que la tolerancia, y estén o no unidos en Revit. Las caras
    ///      curvas (columnas circulares) se muestrean punto a punto con el mismo criterio.
    /// Solo lee geometría: no necesita transacción.
    /// </summary>
    public class CalculadorEncofrado
    {
        /// <summary>|nz| por encima del cual una cara es horizontal (superior o fondo); por debajo, lateral (inclinación > 45°).</summary>
        private const double UmbralHorizontal = 0.7;
        /// <summary>Coseno mínimo para considerar dos normales paralelas.</summary>
        private const double Paralelas = 0.99;
        /// <summary>Paso de la malla de muestreo de las caras curvas, en metros.</summary>
        private const double PasoMuestreoM = 0.025;
        private const int MaxMuestrasPorEje = 80;
        /// <summary>Una losa cuyo punto más bajo queda a menos de esto (m) sobre el nivel más bajo se considera sobre terreno.</summary>
        private const double MargenNivelMasBajoM = 0.30;

        private readonly Document _doc;
        private readonly OpcionesEncofrado _op;
        /// <summary>Tolerancia de contacto en unidades internas (pies): el prisma va de -tol a +tol.</summary>
        private readonly double _tol;
        /// <summary>Espesor de la piel de verificación en unidades internas.</summary>
        private readonly double _espesorPiel;
        private readonly Dictionary<ElementId, Level> _cacheNiveles = new Dictionary<ElementId, Level>();
        private readonly SolidCurveIntersectionOptions _dentro = new SolidCurveIntersectionOptions
        {
            ResultType = SolidCurveIntersectionMode.CurveSegmentsInside,
        };

        private int _booleanos;
        private int _muestreos;

        /// <summary>Un sólido de un elemento de concreto del modelo, con su caja en coordenadas del proyecto.</summary>
        private sealed class SolidoVecino
        {
            public ElementId Id;
            public string Categoria;
            public string Tipo;
            public Solid Solido;
            public BoundingBoxXYZ Caja;
        }

        private sealed class ElementoDeConcreto
        {
            public Element Elemento;
            public CategoriaMetrado Grupo;
            public List<Solid> Solidos;
            public BoundingBoxXYZ Caja;
        }

        public CalculadorEncofrado(Document doc, OpcionesEncofrado opciones)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _op = opciones ?? throw new ArgumentNullException(nameof(opciones));
            double mm = Math.Max(1, _op.ToleranciaContactoMm);
            _tol = UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
            _espesorPiel = UnitUtils.ConvertToInternalUnits(PielEncofrado.EspesorMm, UnitTypeId.Millimeters);
        }

        public ResultadoEncofrado Calcular()
        {
            var cronometro = Stopwatch.StartNew();
            var resultado = new ResultadoEncofrado();
            _booleanos = 0;
            _muestreos = 0;

            var reglas = _op.Reglas.Where(r => r.Seleccionada).ToDictionary(r => r.Grupo.NombreParticion, StringComparer.Ordinal);

            // 1. Todos los elementos de concreto del modelo (el contexto), con su grupo y su geometría.
            var concreto = new List<ElementoDeConcreto>();
            var categorias = _op.Catalogo.SelectMany(g => g.Categorias).Distinct().ToList();
            foreach (Element e in CategoriaMetrado.Colector(_doc, categorias).ToElements())
            {
                try
                {
                    // La piel de verificación de un cálculo anterior no es concreto ni contexto.
                    if (PielEncofrado.EsPiel(e)) continue;
                    CategoriaMetrado grupo = ClasificadorElementos.GrupoDe(_doc, e, _op.Catalogo);
                    if (grupo == null) continue;
                    if (ClasificadorElementos.ClasificacionActual(_doc, e) != ClasificadorElementos.ValorConcreto) continue;

                    List<Solid> solidos = SolidosDe(e);
                    if (solidos.Count == 0) continue;

                    concreto.Add(new ElementoDeConcreto
                    {
                        Elemento = e,
                        Grupo = grupo,
                        Solidos = solidos,
                        Caja = Union(solidos.Select(s => CajaMundo(s, 2 * _tol))),
                    });
                }
                catch (Exception ex)
                {
                    resultado.Advertencias.Add($"Id {e.Id}: no se pudo leer su geometría ({ex.Message}).");
                }
            }
            resultado.ElementosConcreto = concreto.Count;

            // 2. Los sólidos vecinos, con la caja ampliada en la tolerancia.
            var vecinos = new List<SolidoVecino>();
            foreach (ElementoDeConcreto info in concreto)
            {
                string categoria = info.Elemento.Category?.Name ?? string.Empty;
                string tipo = NombreTipo(info.Elemento);
                foreach (Solid s in info.Solidos)
                {
                    vecinos.Add(new SolidoVecino
                    {
                        Id = info.Elemento.Id, Categoria = categoria, Tipo = tipo, Solido = s, Caja = CajaMundo(s, 2 * _tol),
                    });
                }
            }

            double nivelMasBajo = NivelMasBajo();

            // 3. Metrar los elementos de los grupos con regla.
            foreach (ElementoDeConcreto info in concreto)
            {
                if (!reglas.TryGetValue(info.Grupo.NombreParticion, out ReglaEncofrado regla)) continue;
                try
                {
                    resultado.Elementos.Add(Medir(info, regla, vecinos, nivelMasBajo));
                }
                catch (Exception ex)
                {
                    resultado.Advertencias.Add($"{info.Grupo.Nombre} Id {info.Elemento.Id}: no se pudo calcular el encofrado ({ex.Message}).");
                }
            }

            resultado.Duracion = cronometro.Elapsed;
            resultado.OperacionesBooleanas = _booleanos;
            resultado.CarasMuestreadas = _muestreos;
            return resultado;
        }

        // ------------------------------------------------------------------
        // Un elemento
        // ------------------------------------------------------------------

        private ElementoEncofrado Medir(ElementoDeConcreto info, ReglaEncofrado regla, List<SolidoVecino> vecinos, double nivelMasBajo)
        {
            Element e = info.Elemento;
            Level nivel = ObtenerNivel(e);
            var tipo = _doc.GetElement(e.GetTypeId()) as ElementType;
            Material material = null;
            try { material = CalculadorMetrado.MaterialEstructuralDe(_doc, e); } catch (Exception) { }

            var r = new ElementoEncofrado
            {
                Id = e.Id,
                Grupo = regla.Grupo.Nombre,
                ElementoTexto = ArbaSharedParams.GetText(e, ArbaContract.Elemento).Trim(),
                Nivel = nivel?.Name ?? "(sin nivel)",
                ElevacionNivel = nivel?.Elevation ?? double.MinValue,
                Familia = tipo?.FamilyName ?? e.Category?.Name ?? string.Empty,
                Tipo = tipo?.Name ?? e.Name,
                Marca = LeerTexto(e, BuiltInParameter.ALL_MODEL_MARK),
                Material = material?.Name ?? string.Empty,
            };

            // Fondo de las losas: según la opción (nunca / siempre / salvo sobre terreno).
            bool fondoPermitido = regla.Fondo;
            if (fondoPermitido && regla.Grupo.Categoria == BuiltInCategory.OST_Floors && _op.FondoLosas != ReglaFondoLosas.Siempre)
            {
                if (_op.FondoLosas == ReglaFondoLosas.Nunca)
                {
                    fondoPermitido = false;
                    r.FondoExcluido = true;
                    r.Nota = "Fondo no contado (opción).";
                }
                else if (info.Caja.Min.Z <= nivelMasBajo + UnitUtils.ConvertToInternalUnits(MargenNivelMasBajoM, UnitTypeId.Meters))
                {
                    fondoPermitido = false;
                    r.FondoExcluido = true;
                    r.Nota = "Fondo no contado: losa apoyada en el nivel más bajo (sobre terreno).";
                }
            }

            // Vecinos que pueden tocar a este elemento (por caja). Incluyen los demás sólidos del propio
            // elemento: la cara interior entre dos sólidos de una misma familia no se encofra.
            List<SolidoVecino> candidatosElemento = _op.DescontarContactos
                ? vecinos.Where(v => Intersecan(v.Caja, info.Caja)).ToList()
                : new List<SolidoVecino>();

            foreach (Solid solido in info.Solidos)
            {
                foreach (Face cara in solido.Faces)
                {
                    double area = cara.Area;
                    if (area < 1e-6) continue;

                    bool plana = cara is PlanarFace;
                    bool invertir = false;
                    XYZ normal = plana ? ((PlanarFace)cara).FaceNormal : NormalCurva(cara, solido, out invertir);
                    if (normal == null)
                    {
                        r.Aproximado = true;
                        continue;
                    }

                    CaraEncofrado clase = Clasificar(normal);
                    if (clase == CaraEncofrado.Superior)
                    {
                        r.SuperiorM2 += M2(area);
                        continue;
                    }

                    bool cuenta = clase == CaraEncofrado.Lateral ? regla.Laterales : fondoPermitido;
                    if (!cuenta)
                    {
                        if (clase == CaraEncofrado.Fondo) r.FondoNoContadoM2 += M2(area);
                        continue;
                    }

                    if (clase == CaraEncofrado.Lateral) r.LateralBrutaM2 += M2(area);
                    else r.FondoBrutaM2 += M2(area);

                    // Piel de verificación: prisma fino hacia fuera sobre la cara plana, o sus triángulos si es curva.
                    Solid piel = null;
                    if (_op.CrearPiel)
                    {
                        if (plana) piel = PrismaPiel((PlanarFace)cara, normal);
                        else TriangulosPiel(cara, invertir, r);
                    }

                    List<SolidoVecino> candidatos = null;
                    if (candidatosElemento.Count > 0)
                    {
                        BoundingBoxXYZ cajaCara = CajaDeCara(cara, 2 * _tol);
                        if (cajaCara != null)
                        {
                            candidatos = candidatosElemento
                                .Where(v => !ReferenceEquals(v.Solido, solido) && Intersecan(v.Caja, cajaCara))
                                .ToList();
                        }
                    }

                    if (candidatos != null && candidatos.Count > 0)
                    {
                        double contacto = Math.Min(area, Contacto(cara, normal, plana, invertir, candidatos, r, clase, ref piel, out bool pielExacta));
                        if (contacto > 0)
                        {
                            if (clase == CaraEncofrado.Lateral) r.DescuentoLateralM2 += M2(contacto);
                            else r.DescuentoFondoM2 += M2(contacto);
                            // La piel de una cara curva o medida por muestreo se pinta entera, sin el descuento.
                            if (_op.CrearPiel && !pielExacta) r.PielCarasSinDescuento++;
                        }
                    }

                    if (piel != null)
                    {
                        if (piel.Volume > 1e-12) r.PielSolidos.Add(piel);
                        else piel.Dispose();
                    }
                }
            }

            return r;
        }

        private static CaraEncofrado Clasificar(XYZ normal)
        {
            if (normal.Z > UmbralHorizontal) return CaraEncofrado.Superior;
            if (normal.Z < -UmbralHorizontal) return CaraEncofrado.Fondo;
            return CaraEncofrado.Lateral;
        }

        // ------------------------------------------------------------------
        // Contacto con los vecinos
        // ------------------------------------------------------------------

        /// <summary>
        /// Superficie de la cara (unidades internas) en contacto con alguno de los candidatos. Si hay piel de
        /// verificación (<paramref name="piel"/>) y el cálculo es exacto (cara plana por booleanos), se le
        /// resta la huella de cada contacto; <paramref name="pielExacta"/> dice si fue así.
        /// </summary>
        private double Contacto(Face cara, XYZ normal, bool plana, bool invertir, List<SolidoVecino> candidatos,
            ElementoEncofrado r, CaraEncofrado clase, ref Solid piel, out bool pielExacta)
        {
            pielExacta = false;
            if (plana)
            {
                double? exacto = ContactoPorBooleano((PlanarFace)cara, normal, candidatos, r, clase, ref piel);
                if (exacto.HasValue)
                {
                    pielExacta = true;
                    return exacto.Value;
                }
            }
            else
            {
                r.CarasCurvas++;
            }
            r.Aproximado = true;
            return ContactoPorMuestreo(cara, invertir, candidatos, r, clase);
        }

        /// <summary>
        /// Prisma fino sobre la cara plana (de -tolerancia a +tolerancia) intersectado con cada
        /// vecino: el área de las caras del resultado paralelas a la cara (mirando hacia fuera) es
        /// la superficie en contacto. Los resultados de varios vecinos se unen para no contar dos
        /// veces un mismo punto. Null si la geometría no lo permite (se recurre al muestreo).
        /// </summary>
        private double? ContactoPorBooleano(PlanarFace cara, XYZ normal, List<SolidoVecino> candidatos, ElementoEncofrado r, CaraEncofrado clase,
            ref Solid piel)
        {
            Solid prisma;
            try
            {
                IList<CurveLoop> bucles = cara.GetEdgesAsCurveLoops();
                Transform haciaDentro = Transform.CreateTranslation(normal.Multiply(-_tol));
                var movidos = bucles.Select(b => CurveLoop.CreateViaTransform(b, haciaDentro)).ToList();
                prisma = GeometryCreationUtilities.CreateExtrusionGeometry(movidos, normal, 2 * _tol);
            }
            catch (Exception)
            {
                return null;
            }
            if (prisma == null || prisma.Volume <= 0) return null;

            Solid acumulado = null;
            bool unionOk = true;
            double suma = 0;
            // Los contactos se anotan al final: si un booleano falla, la cara pasa a muestreo y es este quien los anota.
            var contactos = new List<ContactoEncofrado>();
            try
            {
                foreach (SolidoVecino v in candidatos)
                {
                    Solid interseccion;
                    try
                    {
                        _booleanos++;
                        interseccion = BooleanOperationsUtils.ExecuteBooleanOperation(prisma, v.Solido, BooleanOperationsType.Intersect);
                    }
                    catch (Exception)
                    {
                        // Geometría que el motor booleano no digiere: toda la cara pasa a muestreo.
                        return null;
                    }
                    if (interseccion == null) continue;
                    if (interseccion.Volume < 1e-9)
                    {
                        interseccion.Dispose();
                        continue;
                    }

                    double areaVecino = AreaCarasParalelas(interseccion, normal);
                    if (piel != null && areaVecino > 1e-9) RestarHuella(ref piel, interseccion, cara, normal, r);
                    if (areaVecino > 1e-9)
                    {
                        contactos.Add(new ContactoEncofrado
                        {
                            ConId = v.Id, ConCategoria = v.Categoria, ConTipo = v.Tipo, Cara = clase, AreaM2 = M2(areaVecino),
                        });
                    }
                    suma += areaVecino;

                    if (acumulado == null)
                    {
                        acumulado = interseccion;
                    }
                    else if (unionOk)
                    {
                        try
                        {
                            _booleanos++;
                            Solid union = BooleanOperationsUtils.ExecuteBooleanOperation(acumulado, interseccion, BooleanOperationsType.Union);
                            acumulado.Dispose();
                            interseccion.Dispose();
                            acumulado = union;
                        }
                        catch (Exception)
                        {
                            unionOk = false;
                            interseccion.Dispose();
                        }
                    }
                    else
                    {
                        interseccion.Dispose();
                    }
                }

                r.Contactos.AddRange(contactos);
                if (acumulado == null) return 0;
                if (unionOk) return AreaCarasParalelas(acumulado, normal);
                r.Aproximado = true;
                return suma;
            }
            finally
            {
                prisma.Dispose();
                acumulado?.Dispose();
            }
        }

        /// <summary>Área de las caras planas del sólido cuya normal coincide con la dada (las que miran hacia fuera de la cara original).</summary>
        private static double AreaCarasParalelas(Solid solido, XYZ normal)
        {
            double area = 0;
            foreach (Face f in solido.Faces)
            {
                if (f is PlanarFace pf && pf.FaceNormal.DotProduct(normal) > Paralelas) area += pf.Area;
            }
            return area;
        }

        // ------------------------------------------------------------------
        // Piel de verificación
        // ------------------------------------------------------------------

        /// <summary>Prisma fino hacia fuera (0 a espesor de la piel) sobre la cara plana; null si la geometría no lo permite.</summary>
        private Solid PrismaPiel(PlanarFace cara, XYZ normal)
        {
            try
            {
                Solid s = GeometryCreationUtilities.CreateExtrusionGeometry(cara.GetEdgesAsCurveLoops(), normal, _espesorPiel);
                if (s != null && s.Volume > 1e-12) return s;
                s?.Dispose();
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Resta a la piel la huella de un contacto: cada cara del sólido de intersección paralela a la cara
        /// original (la superficie en contacto) se extruye de -tolerancia a +tolerancia + espesor y se
        /// descuenta de la piel. Si una resta falla, la piel conserva esa zona y se anota.
        /// </summary>
        private void RestarHuella(ref Solid piel, Solid interseccion, PlanarFace cara, XYZ normal, ElementoEncofrado r)
        {
            foreach (Face f in interseccion.Faces)
            {
                if (!(f is PlanarFace pf) || pf.FaceNormal.DotProduct(normal) <= Paralelas || pf.Area < 1e-9) continue;
                Solid huella = null;
                try
                {
                    double distancia = (pf.Origin - cara.Origin).DotProduct(normal);
                    Transform alPlanoInterior = Transform.CreateTranslation(normal.Multiply(-distancia - _tol));
                    var bucles = pf.GetEdgesAsCurveLoops().Select(b => CurveLoop.CreateViaTransform(b, alPlanoInterior)).ToList();
                    huella = GeometryCreationUtilities.CreateExtrusionGeometry(bucles, normal, 2 * _tol + _espesorPiel);
                    _booleanos++;
                    Solid resto = BooleanOperationsUtils.ExecuteBooleanOperation(piel, huella, BooleanOperationsType.Difference);
                    if (resto == null) continue;
                    piel.Dispose();
                    piel = resto;
                }
                catch (Exception)
                {
                    r.PielCarasSinDescuento++;
                }
                finally
                {
                    huella?.Dispose();
                }
            }
        }

        /// <summary>
        /// Piel de una cara curva: sus triángulos desplazados 1 mm hacia fuera (para que no coincidan con la
        /// cara del elemento), orientados hacia fuera. Sin descuentos: la cara se pinta entera.
        /// </summary>
        private static void TriangulosPiel(Face cara, bool invertir, ElementoEncofrado r)
        {
            try
            {
                Mesh malla = cara.Triangulate();
                if (malla == null) return;
                double separacion = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
                for (int i = 0; i < malla.NumTriangles; i++)
                {
                    MeshTriangle t = malla.get_Triangle(i);
                    XYZ a = t.get_Vertex(0), b = t.get_Vertex(1), c = t.get_Vertex(2);
                    XYZ n = (b - a).CrossProduct(c - a);
                    if (n.GetLength() < 1e-12) continue;
                    n = n.Normalize();

                    XYZ centro = (a + b + c) / 3;
                    try
                    {
                        IntersectionResult proyeccion = cara.Project(centro);
                        if (proyeccion != null)
                        {
                            XYZ normalCara = cara.ComputeNormal(proyeccion.UVPoint);
                            if (invertir) normalCara = normalCara.Negate();
                            if (n.DotProduct(normalCara) < 0)
                            {
                                n = n.Negate();
                                XYZ aux = b;
                                b = c;
                                c = aux;
                            }
                        }
                    }
                    catch (Exception) { }

                    XYZ d = n.Multiply(separacion);
                    r.PielTriangulos.Add(new[] { a + d, b + d, c + d });
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Malla de puntos sobre la cara (unos 2,5 cm): cada punto está en contacto si el segmento
        /// de -tolerancia a +tolerancia a lo largo de la normal entra en algún vecino. La
        /// superficie en contacto es la fracción de puntos en contacto por el área de la cara.
        /// </summary>
        private double ContactoPorMuestreo(Face cara, bool invertir, List<SolidoVecino> candidatos, ElementoEncofrado r, CaraEncofrado clase)
        {
            _muestreos++;
            BoundingBoxUV caja;
            try { caja = cara.GetBoundingBox(); }
            catch (Exception) { return 0; }

            double paso = UnitUtils.ConvertToInternalUnits(PasoMuestreoM, UnitTypeId.Meters);
            double anchoU = caja.Max.U - caja.Min.U, anchoV = caja.Max.V - caja.Min.V;
            double largoU = anchoU, largoV = anchoV;
            try
            {
                Transform derivadas = cara.ComputeDerivatives(new UV(caja.Min.U + anchoU / 2, caja.Min.V + anchoV / 2));
                largoU = derivadas.BasisX.GetLength() * anchoU;
                largoV = derivadas.BasisY.GetLength() * anchoV;
            }
            catch (Exception) { }
            int nu = Math.Min(MaxMuestrasPorEje, Math.Max(2, (int)Math.Ceiling(largoU / paso)));
            int nv = Math.Min(MaxMuestrasPorEje, Math.Max(2, (int)Math.Ceiling(largoV / paso)));

            int dentro = 0, enContacto = 0;
            var porVecino = new Dictionary<SolidoVecino, int>();
            for (int i = 0; i < nu; i++)
            {
                for (int j = 0; j < nv; j++)
                {
                    var uv = new UV(caja.Min.U + (i + 0.5) * anchoU / nu, caja.Min.V + (j + 0.5) * anchoV / nv);
                    XYZ p, n;
                    try
                    {
                        if (!cara.IsInside(uv)) continue;
                        p = cara.Evaluate(uv);
                        n = cara.ComputeNormal(uv);
                    }
                    catch (Exception) { continue; }
                    dentro++;
                    if (invertir) n = n.Negate();

                    Line segmento;
                    try { segmento = Line.CreateBound(p - n.Multiply(_tol), p + n.Multiply(_tol)); }
                    catch (Exception) { continue; }

                    foreach (SolidoVecino v in candidatos)
                    {
                        if (!Contiene(v.Caja, p)) continue;
                        try
                        {
                            SolidCurveIntersection corte = v.Solido.IntersectWithCurve(segmento, _dentro);
                            if (corte == null || corte.SegmentCount == 0) continue;
                        }
                        catch (Exception) { continue; }

                        enContacto++;
                        porVecino.TryGetValue(v, out int k);
                        porVecino[v] = k + 1;
                        break;
                    }
                }
            }

            if (dentro == 0) return 0;
            double area = cara.Area;
            foreach (KeyValuePair<SolidoVecino, int> par in porVecino)
            {
                r.Contactos.Add(new ContactoEncofrado
                {
                    ConId = par.Key.Id, ConCategoria = par.Key.Categoria, ConTipo = par.Key.Tipo, Cara = clase,
                    AreaM2 = M2(area * par.Value / dentro),
                });
            }
            return area * enContacto / dentro;
        }

        /// <summary>
        /// Normal (hacia fuera del sólido) de una cara curva, evaluada en un punto interior de la
        /// cara. Si la superficie está orientada hacia dentro se invierte (<paramref name="invertir"/>
        /// lo recuerda para el muestreo). Null si no se puede evaluar.
        /// </summary>
        private XYZ NormalCurva(Face cara, Solid solido, out bool invertir)
        {
            invertir = false;
            try
            {
                UV uv = PuntoInterior(cara);
                XYZ n = cara.ComputeNormal(uv).Normalize();
                XYZ p = cara.Evaluate(uv);

                // Un segmento corto hacia +n no debe quedar dentro del propio sólido.
                double a = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
                double b = UnitUtils.ConvertToInternalUnits(8, UnitTypeId.Millimeters);
                Line sonda = Line.CreateBound(p + n.Multiply(a), p + n.Multiply(b));
                SolidCurveIntersection corte = solido.IntersectWithCurve(sonda, _dentro);
                if (corte != null && corte.SegmentCount > 0)
                {
                    invertir = true;
                    n = n.Negate();
                }
                return n;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Un punto (UV) dentro de la cara: el centro de su caja o, si cae en un vano, el primero de una malla gruesa.</summary>
        private static UV PuntoInterior(Face cara)
        {
            BoundingBoxUV caja = cara.GetBoundingBox();
            var centro = new UV((caja.Min.U + caja.Max.U) / 2, (caja.Min.V + caja.Max.V) / 2);
            try { if (cara.IsInside(centro)) return centro; } catch (Exception) { return centro; }

            const int n = 7;
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    var uv = new UV(caja.Min.U + (i + 0.5) * (caja.Max.U - caja.Min.U) / n,
                                    caja.Min.V + (j + 0.5) * (caja.Max.V - caja.Min.V) / n);
                    try { if (cara.IsInside(uv)) return uv; } catch (Exception) { }
                }
            }
            return centro;
        }

        // ------------------------------------------------------------------
        // Geometría y cajas
        // ------------------------------------------------------------------

        /// <summary>Sólidos con volumen del elemento, en coordenadas del proyecto (instancias incluidas).</summary>
        private static List<Solid> SolidosDe(Element e)
        {
            var lista = new List<Solid>();
            var opciones = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false };
            GeometryElement geometria = e.get_Geometry(opciones);
            if (geometria != null) Recoger(geometria, lista);
            return lista;
        }

        private static void Recoger(GeometryElement geometria, List<Solid> lista)
        {
            foreach (GeometryObject g in geometria)
            {
                if (g is Solid s)
                {
                    if (s.Volume > 1e-9 && s.Faces.Size > 0) lista.Add(s);
                }
                else if (g is GeometryInstance instancia)
                {
                    GeometryElement interior = instancia.GetInstanceGeometry();
                    if (interior != null) Recoger(interior, lista);
                }
            }
        }

        /// <summary>Caja del sólido en coordenadas del proyecto (la de la API viene en el sistema de su Transform), ampliada.</summary>
        private static BoundingBoxXYZ CajaMundo(Solid solido, double ampliar)
        {
            BoundingBoxXYZ local = solido.GetBoundingBox();
            Transform t = local.Transform ?? Transform.Identity;
            XYZ min = null, max = null;
            foreach (XYZ esquina in Esquinas(local.Min, local.Max))
            {
                XYZ p = t.OfPoint(esquina);
                min = min == null ? p : new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                max = max == null ? p : new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
            }
            return Ampliar(min, max, ampliar);
        }

        private static IEnumerable<XYZ> Esquinas(XYZ min, XYZ max)
        {
            for (int i = 0; i < 8; i++)
            {
                yield return new XYZ((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            }
        }

        /// <summary>Caja de la cara a partir de sus aristas teseladas, ampliada. Null si no tiene aristas.</summary>
        private static BoundingBoxXYZ CajaDeCara(Face cara, double ampliar)
        {
            XYZ min = null, max = null;
            try
            {
                foreach (EdgeArray bucle in cara.EdgeLoops)
                {
                    foreach (Edge arista in bucle)
                    {
                        foreach (XYZ p in arista.Tessellate())
                        {
                            min = min == null ? p : new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                            max = max == null ? p : new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
                        }
                    }
                }
            }
            catch (Exception) { }
            return min == null ? null : Ampliar(min, max, ampliar);
        }

        private static BoundingBoxXYZ Ampliar(XYZ min, XYZ max, double d)
        {
            var v = new XYZ(d, d, d);
            return new BoundingBoxXYZ { Min = min - v, Max = max + v, Transform = Transform.Identity };
        }

        private static BoundingBoxXYZ Union(IEnumerable<BoundingBoxXYZ> cajas)
        {
            XYZ min = null, max = null;
            foreach (BoundingBoxXYZ c in cajas)
            {
                min = min == null ? c.Min : new XYZ(Math.Min(min.X, c.Min.X), Math.Min(min.Y, c.Min.Y), Math.Min(min.Z, c.Min.Z));
                max = max == null ? c.Max : new XYZ(Math.Max(max.X, c.Max.X), Math.Max(max.Y, c.Max.Y), Math.Max(max.Z, c.Max.Z));
            }
            return new BoundingBoxXYZ { Min = min ?? XYZ.Zero, Max = max ?? XYZ.Zero, Transform = Transform.Identity };
        }

        private static bool Intersecan(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            return a.Min.X <= b.Max.X && a.Max.X >= b.Min.X &&
                   a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y &&
                   a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;
        }

        private static bool Contiene(BoundingBoxXYZ caja, XYZ p)
        {
            return p.X >= caja.Min.X && p.X <= caja.Max.X &&
                   p.Y >= caja.Min.Y && p.Y <= caja.Max.Y &&
                   p.Z >= caja.Min.Z && p.Z <= caja.Max.Z;
        }

        // ------------------------------------------------------------------
        // Datos del elemento
        // ------------------------------------------------------------------

        /// <summary>Elevación (coordenadas del proyecto) del nivel más bajo; -infinito si no hay niveles.</summary>
        private double NivelMasBajo()
        {
            double minimo = double.PositiveInfinity;
            foreach (Level nivel in new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>())
            {
                try { minimo = Math.Min(minimo, nivel.ProjectElevation); }
                catch (Exception) { }
            }
            return double.IsPositiveInfinity(minimo) ? double.NegativeInfinity : minimo;
        }

        /// <summary>Nivel del elemento: referencia (vigas), base (columnas, muros) o el suyo.</summary>
        private Level ObtenerNivel(Element e)
        {
            ElementId id = ElementId.InvalidElementId;
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

        private string NombreTipo(Element e)
        {
            var tipo = _doc.GetElement(e.GetTypeId()) as ElementType;
            if (tipo == null) return e.Name;
            return string.IsNullOrEmpty(tipo.FamilyName) || tipo.FamilyName == tipo.Name ? tipo.Name : tipo.FamilyName + ": " + tipo.Name;
        }

        private static string LeerTexto(Element e, BuiltInParameter bip)
        {
            Parameter p = e.get_Parameter(bip);
            return p != null && p.HasValue ? (p.AsString() ?? string.Empty) : string.Empty;
        }

        private static double M2(double pies2) => UnitUtils.ConvertFromInternalUnits(pies2, UnitTypeId.SquareMeters);
    }
}
