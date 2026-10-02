using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Autodesk.Revit.DB;
using ExportacionMetrados.Core;
using Microsoft.Win32;

namespace ExportacionMetrados.UI
{
    /// <summary>
    /// Ventana para elegir las tablas a exportar, las opciones y el archivo destino.
    /// </summary>
    public partial class SeleccionTablasWindow : Window
    {
        private readonly List<TablaItem> _items;
        private readonly ICollectionView _vista;

        public SeleccionTablasWindow(IEnumerable<ViewSchedule> tablas, ElementId preseleccionada, string nombreSugerido)
        {
            InitializeComponent();

            _items = tablas.Select(t => new TablaItem(t)).ToList();
            foreach (var item in _items)
            {
                item.PropertyChanged += (s, e) => ActualizarResumen();
                if (preseleccionada != null && item.Tabla.Id == preseleccionada)
                {
                    item.Seleccionada = true;
                }
            }

            _vista = CollectionViewSource.GetDefaultView(_items);
            _vista.Filter = FiltrarItem;
            LstTablas.ItemsSource = _vista;

            string carpeta = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            TxtRuta.Text = Path.Combine(carpeta, nombreSugerido ?? "Metrados.xlsx");

            ActualizarResumen();
        }

        /// <summary>Tablas marcadas por el usuario, en el orden de la lista.</summary>
        public List<ViewSchedule> TablasSeleccionadas { get; private set; } = new List<ViewSchedule>();

        /// <summary>Opciones elegidas. Válido solo si ShowDialog devolvió true.</summary>
        public OpcionesExportacion Opciones { get; private set; }

        private bool FiltrarItem(object obj)
        {
            if (!(obj is TablaItem item)) return false;
            string filtro = TxtFiltro.Text?.Trim();
            if (string.IsNullOrEmpty(filtro)) return true;
            return item.Nombre.IndexOf(filtro, StringComparison.CurrentCultureIgnoreCase) >= 0
                || item.Categoria.IndexOf(filtro, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void ActualizarResumen()
        {
            int n = _items.Count(i => i.Seleccionada);
            TxtResumen.Text = n == 0 ? "Ninguna tabla seleccionada"
                            : n == 1 ? "1 tabla seleccionada"
                            : $"{n} tablas seleccionadas";
            BtnExportar.IsEnabled = n > 0;
        }

        private void TxtFiltro_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _vista.Refresh();
        }

        private void BtnTodas_Click(object sender, RoutedEventArgs e)
        {
            // Solo afecta a las tablas visibles con el filtro actual.
            foreach (TablaItem item in _vista) item.Seleccionada = true;
        }

        private void BtnNinguna_Click(object sender, RoutedEventArgs e)
        {
            foreach (TablaItem item in _vista) item.Seleccionada = false;
        }

        private void LstTablas_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstTablas.SelectedItem is TablaItem item)
            {
                item.Seleccionada = !item.Seleccionada;
            }
        }

        private void BtnExaminar_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Guardar metrados como",
                Filter = "Libro de Excel (*.xlsx)|*.xlsx",
                DefaultExt = ".xlsx",
                AddExtension = true,
                OverwritePrompt = true,
            };

            try
            {
                dlg.InitialDirectory = Path.GetDirectoryName(TxtRuta.Text);
                dlg.FileName = Path.GetFileName(TxtRuta.Text);
            }
            catch (ArgumentException) { }

            if (dlg.ShowDialog(this) == true)
            {
                TxtRuta.Text = dlg.FileName;
            }
        }

        private void BtnExportar_Click(object sender, RoutedEventArgs e)
        {
            string ruta = TxtRuta.Text?.Trim();
            if (string.IsNullOrEmpty(ruta))
            {
                MessageBox.Show(this, "Indique el archivo de destino.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!ruta.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                ruta += ".xlsx";
            }

            if (ruta.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                MessageBox.Show(this, "La ruta contiene caracteres no válidos.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (File.Exists(ruta))
            {
                var r = MessageBox.Show(this,
                    "El archivo ya existe. ¿Desea reemplazarlo?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }

            TablasSeleccionadas = _items.Where(i => i.Seleccionada).Select(i => i.Tabla).ToList();
            if (TablasSeleccionadas.Count == 0)
            {
                MessageBox.Show(this, "Seleccione al menos una tabla.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Opciones = new OpcionesExportacion
            {
                RutaArchivo = ruta,
                IncluirTitulo = ChkTitulo.IsChecked == true,
                IncluirEncabezados = ChkEncabezados.IsChecked == true,
                ConvertirNumeros = ChkNumeros.IsChecked == true,
                AplicarFormato = ChkFormato.IsChecked == true,
                AbrirAlTerminar = ChkAbrir.IsChecked == true,
                Sobrescribir = true,
            };

            DialogResult = true;
        }
    }
}
