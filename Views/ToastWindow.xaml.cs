using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GameModeHub.Core;

namespace GameModeHub.Views
{
    /// <summary>Aviso rápido que NÃO rouba o foco do jogo (WS_EX_NOACTIVATE + ShowActivated=false + clique atravessa).</summary>
    public partial class ToastWindow : Window
    {
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private bool _fechando;

        public ToastWindow(string titulo, string detalhe, string icone)
        {
            InitializeComponent();
            Titulo.Text = titulo ?? "";
            Detalhe.Text = detalhe ?? "";
            Detalhe.Visibility = string.IsNullOrWhiteSpace(detalhe) ? Visibility.Collapsed : Visibility.Visible;
            Icone.Text = string.IsNullOrEmpty(icone) ? "🎮" : icone;

            SourceInitialized += (s, e) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT);
            };
            Loaded += (s, e) =>
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Left + (wa.Width - ActualWidth) / 2;
                Top = wa.Bottom - ActualHeight - 28;
                var dur = new Duration(TimeSpan.FromMilliseconds(220));
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
                Deslize.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(12, 0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                // tempo na tela cresce com o tamanho do texto (2,8 s a 6 s)
                int chars = (titulo?.Length ?? 0) + (detalhe?.Length ?? 0);
                _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(2800 + chars * 35, 2800, 6000));
                _timer.Tick += (a, b) => FecharSuave();
                _timer.Start();
            };
            // CORREÇÃO 4.1: o temporizador continuava vivo depois que o aviso era substituído e chamava Close() de novo
            // numa janela já fechada (erro registrado no log a cada aviso).
            Closed += (s, e) => _timer.Stop();
        }

        private void FecharSuave()
        {
            _timer.Stop();
            if (_fechando) return;
            _fechando = true;
            var a = new DoubleAnimation(Opacity, 0, new Duration(TimeSpan.FromMilliseconds(180)));
            a.Completed += (s, e) => { try { Close(); } catch { } };
            BeginAnimation(OpacityProperty, a);
        }

        private static ToastWindow _atual;

        public static void Mostrar(string titulo, string detalhe = null, string icone = null)
        {
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_atual != null) { var velho = _atual; _atual = null; velho._fechando = true; velho._timer.Stop(); try { velho.Close(); } catch { } }
                    _atual = new ToastWindow(titulo, detalhe, icone);
                    _atual.Closed += (s, e) => { if (ReferenceEquals(_atual, s)) _atual = null; };
                    _atual.Show();
                }
                catch (Exception ex) { Log.Warn("Toast: " + ex.Message); }
            }));
        }
    }
}
