using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ExportacionMetrados.Core.Metrado.Encofrado
{
    /// <summary>
    /// Piel de encofrado de verificación: por cada elemento metrado, un modelo genérico auxiliar
    /// (<see cref="DirectShape"/>) con un sólido fino sobre cada cara que se encofra, ya descontados los
    /// contactos con los vecinos (la misma geometría con la que se metró), y un material propio por grupo
    /// con su color (Vigas, Columnas, Cimentaciones, Losas, Muros; la misma paleta que los filtros de
    /// vista de concreto). Se ve en cualquier vista sombreada. Las caras curvas se pintan enteras, sin
    /// descuentos. Las pieles se reconocen por su <see cref="DirectShape.ApplicationId"/>, se reemplazan en
    /// cada cálculo y las quita "Limpiar modelo". El metrado las ignora: no son concreto ni contexto.
    /// Debe usarse dentro de una transacción abierta.
    /// </summary>
    public static class PielEncofrado
    {
        /// <summary>Identificador de aplicación de los DirectShape de la piel.</summary>
        public const string AppId = "ExportacionMetrados.PielEncofrado";
        /// <summary>Nombre de los elementos: "Piel de encofrado - {grupo}".</summary>
        public const string PrefijoNombre = "Piel de encofrado - ";
        /// <summary>Nombre de los materiales: "Piel de encofrado - {grupo}".</summary>
        public const string PrefijoMaterial = "Piel de encofrado - ";
        /// <summary>Espesor de la piel (mm).</summary>
        public const double EspesorMm = 5;

        private static readonly Color ColorPorDefecto = new Color(128, 128, 128);

        /// <summary>True si el elemento es una piel de encofrado creada por el plugin.</summary>
        public static bool EsPiel(Element e)
        {
            try { return e is DirectShape ds && string.Equals(ds.ApplicationId, AppId, StringComparison.Ordinal); }
            catch (Exception) { return false; }
        }

        /// <summary>Pieles de encofrado del proyecto.</summary>
        public static List<DirectShape> Existentes(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(DirectShape))
                .Cast<DirectShape>()
                .Where(EsPiel)
                .ToList();
        }

        /// <summary>
        /// Elimina todas las pieles del proyecto y, si se pide, sus materiales. Devuelve el número de
        /// pieles eliminadas. Dentro de una transacción.
        /// </summary>
        public static int Eliminar(Document doc, bool incluirMateriales)
        {
            int n = 0;
            foreach (DirectShape ds in Existentes(doc))
            {
                try
                {
                    doc.Delete(ds.Id);
                    n++;
                }
                catch (Exception) { }
            }

            if (incluirMateriales)
            {
                var materiales = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material))
                    .Cast<Material>()
                    .Where(m => m.Name.StartsWith(PrefijoMaterial, StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.Id)
                    .ToList();
                foreach (ElementId id in materiales)
                {
                    try { doc.Delete(id); }
                    catch (Exception) { }
                }
            }
            return n;
        }

        /// <summary>
        /// Reemplaza las pieles del proyecto por las de los elementos calculados. Devuelve cuántas se
        /// crearon; <paramref name="carasSinDescuento"/> cuenta las caras pintadas enteras aunque tengan
        /// contacto (curvas, muestreadas o cuyo descuento falló). Dentro de una transacción.
        /// </summary>
        public static int Crear(Document doc, IEnumerable<ElementoEncofrado> elementos, List<string> advertencias, out int carasSinDescuento)
        {
            Eliminar(doc, incluirMateriales: false);

            var materiales = new Dictionary<string, ElementId>(StringComparer.Ordinal);
            var categoria = new ElementId(BuiltInCategory.OST_GenericModel);
            int n = 0;
            carasSinDescuento = 0;

            foreach (ElementoEncofrado e in elementos)
            {
                carasSinDescuento += e.PielCarasSinDescuento;
                if (e.PielSolidos.Count == 0 && e.PielTriangulos.Count == 0) continue;
                try
                {
                    ElementId material = MaterialDe(doc, e.Grupo, materiales, advertencias);
                    IList<GeometryObject> geometria = Geometria(e, material);
                    if (geometria.Count == 0)
                    {
                        advertencias.Add($"Piel de encofrado: no se pudo construir la geometría del elemento {e.Id} ({e.Tipo}).");
                        continue;
                    }

                    DirectShape ds = DirectShape.CreateElement(doc, categoria);
                    ds.ApplicationId = AppId;
                    ds.ApplicationDataId = e.Id.ToString();
                    ds.SetShape(geometria);
                    try { ds.Name = PrefijoNombre + e.Grupo; } catch (Exception) { }

                    Parameter comentarios = ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (comentarios != null && !comentarios.IsReadOnly)
                    {
                        comentarios.Set($"Piel de encofrado de verificación · {e.Grupo} · elemento {e.Id} ({e.Tipo}) · {e.TotalM2:0.00} m²");
                    }
                    n++;
                }
                catch (Exception ex)
                {
                    advertencias.Add($"Piel de encofrado del elemento {e.Id} ({e.Tipo}): {ex.Message}");
                }
            }
            return n;
        }

        // ------------------------------------------------------------------
        // Geometría
        // ------------------------------------------------------------------

        /// <summary>Sólidos (caras planas con sus bucles) y mallas (triángulos de las caras curvas) de la piel de un elemento.</summary>
        private static IList<GeometryObject> Geometria(ElementoEncofrado e, ElementId material)
        {
            var geometria = new List<GeometryObject>();
            foreach (Solid s in e.PielSolidos)
            {
                if (s == null) continue;
                List<TessellatedFace> caras = CarasDe(s, material);
                if (caras.Count < 4) continue;
                geometria.AddRange(Construir(caras, cerrado: true));
            }
            if (e.PielTriangulos.Count > 0)
            {
                var triangulos = new List<TessellatedFace>();
                foreach (XYZ[] t in e.PielTriangulos)
                {
                    try { triangulos.Add(new TessellatedFace(t, material)); }
                    catch (Exception) { }
                }
                if (triangulos.Count > 0) geometria.AddRange(Construir(triangulos, cerrado: false));
            }
            return geometria;
        }

        /// <summary>Caras del sólido como polígonos (bucle exterior primero y los huecos después) con el material.</summary>
        private static List<TessellatedFace> CarasDe(Solid s, ElementId material)
        {
            var lista = new List<TessellatedFace>();
            foreach (Face f in s.Faces)
            {
                try
                {
                    var bucles = new List<IList<XYZ>>();
                    foreach (CurveLoop bucle in f.GetEdgesAsCurveLoops())
                    {
                        var puntos = new List<XYZ>();
                        foreach (Curve c in bucle)
                        {
                            IList<XYZ> tramo = c.Tessellate();
                            for (int i = 0; i < tramo.Count - 1; i++) puntos.Add(tramo[i]);
                        }
                        if (puntos.Count >= 3) bucles.Add(puntos);
                    }
                    if (bucles.Count == 0) continue;
                    if (bucles.Count > 1) bucles = bucles.OrderByDescending(Area).ToList();
                    lista.Add(new TessellatedFace(bucles, material));
                }
                catch (Exception) { }
            }
            return lista;
        }

        /// <summary>Área de un polígono 3D (método de Newell), para distinguir el bucle exterior de los huecos.</summary>
        private static double Area(IList<XYZ> p)
        {
            XYZ suma = XYZ.Zero;
            for (int i = 0; i < p.Count; i++) suma += p[i].CrossProduct(p[(i + 1) % p.Count]);
            return suma.GetLength() / 2;
        }

        /// <summary>Construye un sólido (o una malla si Revit no lo cierra) a partir de las caras.</summary>
        private static IList<GeometryObject> Construir(List<TessellatedFace> caras, bool cerrado)
        {
            try
            {
                var constructor = new TessellatedShapeBuilder
                {
                    Target = TessellatedShapeBuilderTarget.AnyGeometry,
                    Fallback = TessellatedShapeBuilderFallback.Mesh,
                };
                constructor.OpenConnectedFaceSet(cerrado);
                int agregadas = 0;
                foreach (TessellatedFace cara in caras)
                {
                    try
                    {
                        constructor.AddFace(cara);
                        agregadas++;
                    }
                    catch (Exception) { }
                }
                constructor.CloseConnectedFaceSet();
                if (agregadas == 0) return new List<GeometryObject>();
                constructor.Build();
                return constructor.GetBuildResult().GetGeometricalObjects();
            }
            catch (Exception)
            {
                return new List<GeometryObject>();
            }
        }

        // ------------------------------------------------------------------
        // Materiales
        // ------------------------------------------------------------------

        /// <summary>Material "Piel de encofrado - {grupo}" con el color del grupo (se crea o se actualiza).</summary>
        private static ElementId MaterialDe(Document doc, string grupo, Dictionary<string, ElementId> cache, List<string> advertencias)
        {
            if (cache.TryGetValue(grupo, out ElementId id)) return id;

            string nombre = PrefijoMaterial + grupo;
            id = ElementId.InvalidElementId;
            try
            {
                Material material = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material))
                    .Cast<Material>()
                    .FirstOrDefault(m => string.Equals(m.Name, nombre, StringComparison.OrdinalIgnoreCase));
                if (material == null) material = doc.GetElement(Material.Create(doc, nombre)) as Material;
                if (material != null)
                {
                    Color color = GeneradorFiltrosVista.ColoresConcreto.TryGetValue(grupo, out Color c) ? c : ColorPorDefecto;
                    material.Color = color;
                    material.Transparency = 0;
                    material.UseRenderAppearanceForShading = false;
                    ElementId solido = GeneradorFiltrosVista.PatronSolido(doc);
                    if (solido != null)
                    {
                        material.SurfaceForegroundPatternId = solido;
                        material.SurfaceForegroundPatternColor = color;
                    }
                    id = material.Id;
                }
            }
            catch (Exception ex)
            {
                advertencias.Add($"No se pudo crear el material \"{nombre}\": {ex.Message}");
            }
            cache[grupo] = id;
            return id;
        }
    }
}
