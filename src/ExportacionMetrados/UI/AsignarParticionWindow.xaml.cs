using System.Windows;

namespace ExportacionMetrados.UI
{
    public enum ModoSeleccion { SeleccionActual, ElegirEnPantalla, TodoElModelo }

    public partial class AsignarParticionWindow : Window
    {
        public AsignarParticionWindow(int elementosSeleccionados)
        {
            InitializeComponent();
            if (elementosSeleccionados > 0)
            {
                TxtEstado.Text = $"Hay {elementosSeleccionados} elemento(s) seleccionados. Se asignará la partición a las armaduras seleccionadas y a las alojadas en los elementos seleccionados.";
            }
            else
            {
                TxtEstado.Text = "No hay nada seleccionado. Elija elementos en pantalla o aplique a todo el modelo.";
                RbSeleccion.IsEnabled = false;
                RbElegir.IsChecked = true;
            }
        }

        public ModoSeleccion Modo { get; private set; }
        public string TextoPersonalizado { get; private set; }
        public bool Sobrescribir { get; private set; }

        private void RbTexto_Changed(object sender, RoutedEventArgs e)
        {
            if (TxtPersonalizada == null) return;
            bool propio = RbPersonalizada.IsChecked == true;
            TxtPersonalizada.IsEnabled = propio;
            if (propio) TxtPersonalizada.Focus();
        }

        private void TxtPersonalizada_GotFocus(object sender, RoutedEventArgs e)
        {
            RbPersonalizada.IsChecked = true;
        }

        private void BtnAsignar_Click(object sender, RoutedEventArgs e)
        {
            if (RbPersonalizada.IsChecked == true && string.IsNullOrWhiteSpace(TxtPersonalizada.Text))
            {
                MessageBox.Show(this, "Escriba el texto de la partición.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Modo = RbTodo.IsChecked == true ? ModoSeleccion.TodoElModelo
                 : RbElegir.IsChecked == true ? ModoSeleccion.ElegirEnPantalla
                 : ModoSeleccion.SeleccionActual;
            TextoPersonalizado = RbPersonalizada.IsChecked == true ? TxtPersonalizada.Text.Trim() : null;
            Sobrescribir = ChkSobrescribir.IsChecked == true;
            DialogResult = true;
        }
    }
}
