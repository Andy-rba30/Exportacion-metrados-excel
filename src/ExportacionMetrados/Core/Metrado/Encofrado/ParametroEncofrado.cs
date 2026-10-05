using System;
using System.Collections.Generic;
using Arba.Comun;
using Autodesk.Revit.DB;

namespace ExportacionMetrados.Core.Metrado.Encofrado
{
    /// <summary>
    /// Parámetro compartido de ejemplar "Metrado - Encofrado (m²)" en el que el plugin escribe el
    /// encofrado calculado de cada elemento de concreto, para que las tablas de Revit lo sumen
    /// (Revit no permite crear valores calculados desde la API). No forma parte del contrato
    /// ARBA-comun (ver NOTAS-ARBA-COMUN.md): se define aquí con GUID fijo propio y se crea y
    /// vincula con la misma maquinaria del contrato, en las mismas categorías que
    /// "Metrado - Material".
    /// </summary>
    public static class ParametroEncofrado
    {
        internal static readonly ArbaParam Definicion = new ArbaParam(
            "Encofrado", "Metrado - Encofrado (m²)", "2662EFB0-F102-4EB6-AD03-2D8FA4C6E0AA",
            ArbaParamType.Number, ArbaContract.Material.Categories,
            "Encofrado en m² calculado por el plugin de metrados: caras laterales y fondos según el tipo de elemento, " +
            "descontadas las superficies en contacto con otros elementos de concreto.");

        public static string Nombre => Definicion.Name;

        /// <summary>Asegura el parámetro (definición compartida y vínculo de ejemplar). Dentro de una transacción.</summary>
        public static bool Asegurar(Document doc, List<string> advertencias) => ArbaSharedParams.Ensure(doc, Definicion, advertencias);

        /// <summary>Id del parámetro en el proyecto (campos de tabla), o null si aún no existe.</summary>
        public static ElementId Id(Document doc) => ArbaSharedParams.IdOf(doc, Definicion);

        /// <summary>
        /// Escribe el encofrado total (m², tres decimales) en cada elemento calculado. Devuelve el
        /// número de elementos actualizados. Dentro de una transacción.
        /// </summary>
        public static int Escribir(Document doc, IEnumerable<ElementoEncofrado> elementos, List<string> advertencias)
        {
            int n = 0;
            foreach (ElementoEncofrado m in elementos)
            {
                try
                {
                    Element e = doc.GetElement(m.Id);
                    if (e == null) continue;
                    Parameter p = ArbaSharedParams.Get(e, Definicion);
                    if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) continue;

                    double valor = Math.Round(m.TotalM2, 3);
                    if (Math.Abs(p.AsDouble() - valor) > 0.0005)
                    {
                        p.Set(valor);
                        n++;
                    }
                }
                catch (Exception ex)
                {
                    advertencias.Add($"No se pudo escribir \"{Nombre}\" en el elemento {m.Id}: {ex.Message}");
                }
            }
            return n;
        }
    }
}
