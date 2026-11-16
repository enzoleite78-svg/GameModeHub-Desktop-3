using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GameModeHub.Services;

namespace GameModeHub.Views
{
    /// <summary>Navega a interface com o controle: direcionais movem o foco, A confirma, B volta.</summary>
    public static class GamepadNav
    {
        public static void Tratar(Window w, Botao b, System.Action aoVoltar = null)
        {
            w.Dispatcher.BeginInvoke(new System.Action(() =>
            {
                if (!w.IsActive) return;      // só navega se a janela está em primeiro plano (não interfere no jogo)
                var foco = Keyboard.FocusedElement as FrameworkElement;

                // CORREÇÃO 4.1: lista aberta de um ComboBox (o foco fica no popup, fora da janela)
                if (foco is ComboBoxItem item)
                {
                    var dono = ItemsControl.ItemsControlFromItemContainer(item) as ComboBox;
                    switch (b)
                    {
                        case Botao.Cima: item.MoveFocus(new TraversalRequest(FocusNavigationDirection.Up)); return;
                        case Botao.Baixo: item.MoveFocus(new TraversalRequest(FocusNavigationDirection.Down)); return;
                        case Botao.A: item.IsSelected = true; if (dono != null) { dono.IsDropDownOpen = false; dono.Focus(); } return;
                        case Botao.B: if (dono != null) { dono.IsDropDownOpen = false; dono.Focus(); } return;
                    }
                    return;
                }

                if (foco == null || !w.IsAncestorOf(foco))
                {
                    w.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                    foco = Keyboard.FocusedElement as FrameworkElement;
                    if (b != Botao.A && b != Botao.B) return;
                }

                switch (b)
                {
                    case Botao.Cima: Mover(w, foco, FocusNavigationDirection.Up); break;
                    case Botao.Baixo: Mover(w, foco, FocusNavigationDirection.Down); break;
                    case Botao.Esq:
                        if (foco is Slider s1) s1.Value = System.Math.Max(s1.Minimum, s1.Value - 5);
                        else Mover(w, foco, FocusNavigationDirection.Left);
                        break;
                    case Botao.Dir:
                        if (foco is Slider s2) s2.Value = System.Math.Min(s2.Maximum, s2.Value + 5);
                        else Mover(w, foco, FocusNavigationDirection.Right);
                        break;
                    case Botao.A: Confirmar(foco); break;
                    case Botao.B: aoVoltar?.Invoke(); break;
                }
            }));
        }

        private static void Mover(Window w, FrameworkElement foco, FocusNavigationDirection d)
        {
            var origem = (UIElement)foco ?? w;
            if (!origem.MoveFocus(new TraversalRequest(d)))
                origem.MoveFocus(new TraversalRequest(d == FocusNavigationDirection.Up || d == FocusNavigationDirection.Left ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
            (Keyboard.FocusedElement as FrameworkElement)?.BringIntoView();
        }

        private static void Confirmar(FrameworkElement foco)
        {
            switch (foco)
            {
                case ComboBox cb: cb.IsDropDownOpen = !cb.IsDropDownOpen; break;
                // CORREÇÃO 4.1: o RadioButton marcava mas não disparava o clique (a troca de áudio pelo controle não aplicava)
                case RadioButton rb: rb.IsChecked = true; rb.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
                case CheckBox ck: ck.IsChecked = !(ck.IsChecked ?? false); ck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
                case ButtonBase bt: bt.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
            }
        }
    }
}
