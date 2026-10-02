using System.ComponentModel;
using Autodesk.Revit.DB;

namespace ExportacionMetrados.UI
{
    /// <summary>
    /// Elemento de la lista de tablas en la ventana de selección.
    /// </summary>
    public class TablaItem : INotifyPropertyChanged
    {
        private bool _seleccionada;

        public TablaItem(ViewSchedule tabla)
        {
            Tabla = tabla;
            Nombre = tabla.Name;
            Categoria = ObtenerCategoria(tabla);
            Tipo = tabla.Definition.IsKeySchedule ? "Tabla de claves"
                 : tabla.Definition.IsMaterialTakeoff ? "Cómputo de materiales"
                 : "Tabla de planificación";
        }

        public ViewSchedule Tabla { get; }
        public string Nombre { get; }
        public string Categoria { get; }
        public string Tipo { get; }

        public bool Seleccionada
        {
            get => _seleccionada;
            set
            {
                if (_seleccionada == value) return;
                _seleccionada = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Seleccionada)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private static string ObtenerCategoria(ViewSchedule tabla)
        {
            try
            {
                ElementId catId = tabla.Definition.CategoryId;
                if (catId == null || catId == ElementId.InvalidElementId) return "Multicategoría";
                Category cat = Category.GetCategory(tabla.Document, catId);
                return cat != null ? cat.Name : "—";
            }
            catch
            {
                return "—";
            }
        }
    }
}
