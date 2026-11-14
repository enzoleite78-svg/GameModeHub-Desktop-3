using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GameModeHub.Views
{
    /// <summary>
    /// NOVO 4.1: caixa que "grava" o atalho. Clique nela e aperte a combinação (ex.: Ctrl+Alt+G).
    /// Backspace ou Esc apagam. Antes era preciso digitar o nome da tecla à mão.
    /// Uso no XAML: &lt;TextBox v:HotkeyBox.Ativo="True"/&gt;
    /// </summary>
    public static class HotkeyBox
    {
        public static readonly DependencyProperty AtivoProperty =
            DependencyProperty.RegisterAttached("Ativo", typeof(bool), typeof(HotkeyBox), new PropertyMetadata(false, AoMudar));

        public static bool GetAtivo(DependencyObject d) => (bool)d.GetValue(AtivoProperty);
        public static void SetAtivo(DependencyObject d, bool v) => d.SetValue(AtivoProperty, v);

        private const string Dica = "Clique e aperte a tecla…";

        private static void AoMudar(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBox tb || !(bool)e.NewValue) return;
            tb.IsReadOnly = true;
            tb.IsReadOnlyCaretVisible = false;
            tb.Cursor = Cursors.Hand;
            tb.IsUndoEnabled = false;
            if (tb.Tag == null) tb.Tag = Dica;
            tb.PreviewKeyDown += Tecla;
            tb.LostKeyboardFocus += (s, a) => tb.Tag = Dica;
            tb.GotKeyboardFocus += (s, a) => tb.Tag = "Aperte a combinação agora…";
        }

        private static void Tecla(object sender, KeyEventArgs e)
        {
            var tb = (TextBox)sender;
            var k = e.Key == Key.System ? e.SystemKey : e.Key;
            var mods = Keyboard.Modifiers;
            if (k == Key.Tab && mods == ModifierKeys.None) return;   // deixa o Tab navegar
            e.Handled = true;
            if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            if ((k == Key.Back || k == Key.Escape) && mods == ModifierKeys.None) { tb.Text = ""; tb.Tag = "Sem atalho"; return; }

            var nome = NomeTecla(k);
            if (nome == null) { tb.Tag = "Essa tecla não pode ser usada"; return; }

            var partes = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) partes.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Alt)) partes.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) partes.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Windows)) partes.Add("Win");

            // letra/número sozinho travaria a digitação no PC inteiro
            bool comum = (k >= Key.A && k <= Key.Z) || (k >= Key.D0 && k <= Key.D9) || k == Key.Space || k == Key.Enter;
            if (partes.Count == 0 && comum) { tb.Text = ""; tb.Tag = "Use Ctrl ou Alt junto com letras/números"; return; }

            partes.Add(nome);
            tb.Text = string.Join("+", partes);
        }

        public static string NomeTecla(Key k)
        {
            if (k >= Key.NumPad0 && k <= Key.NumPad9) return "NumPad" + (k - Key.NumPad0);
            if (k >= Key.F1 && k <= Key.F24) return "F" + (k - Key.F1 + 1);
            if (k >= Key.A && k <= Key.Z) return k.ToString();
            if (k >= Key.D0 && k <= Key.D9) return (k - Key.D0).ToString();
            return k switch
            {
                Key.Home => "Home", Key.End => "End", Key.Insert => "Insert", Key.Pause => "Pause", Key.Delete => "Delete",
                Key.PageUp => "PageUp", Key.PageDown => "PageDown", Key.Scroll => "ScrollLock",
                Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right",
                Key.Space => "Space", Key.Enter => "Enter",
                Key.Multiply => "Multiply", Key.Add => "Add", Key.Subtract => "Subtract", Key.Decimal => "Decimal", Key.Divide => "Divide",
                _ => null
            };
        }
    }
}
